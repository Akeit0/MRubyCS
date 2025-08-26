using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MRubyCS.Internals;
using MRubyCS.StdLib;
using static System.Runtime.CompilerServices.Unsafe;

// ReSharper disable UnreachableSwitchArmDueToIntegerAnalysis

namespace MRubyCS;

partial class MRubyState
{
    public MRubyValue Send(MRubyValue self, Symbol methodId) =>
        Send(self, methodId, ReadOnlySpan<MRubyValue>.Empty);

    public MRubyValue Send(MRubyValue self, Symbol methodId, params ReadOnlySpan<MRubyValue> args) =>
        Send(self, methodId, args, null, null);

    public MRubyValue Send(
        MRubyValue self,
        Symbol methodId,
        RProc block) =>
        Send(self, methodId, ReadOnlySpan<MRubyValue>.Empty, null, block);

    public MRubyValue Send(
        MRubyValue self,
        Symbol methodId,
        ReadOnlySpan<MRubyValue> args,
        ReadOnlySpan<KeyValuePair<Symbol, MRubyValue>> kargs,
        RProc? block)
    {
        ref var currentCallInfo = ref Context.CurrentCallInfo;
        var nextStackPointer = currentCallInfo.StackPointer + currentCallInfo.NumberOfRegisters;

        var stackSize = MRubyCallInfo.CalculateBlockArgumentOffset(
            args.Length,
            kargs.IsEmpty ? 0 : MRubyCallInfo.CallMaxArgs) + 1; // argc + kargs(packed) + self + proc
        Context.ExtendStack(nextStackPointer + stackSize);

        var nextStack = Context.Stack.AsSpan(nextStackPointer);

        var receiverClass = ClassOf(self);
        ref var nextCallInfo = ref Context.PushCallStack();
        nextCallInfo.StackPointer = nextStackPointer;
        nextCallInfo.Scope = receiverClass;
        nextCallInfo.ArgumentCount = (byte)args.Length;
        nextCallInfo.KeywordArgumentCount = (byte)kargs.Length;

        nextStack[0] = self;
        if (!args.IsEmpty)
        {
            // packing
            if (args.Length >= MRubyCallInfo.CallMaxArgs)
            {
                throw new NotImplementedException();
            }
            else
            {
                args.CopyTo(nextStack[1..]);
            }
        }

        if (!kargs.IsEmpty)
        {
            var kargOffset = MRubyCallInfo.CalculateKeywordArgumentOffset(args.Length, kargs.Length);
            // packing
            var kdict = NewHash(kargs.Length);
            foreach (var (key, value) in kargs)
            {
                kdict.Add(MRubyValue.From(key), value);
            }

            nextStack[kargOffset] = MRubyValue.From(kdict);
            nextCallInfo.MarkAsKeywordArgumentPacked();
        }

        nextStack[stackSize - 1] = block != null ? MRubyValue.From(block) : default;

        if (TryFindMethod(receiverClass, methodId, out var method, out _) &&
            method != MRubyMethod.Undef)
        {
            nextCallInfo.MethodId = methodId;
        }
        else
        {
            method = PrepareMethodMissing(ref nextCallInfo, self, methodId);
        }

        nextCallInfo.Proc = method.Proc;

        // var block = stack[blockArgumentOffset];
        // if (!block.IsNil) EnsureValueIsBlock(block);

        if (method.Kind == MRubyMethodKind.CSharpFunc)
        {
            nextCallInfo.CallerType = CallerType.MethodCalled;
            nextCallInfo.ProgramCounter = 0;

            var result = method.Invoke(this, self);
            Context.PopCallStack();
            return result;
        }
        else
        {
            var irepProc = nextCallInfo.Proc!;
            nextCallInfo.CallerType = CallerType.VmExecuted;
            nextCallInfo.ProgramCounter = irepProc.ProgramCounter;
            return Execute(irepProc.Irep, irepProc.ProgramCounter, nextCallInfo.BlockArgumentOffset + 1);
        }
    }

    public MRubyValue YieldWithClass(
        RClass c,
        MRubyValue self,
        ReadOnlySpan<MRubyValue> args,
        RProc block)
    {
        ref var callInfo = ref Context.CurrentCallInfo;

        var stackSize = callInfo.NumberOfRegisters;
        ref var nextCallInfo = ref Context.PushCallStack();
        nextCallInfo.StackPointer = callInfo.StackPointer + stackSize;
        nextCallInfo.CallerType = CallerType.VmExecuted;
        nextCallInfo.MethodId = block.Scope is REnv env
            ? env.MethodId
            : callInfo.MethodId;
        nextCallInfo.Proc = block;
        nextCallInfo.Scope = c;

        var nextStack = Context.Stack.AsSpan(nextCallInfo.StackPointer);
        nextStack[0] = self;

        if (args.Length >= MRubyCallInfo.CallMaxArgs)
        {
            // TODO: packing
            throw new NotImplementedException();
        }
        else
        {
            args.CopyTo(nextStack[1..]);
            nextCallInfo.ArgumentCount = (byte)args.Length;
        }

        nextCallInfo.KeywordArgumentCount = 0;

        return Execute(block.Irep, block.ProgramCounter, nextCallInfo.BlockArgumentOffset + 1);
    }

    public RProc CreateProc(Irep irep)
    {
        return new RProc(irep, 0, ProcClass)
        {
            Upper = null,
            Scope = ObjectClass
        };
    }

    public Irep ParseBytecode(ReadOnlySpan<byte> bytecode) => RiteParser.Parse(bytecode);

    public MRubyValue LoadBytecode(ReadOnlySpan<byte> bytecode)
    {
        var irep = RiteParser.Parse(bytecode);
        return Execute(irep);
    }

    public MRubyValue LoadBytecodeFile(string filePath)
    {
        var bytecode = File.ReadAllBytes(filePath);
        return LoadBytecode(bytecode);
    }

    public async Task<MRubyValue> LoadBytecodeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var bytecode = await File.ReadAllBytesAsync(filePath, cancellationToken);
        return LoadBytecode(bytecode);
    }

    public MRubyValue Execute(Irep irep)
    {
        var proc = new RProc(irep, 0, ProcClass)
        {
            Upper = null,
            Scope = ObjectClass
        };

        Context.UnwindStack();

        ref var callInfo = ref Context.CurrentCallInfo;
        callInfo.StackPointer = 0;
        callInfo.Proc = proc;
        callInfo.Scope = ObjectClass;
        callInfo.MethodId = default;
        callInfo.CallerType = CallerType.InVmLoop;
        Context.Stack[0] = MRubyValue.From(TopSelf);
        return Execute(irep, 0, 1);
    }

    public string GetBacktraceString()
    {
        var backtrace = Backtrace.Capture(Context);
        return backtrace.ToString(this);
    }

    internal bool CheckProcIsOrphan(RProc proc) =>
        Context.CheckProcIsOrphan(proc);

    internal MRubyValue SendMeta(MRubyValue self)
    {
        ref var callInfo = ref Context.CurrentCallInfo;

        var argc = GetArgumentCount();
        if (argc <= 0)
        {
            RaiseArgumentNumberError(argc, 1, 255);
        }

        var methodId = GetArgumentAsSymbolAt(0);
        if (callInfo.CallerType != CallerType.InVmLoop)
        {
            var block = GetBlockArgument();
            var args = GetRestArgumentsAfter(1);
            var kargs = GetKeywordArguments();
            return Send(self, methodId, args, kargs, block);
        }

        var registers = Context.Stack.AsSpan(callInfo.StackPointer + 1);
        var receiverClass = ClassOf(self);

        if (TryFindMethod(receiverClass, methodId, out var method, out receiverClass))
        {
            callInfo.MethodId = methodId;
            callInfo.Scope = receiverClass;
        }
        else
        {
            method = PrepareMethodMissing(ref callInfo, self, methodId);
        }

        if (callInfo.ArgumentPacked)
        {
            var packedArgv = registers[0].As<RArray>();
            registers[0] = MRubyValue.From(packedArgv.SubSequence(1, packedArgv.Length - 1));
        }
        else
        {
            registers[1..].CopyTo(registers); // copy args
            registers[callInfo.ArgumentCount] = registers[callInfo.ArgumentCount + 1]; // copy kargs or blocka
            if (callInfo.KeywordArgumentCount > 0)
            {
                registers[callInfo.ArgumentCount + 1] = registers[callInfo.ArgumentCount + 2]; // copy block
            }
            callInfo.ArgumentCount--; // remove
        }

        // var block = stack[blockArgumentOffset];
        // if (!block.IsNil) EnsureValueIsBlock(block);

        if (method.Kind == MRubyMethodKind.CSharpFunc)
        {
            callInfo.CallerType = CallerType.MethodCalled;
            callInfo.ProgramCounter = 0;

            return method.Invoke(this, self);
        }
        else
        {
            callInfo.CallerType = CallerType.VmExecuted;
            callInfo.Proc = method.Proc;
            callInfo.ProgramCounter = method.Proc!.ProgramCounter;

            var nregs = method.Proc.Irep.RegisterVariableCount;
            var keep = callInfo.BlockArgumentOffset + 1;
            if (nregs > keep)
            {
                Context.ExtendStack(callInfo.StackPointer + nregs);
                Context.ClearStack(callInfo.StackPointer + keep, nregs - keep);
            }

            // dummy. pop after `__send__` called.
            ref var nextCallInfo = ref Context.PushCallStack();
            nextCallInfo.MethodId = default;
            nextCallInfo.Proc = null;
            nextCallInfo.StackPointer = callInfo.StackPointer;
            callInfo.CallerType = CallerType.InVmLoop;
            callInfo.Scope = receiverClass;

            return self;
        }
    }

    internal MRubyValue EvalUnder(MRubyValue self, RProc block, RClass c)
    {
        ref var callInfo = ref Context.CurrentCallInfo;
        if (callInfo.CallerType == CallerType.MethodCalled)
        {
            return YieldWithClass(c, self, [self], block);
        }

        callInfo.Scope = c;
        callInfo.Proc = block;
        callInfo.ProgramCounter = block.ProgramCounter;
        callInfo.ArgumentCount = 0;
        callInfo.KeywordArgumentCount = 0;
        callInfo.MethodId = Context.CallStack[Context.CallDepth - 1].MethodId;

        var nregs = block.Irep.RegisterVariableCount < 4 ? 4 : block.Irep.RegisterVariableCount;
        Context.ExtendStack(nregs);
        Context.Stack[callInfo.StackPointer] = self;
        Context.Stack[callInfo.StackPointer + 1] = self;
        Context.ClearStack(callInfo.StackPointer + 2, nregs - 2);

        // Popped at the end of an upstream method call such as instance_eval/class_eval, and the above rewritten callInfo is executed.
        Context.PushCallStack();
        return self;
    }

    /// <summary>
    /// Execute irep assuming the Stack values are placed
    /// </summary>
    internal unsafe MRubyValue Execute(Irep irep, int pc, int stackKeep)
    {
        Exception = null;

        var registerVariableCount = irep.RegisterVariableCount;
        if (stackKeep > registerVariableCount)
        {
            registerVariableCount = (ushort)stackKeep;
        }
        // else
        // {
        //     if (context.CurrentCallInfo.Scope is REnv env &&
        //         (stackKeep == 0 || irep.LocalVariablesBackingField.Length < env.Stack.Length))
        //     {
        //         context.CurrentCallInfo.Scope = null!;
        //         env.CaptureStack();
        //     }
        // }

        Span<byte> sequence = irep.SequenceBackingField;

        Span<Symbol> symbols = irep.SymbolsBackingField;

        ref var callInfo = ref Context.CurrentCallInfo;
        Context.ExtendStack(callInfo.StackPointer + registerVariableCount);
        Context.ClearStack(callInfo.StackPointer + stackKeep, registerVariableCount - stackKeep);

        ref var seq0 = ref MemoryMarshal.GetReference(sequence);
        var registers = Context.Stack.AsSpan(callInfo.StackPointer);
        ref var register0 = ref MemoryMarshal.GetReference(registers);
        ref var symbol0 = ref MemoryMarshal.GetReference(symbols);
        callInfo.ProgramCounter = pc;

        while (true)
        {
            try
            {
            Next:
                ref var seqRef = ref Add(ref seq0, callInfo.ProgramCounter);
                var opcode = (OpCode)seqRef;
                var a = Add(ref seqRef, 1);
                // var b = Unsafe.Add(ref seqRef, 2)
                ref var registerA = ref a < registers.Length ? ref Add(ref register0, a) : ref NullRef<MRubyValue>();

                switch ((OpCode)seqRef)
                {
                    case OpCode.Nop:
                        Markers.Nop();
                    {
                        callInfo.ProgramCounter++;
                        goto Next;
                    }
                    case OpCode.Move:
                        Markers.Move();
                        // OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        registerA = Add(ref register0, Add(ref seqRef, 2));
                        goto Next;
                    case OpCode.LoadL:
                        Markers.LoadL();
                        // OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        registerA = irep.PoolValuesBackingField[Add(ref seqRef, 2)];
                        goto Next;
                    case OpCode.LoadI8:
                    case OpCode.LoadINeg:
                        Markers.LoadI8();
                        Markers.LoadINeg();
                        // OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        registerA = MRubyValue.From(Add(ref seqRef, 2) * (opcode == OpCode.LoadI8 ? 1 : -1));
                        goto Next;
                    case OpCode.LoadI__1:
                    case OpCode.LoadI_0:
                    case OpCode.LoadI_1:
                    case OpCode.LoadI_2:
                    case OpCode.LoadI_3:
                    case OpCode.LoadI_4:
                    case OpCode.LoadI_5:
                    case OpCode.LoadI_6:
                    case OpCode.LoadI_7:
                        Markers.LoadI__1();
                        // OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA = MRubyValue.From((int)opcode - (int)OpCode.LoadI_0);
                        goto Next;
                    case OpCode.LoadI16:
                        Markers.LoadI16();
                        // OperandBS bs;
                        callInfo.ProgramCounter += 4;
                        var b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 2));
                        registerA = MRubyValue.From((b2.Bytes[0] << 8) | b2.Bytes[1]);
                        goto Next;
                    case OpCode.LoadI32:
                        Markers.LoadI32();
                        // OperandBSS bss;
                        callInfo.ProgramCounter += 6;
                        var b4 = ReadUnaligned<Byte4>(ref Add(ref seqRef, 2));
                        registerA = MRubyValue.From((b4.Bytes[0] << 24) | (b4.Bytes[1] << 16) | (b4.Bytes[2] << 8) | b4.Bytes[3]);
                        goto Next;
                    case OpCode.LoadSym:
                        Markers.LoadSym();
                        // OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        registerA = MRubyValue.From(Add(ref symbol0, Add(ref seqRef, 2)));
                        goto Next;
                    case OpCode.LoadNil:
                    case OpCode.LoadSelf:
                        Markers.LoadNil();
                        Markers.LoadSelf();
                        // OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA = opcode == OpCode.LoadNil ? default : register0;
                        goto Next;
                    case OpCode.LoadT:
                    case OpCode.LoadF:
                        Markers.LoadT();
                        Markers.LoadF();
                        // OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA = MRubyValue.From(opcode == OpCode.LoadT);
                        goto Next;
                    case OpCode.GetGV:
                        Markers.GetGV();
                        // OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        registerA = globalVariables.Get(Add(ref symbol0, Add(ref seqRef, 2)));
                        goto Next;
                    case OpCode.SetGV:
                        Markers.SetGV();
                        // OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        globalVariables.Set(Add(ref symbol0, Add(ref seqRef, 2)), registerA);
                        goto Next;
                    case OpCode.GetSV:
                    case OpCode.SetSV:
                        Markers.GetSV();
                        callInfo.ProgramCounter += 3;
                        goto Next;
                    case OpCode.GetIV:
                    case OpCode.SetIV:
                        Markers.GetIV();
                        Markers.SetIV();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var variableTable = register0.As<RObject>().InstanceVariables;
                        var symbol = Add(ref symbol0, Add(ref seqRef, 2));
                        if (opcode == OpCode.GetIV)
                        {
                            registerA = variableTable.Get(symbol);
                        }
                        else
                        {
                            variableTable.Set(symbol, registerA);
                        }
                        goto Next;
                    case OpCode.GetCV:
                        Markers.GetCV();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        registerA = GetClassVariable(Add(ref symbol0, Add(ref seqRef, 2)));
                        goto Next;
                    case OpCode.SetCV:
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        SetClassVariable(Add(ref symbol0, Add(ref seqRef, 2)), registerA);
                        goto Next;
                    case OpCode.GetConst:
                        Markers.GetConst();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                    {
                        var id = Add(ref symbol0, Add(ref seqRef, 2));
                        var c = callInfo.Proc?.ScopeBackingField?.TargetClass ?? ObjectClass;
                        if (c.ClassInstanceVariables.TryGet(id, out var value))
                        {
                            registerA = value;
                            goto Next;
                        }

                        GetConstSlowPath(
                            this, ref registerA, ref callInfo, id, c);

                        goto Next;

                        static void GetConstSlowPath(MRubyState state, ref MRubyValue registerA, ref MRubyCallInfo callInfo, Symbol id, RClass c)
                        {
                            var x = c;
                            MRubyValue value;
                            while (x is { VType: MRubyVType.SClass })
                            {
                                if (!x.ClassInstanceVariables.TryGet(id, out value))
                                {
                                    x = null;
                                    break;
                                }
                                x = c.Class;
                            }
                            if (x is { VType: MRubyVType.Class or MRubyVType.Module })
                            {
                                c = x;
                            }
                            var proc = callInfo.Proc?.Upper;
                            while (proc != null)
                            {
                                x = proc.Scope?.TargetClass ?? state.ObjectClass;
                                if (x.ClassInstanceVariables.TryGet(id, out value))
                                {
                                    registerA = value;
                                    return;
                                }
                                proc = proc.Upper;
                            }
                            registerA = state.GetConst(id, c);
                        }
                    }
                    case OpCode.SetConst:
                    {
                        Markers.SetConst();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        //var id = Unsafe.Add(ref symbol0,bb.B];
                        var c = callInfo.Proc?.ScopeBackingField?.TargetClass ?? ObjectClass;
                        SetConst(Add(ref symbol0, Add(ref seqRef, 2)), c, registerA);
                        goto Next;
                    }
                    case OpCode.GetMCnst:
                        Markers.GetMCnst();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                    {
                        //var mod = registers[bb.A];
                        var name = Add(ref symbol0, Add(ref seqRef, 2));
                        registerA = GetConst(name, registerA.As<RClass>());
                        goto Next;
                    }
                    case OpCode.SetMCnst:
                    {
                        Markers.SetMCnst();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        //var mod = registers[bb.A + 1];
                        var name = Add(ref symbol0, Add(ref seqRef, 2));
                        SetConst(name, Add(ref registerA, 1).As<RClass>(), registerA);
                        goto Next;
                    }
                    case OpCode.GetIdx:
                    {
                        Markers.GetIdx();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        var valueB = Add(ref registerA, 1);
                        switch (registerA.Object)
                        {
                            case RArray array when valueB.IsInteger:
                                registerA = array[(int)valueB.Bits];
                                goto Next;
                            case RHash hash:
                                registerA = hash.GetValueOrDefault(valueB, this);
                                goto Next;
                            case RString str:
                                switch (valueB.VType)
                                {
                                    case MRubyVType.Integer:
                                    case MRubyVType.String:
                                    case MRubyVType.Range:
                                        var substr = str.GetPartial(this, valueB);
                                        registerA = substr != null
                                            ? MRubyValue.From(substr)
                                            : default;
                                        goto Next;
                                }
                                break;
                        }

                        // Jump to send :[]
                        Add(ref registerA, 2) = default; // push nil after arguments
                        callInfo = ref GetNextCallInfo(callInfo.StackPointer + a, opcode, 1);
                        goto case OpCode.SendInternal;
                    }
                    case OpCode.SetIdx:
                    {
                        Markers.SetIdx();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        Add(ref registerA, 3) = default; // push nil after arguments

                        // Jump to send :[]=
                        var nextStackPointer = callInfo.StackPointer + a;
                        callInfo = ref Context.PushCallStack();
                        callInfo.CallerType = CallerType.InVmLoop;
                        callInfo.StackPointer = nextStackPointer;
                        callInfo.MethodId = Names.OpAset;
                        callInfo.ArgumentCount = 2;
                        callInfo.KeywordArgumentCount = 0;
                        goto case OpCode.SendInternal;
                    }
                    case OpCode.GetUpVar:
                    case OpCode.SetUpVar:
                        Markers.GetUpVar();
                        Markers.SetUpVar();
                    {
                        //OperandBBB bbb;
                        callInfo.ProgramCounter += 4;
                        b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 2));
                        var env = callInfo.Proc?.FindUpperEnvTo(b2.Bytes[1]);
                        var envStack = env == null ? default : env.Stack;
                        if (b2.Bytes[0] < envStack.Length)
                        {
                            if (opcode == OpCode.GetUpVar)
                            {
                                registerA = envStack[b2.Bytes[0]];
                            }
                            else
                            {
                                envStack[b2.Bytes[0]] = registerA;
                            }
                        }
                        else if (opcode == OpCode.GetUpVar)
                        {
                            registerA = default;
                        }
                        goto Next;
                    }
                    case OpCode.Jmp:
                        Markers.Jmp();
                        //OperandS s;
                        callInfo.ProgramCounter += 3;
                        b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 1));
                        callInfo.ProgramCounter += (short)((b2.Bytes[0] << 8) | b2.Bytes[1]);
                        goto Next;
                    case OpCode.JmpIf:
                        Markers.JmpIf();
                        //OperandBS bs;
                        callInfo.ProgramCounter += 4;
                        if (1 < As<MRubyValue, nuint>(ref registerA))
                        {
                            b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 2));
                            callInfo.ProgramCounter += (short)((b2.Bytes[0] << 8) | b2.Bytes[1]);
                        }
                        goto Next;
                    case OpCode.JmpNot:
                    case OpCode.JmpNil:
                        Markers.JmpNot();
                        Markers.JmpNil();
                        //OperandBS bs;
                        callInfo.ProgramCounter += 4;
                        if (As<MRubyValue, nuint>(ref registerA) <= ((nuint)OpCode.JmpNil - (nuint)opcode))
                        {
                            b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 2));
                            callInfo.ProgramCounter += (short)((b2.Bytes[0] << 8) | b2.Bytes[1]);
                        }
                        goto Next;
                    case OpCode.JmpUw:
                    {
                        Markers.JmpUw();
                        //OperandS s;
                        callInfo.ProgramCounter += 3;
                        b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 1));
                        var newProgramCounter = callInfo.ProgramCounter + (short)((b2.Bytes[0] << 8) | b2.Bytes[1]);

                        if (irep.TryFindCatchHandler(callInfo.ProgramCounter, CatchHandlerType.Ensure, out var catchHandler))
                        {
                            // avoiding a jump from a catch handler into the same handler
                            if (newProgramCounter < catchHandler.Begin ||
                                newProgramCounter > catchHandler.End)
                            {
                                PrepareTaggedBreak(BreakTag.Jump, Context.CallDepth, MRubyValue.From(newProgramCounter));
                                callInfo.ProgramCounter = (int)catchHandler.Target;
                                goto Next;
                            }
                        }
                        Exception = null;
                        callInfo.ProgramCounter = newProgramCounter;
                        goto Next;
                    }
                    case OpCode.Except:
                        Markers.Except();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA = Exception switch
                        {
                            MRubyRaiseException x => MRubyValue.From(x.ExceptionObject),
                            MRubyBreakException x => MRubyValue.From(x.BreakObject),
                            _ => default
                        };
                        Exception = null;
                        goto Next;
                    case OpCode.Rescue:
                    {
                        Markers.Rescue();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        //var exceptionObjectValue = registerA;
                        //exceptionClassValue
                        ref var registerB = ref Add(ref register0, Add(ref seqRef, 2));
                        switch (registerB.VType)
                        {
                            case MRubyVType.Class:
                            case MRubyVType.Module:
                                break;
                            default:
                                Exception = CreateNotClassOrModuleError(this);
                                if (TryRaiseJump(ref callInfo))
                                {
                                    goto JumpAndNext;
                                }
                                throw Exception;

                                static MRubyLongJumpException CreateNotClassOrModuleError(MRubyState state) =>
                                    new MRubyRaiseException(state, new RException(
                                        state.NewString("class or module required for rescue clause"u8),
                                        state.GetExceptionClass(Names.TypeError)), state.Context.CallDepth);
                        }

                        registerB = MRubyValue.From(KindOf(registerA, registerB.As<RClass>()));
                        goto Next;
                    }
                    case OpCode.RaiseIf:
                    {
                        Markers.RaiseIf();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;

                        //var exceptionValue = registers[a];
                        switch (registerA.Object)
                        {
                            case RBreak breakObject:
                                Exception = new MRubyBreakException(this, breakObject);
                                switch (breakObject.Tag)
                                {
                                    case BreakTag.Break:
                                    {
                                        if (TryReturnJump(ref callInfo, breakObject.BreakIndex, breakObject.Value))
                                        {
                                            goto JumpAndNext;
                                        }
                                        return breakObject.Value;
                                    }
                                    case BreakTag.Jump:
                                    {
                                        var newProgramCounter = (int)breakObject.Value.Bits;
                                        if (irep.TryFindCatchHandler(callInfo.ProgramCounter, CatchHandlerType.Ensure, out var catchHandler))
                                        {
                                            // avoiding a jump from a catch handler into the same handler
                                            if (newProgramCounter < catchHandler.Begin || newProgramCounter > catchHandler.End)
                                            {
                                                PrepareTaggedBreak(BreakTag.Jump, Context.CallDepth, MRubyValue.From(newProgramCounter));
                                                callInfo.ProgramCounter = (int)catchHandler.Target;
                                                goto Next;
                                            }
                                        }
                                        Exception = null;
                                        callInfo.ProgramCounter = newProgramCounter;
                                        goto Next;
                                    }
                                    case BreakTag.Stop:
                                    {
                                        if (TryUnwindEnsureJump(ref callInfo, Context.CallDepth, BreakTag.Stop, breakObject.Value))
                                        {
                                            goto JumpAndNext;
                                        }
                                        if (Exception != null) throw Exception;
                                        return Add(ref register0, irep.LocalVariablesBackingField.Length);
                                    }
                                }
                                break;
                            case RException exceptionObject:
                                Exception = new MRubyRaiseException(this, exceptionObject, Context.CallDepth);
                                if (TryRaiseJump(ref callInfo))
                                {
                                    goto JumpAndNext;
                                }
                                throw Exception;
                            default:
                                Exception = null;
                                break;
                        }
                        goto Next;
                    }
                    case OpCode.SSend:
                    case OpCode.SSendB:
                    case OpCode.Send:
                    case OpCode.SendB:
                        OperandBBB bbb;
                    {
                        Markers.SSend();
                        //OperandBBB bbb;
                        callInfo.ProgramCounter += 4;
                        bbb = ReadUnaligned<OperandBBB>(ref Add(ref seqRef, 1));
                        var currentStackPointer = callInfo.StackPointer;

                        callInfo = ref Context.PushCallStack();
                        callInfo.CallerType = CallerType.InVmLoop;
                        callInfo.StackPointer = currentStackPointer + bbb.A;
                        callInfo.MethodId = Add(ref symbol0, bbb.B);
                        callInfo.ArgumentCount = (byte)(bbb.C & 0xf);
                        callInfo.KeywordArgumentCount = (byte)((bbb.C >> 4) & 0xf);

                        var nextRegisters = Context.Stack.AsSpan(callInfo.StackPointer);
                        var blockOffset = callInfo.BlockArgumentOffset;

                        if (callInfo.KeywordArgumentCount > 0)
                        {
                            var kargOffset = callInfo.KeywordArgumentOffset;
                            if (callInfo.KeywordArgumentPacked)
                            {
                                var kdict = nextRegisters[kargOffset];
                                EnsureValueType(kdict, MRubyVType.Hash);
                            }
                            else
                            {
                                PackKeywordArguments(this, ref callInfo, nextRegisters, kargOffset, ref blockOffset);

                                static void PackKeywordArguments(MRubyState state, ref MRubyCallInfo callInfo, Span<MRubyValue> nextRegisters, int kargOffset, ref int blockOffset)
                                {
                                    var hash = state.NewHash(callInfo.KeywordArgumentCount);
                                    for (var i = 0; i < callInfo.KeywordArgumentCount; i++)
                                    {
                                        var k = nextRegisters[kargOffset + (i * 2)];
                                        var v = nextRegisters[kargOffset + (i * 2) + 1];
                                        hash.Add(k, v);
                                    }
                                    nextRegisters[kargOffset] = MRubyValue.From(hash);

                                    var block = nextRegisters[blockOffset];
                                    callInfo.MarkAsKeywordArgumentPacked();
                                    blockOffset = callInfo.BlockArgumentOffset;
                                    nextRegisters[blockOffset] = block;
                                }
                            }
                        }

                        if (opcode is OpCode.Send or OpCode.SSend)
                        {
                            nextRegisters[blockOffset] = default;
                        }
                        else
                        {
                            var block = nextRegisters[blockOffset];
                            if (!block.IsNil) EnsureValueIsBlock(block);
                        }

                        // self send
                        if (opcode is OpCode.SSend or OpCode.SSendB)
                        {
                            nextRegisters[0] = register0;
                        }
                        goto case OpCode.SendInternal;
                    }
                    case OpCode.SendInternal:
                    {
                        Markers.SendInternal();
                        var self = Context.Stack[callInfo.StackPointer];
                        var receiverClass = opcode == OpCode.Super
                            ? (RClass)callInfo.Scope // set RClass.Super in OpCode.Super
                            : ClassOf(self);
                        var methodId = callInfo.MethodId;
                        if (!TryFindMethod(receiverClass, methodId, out var method, out receiverClass) ||
                            method == MRubyMethod.Undef)
                        {
                            method = PrepareMethodMissing(ref callInfo, self, methodId,
                                opcode == OpCode.Super
                                    ? static (state, self, methodId) => state.Raise(Names.NoMethodError, $"no superclass method '{state.NameOf(methodId)}' for {state.StringifyAny(self)}")
                                    : null);
                        }

                        callInfo.Scope = receiverClass;
                        callInfo.Proc = method.Proc;

                        // var block = stack[blockArgumentOffset];
                        // if (!block.IsNil) EnsureValueIsBlock(block);

                        if (method.Kind == MRubyMethodKind.CSharpFunc)
                        {
                            if (CallCSharpFunc(this, method, self, ref irep, ref sequence, ref registers, ref symbols, out var result))
                            {
                                return result;
                            }

                            callInfo = ref Context.CurrentCallInfo;
                            seq0 = ref MemoryMarshal.GetReference(sequence);
                            register0 = ref MemoryMarshal.GetReference(registers);
                            symbol0 = ref MemoryMarshal.GetReference(symbols);
                            goto Next;

                            static bool CallCSharpFunc(MRubyState state, MRubyMethod method, MRubyValue self, ref Irep irep, ref Span<byte> sequence, ref Span<MRubyValue> registers, ref Span<Symbol> symbols, out MRubyValue result)
                            {
                                result = method.Invoke(state, self);

                                ref var callInfo = ref state.Context.CurrentCallInfo;
                                var keepContext = callInfo.KeepContext;
                                var callerType = callInfo.CallerType;

                                state.Context.Stack[callInfo.StackPointer] = result;

                                // return from context modifying method (resume/yield)
                                if (!keepContext)
                                {
                                    if (callerType == CallerType.Resumed)
                                    {
                                        return true;
                                    }
                                }

                                state.Context.PopCallStack();
                                callInfo = ref state.Context.CurrentCallInfo;
                                irep = callInfo.Proc!.IrepBackingField;
                                registers = state.Context.Stack.AsSpan(callInfo.StackPointer);
                                sequence = irep.SequenceBackingField.AsSpan();
                                symbols = irep.SymbolsBackingField;
                                return false;
                            }
                        }

                        var irepProc = callInfo.Proc;
                        irep = irepProc!.IrepBackingField;
                        callInfo.ProgramCounter = irepProc.ProgramCounter;
                        var nregs = irep.RegisterVariableCountBackingField;
                        Context.ExtendStack(callInfo.StackPointer + (nregs < 4 ? 4 : nregs) + 1);
                        registers = Context.Stack.AsSpan(callInfo.StackPointer);
                        sequence = irep.SequenceBackingField;
                        symbols = irep.SymbolsBackingField;
                        seq0 = ref MemoryMarshal.GetReference(sequence);
                        register0 = ref MemoryMarshal.GetReference(registers);
                        symbol0 = ref MemoryMarshal.GetReference(symbols);
                        goto Next;
                        // pop on OpCode.Return
                    }
                    case OpCode.Call: // modify program counter
                    {
                        registers = Call(this, out irep, ref callInfo, registers, out sequence, out symbols);
                        seq0 = ref MemoryMarshal.GetReference(sequence);
                        register0 = ref MemoryMarshal.GetReference(registers);
                        symbol0 = ref MemoryMarshal.GetReference(symbols);
                        goto Next;

                        static Span<MRubyValue> Call(MRubyState state, out Irep irep, ref MRubyCallInfo callInfo, Span<MRubyValue> registers, out Span<byte> sequence, out Span<Symbol> symbols)
                        {
                            callInfo.ProgramCounter += 1; // read opcode
                            var receiver = registers[0];
                            var proc = receiver.As<RProc>();

                            // replace callinfo
                            callInfo.Scope = proc.Scope!.TargetClass;
                            callInfo.Proc = proc;

                            // setup environment for calling method
                            irep = proc.IrepBackingField;
                            sequence = irep.SequenceBackingField.AsSpan();
                            symbols = irep.SymbolsBackingField;
                            callInfo.ProgramCounter = proc.ProgramCounter;

                            var currentSize = callInfo.BlockArgumentOffset + 1;
                            if (currentSize < irep.RegisterVariableCount)
                            {
                                state.Context.ExtendStack(callInfo.StackPointer + irep.RegisterVariableCount);
                                state.Context.ClearStack(
                                    callInfo.StackPointer + currentSize,
                                    irep.RegisterVariableCount - currentSize);
                            }
                            registers = state.Context.Stack.AsSpan(callInfo.StackPointer);
                            if (proc.Scope is REnv env)
                            {
                                callInfo.MethodId = env.MethodId;
                                registers[0] = env.Stack[0];
                            }
                            return registers;
                        }
                    }
                    case OpCode.Super:
                    {
                        Markers.Super();

                        Super(this, ref callInfo, registers, sequence);
                        callInfo = ref Context.CurrentCallInfo;
                        goto case OpCode.SendInternal;

                        static void Super(MRubyState state, ref MRubyCallInfo callInfo, Span<MRubyValue> registers, ReadOnlySpan<byte> sequence)
                        {
                            var bb = OperandBB.Read(sequence, ref callInfo.ProgramCounter);
                            var targetClass = callInfo.Scope.TargetClass;
                            var methodId = callInfo.MethodId;
                            if (methodId == default || targetClass == null!)
                            {
                                state.Raise(Names.NoMethodError, "super called outside of method"u8);
                            }

                            var receiver = registers[0];
                            if (targetClass!.HasFlag(MRubyObjectFlags.ClassPrepended) ||
                                targetClass.VType == MRubyVType.Module ||
                                !state.KindOf(receiver, targetClass))
                            {
                                state.Raise(Names.TypeError, "self has wrong type to call super in this context"u8);
                            }

                            registers[bb.A] = receiver;

                            // Jump to send
                            var nextStackPointer = callInfo.StackPointer + bb.A;
                            callInfo = ref state.Context.PushCallStack();
                            callInfo.CallerType = CallerType.InVmLoop;
                            callInfo.Scope = targetClass.Super;
                            callInfo.StackPointer = nextStackPointer;
                            callInfo.MethodId = methodId;
                            callInfo.ArgumentCount = (byte)(bb.B & 0xf);
                            callInfo.KeywordArgumentCount = (byte)((bb.B >> 4) & 0xf);
                        }
                    }
                    case OpCode.Enter:
                    {
                        Markers.Enter();
                        callInfo.ProgramCounter += 4;
                        bbb = ReadUnaligned<OperandBBB>(ref Add(ref seqRef, 1));
                        var bits = (uint)bbb.A << 16 | (uint)bbb.B << 8 | bbb.C;

                        // fast pass
                        if ((bits & ~0b11111000000000000000001) == 0 && // no other arg
                            callInfo.ArgumentCount < MRubyCallInfo.CallMaxArgs && // not packed
                            callInfo.Proc is not null && (callInfo.Proc.RawFlags & MRubyObjectFlags.ProcStrict) != 0)
                        {
                            // FastPass(this, irep, ref callInfo, bits);
                            var m1 = (byte)((bits >> 18) & 0x1f);
                            var argc = callInfo.ArgumentCount;
                            var actualArgc = argc + (callInfo.KeywordArgumentCount >= MRubyCallInfo.CallMaxArgs ? 1 : 0);
                            if (actualArgc != m1)
                            {
                                RaiseArgumentNumberError(actualArgc, m1);
                            }

                            // clear local (but non-argument) variables
                            var count = m1 + 2; // self + m1 + block
                            var clearCount = irep.LocalVariablesBackingField.Length - count;
                            if (clearCount > 0)
                            {
                                Context.ClearStack(
                                    callInfo.StackPointer + count,
                                    clearCount);
                            }
                            goto Next;
                        }

                        SlowPath(this, irep, ref callInfo, registers, bits);

                        goto Next;

                        static void SlowPath(MRubyState state, Irep irep, ref MRubyCallInfo callInfo, Span<MRubyValue> registers, uint bits)
                        {
                            var argc = callInfo.ArgumentCount;
                            var argv = registers[1..];
                            var aspec = new ArgumentSpec(bits);

                            var m1 = aspec.MandatoryArguments1Count;
                            var o = aspec.OptionalArgumentsCount;
                            var r = aspec.TakeRestArguments ? 1 : 0;
                            var m2 = aspec.MandatoryArguments2Count;
                            // mrb_int kd = (MRB_ASPEC_KEY(a) > 0 || MRB_ASPEC_KDICT(a))? 1 : 0;
                            var argv0 = argv.IsEmpty ? default : argv[0];

                            var mandantryTotalRequired = m1 + o + r + m2;
                            var block = registers[callInfo.BlockArgumentOffset];
                            var kdict = default(MRubyValue);
                            var hasAnyKeyword = aspec.KeywordArgumentsCount > 0 || aspec.TakeKeywordDict;

                            // keyword arguments
                            if (callInfo.KeywordArgumentPacked)
                            {
                                kdict = registers[callInfo.KeywordArgumentOffset];
                            }

                            if (!hasAnyKeyword)
                            {
                                if (kdict.Object is RHash { Length: > 0 })
                                {
                                    switch (argc)
                                    {
                                        // packed
                                        case MRubyCallInfo.CallMaxArgs:
                                            // push kdict to packed arguments
                                            registers[1].As<RArray>().Push(kdict);
                                            break;
                                        case MRubyCallInfo.CallMaxArgs - 1:
                                        {
                                            // pack arguments and kdict
                                            var packed = state.NewArray(registers.Slice(1, argc + 1));
                                            registers[1] = MRubyValue.From(packed);
                                            argc = callInfo.ArgumentCount = MRubyCallInfo.CallMaxArgs;
                                            break;
                                        }
                                        default:
                                            callInfo.ArgumentCount++;
                                            argc++; // include kdict in normal arguments
                                            break;
                                    }
                                }
                                kdict = default;
                                callInfo.KeywordArgumentCount = 0;
                            }
                            else if (aspec.KeywordArgumentsCount > 0 && !kdict.IsNil)
                            {
                                kdict = MRubyValue.From(kdict.As<RHash>().Dup());
                            }

                            // arguments is passed with Array
                            if (callInfo.ArgumentPacked)
                            {
                                argv = argv0.As<RArray>().AsSpan();
                                argc = (byte)argv.Length;
                            }

                            // strict argument check
                            if (callInfo.Proc is not null && (callInfo.Proc.RawFlags & MRubyObjectFlags.ProcStrict) != 0)
                            {
                                if (argc < m1 + m2 || (r == 0 && argc > mandantryTotalRequired))
                                {
                                    state.RaiseArgumentNumberError(argc, m1 + m2);
                                }
                            }
                            // extract first argument array to arguments
                            else if (mandantryTotalRequired > 1 && argc == 1 && argv[0].Object is RArray array)
                            {
                                argc = (byte)array.Length;
                                argv = array.AsSpan();
                            }

                            // rest arguments
                            if (argc < mandantryTotalRequired)
                            {
                                var mlen = (int)m2;
                                if (argc < m1 + m2)
                                {
                                    mlen = m1 < argc ? argc - m1 : 0;
                                }

                                if (!argv.IsEmpty && argv[0] != argv0)
                                {
                                    argv[..(argc - mlen)].CopyTo(registers[1..]); // m1 + o
                                }
                                if (argc < m1)
                                {
                                    registers.Slice(argc + 1, m1 - argc).Clear();
                                }

                                // copy post mandatory arguments
                                if (mlen > 0)
                                {
                                    argv.Slice(argc - mlen, mlen)
                                        .CopyTo(registers[(mandantryTotalRequired - m2 + 1)..]);
                                }
                                if (mlen < m2)
                                {
                                    registers.Slice(mandantryTotalRequired - m2 + mlen + 1, m2 - mlen).Clear();
                                }

                                // initialize rest arguments with empty Array
                                if (r > 0)
                                {
                                    var rest = MRubyValue.From(state.NewArray(0));
                                    registers[m1 + o + 1] = rest;
                                }

                                // skip initializer of passed arguments
                                if (o > 0 && argc > m1 + m2)
                                {
                                    callInfo.ProgramCounter += (argc - m1 - m2) * 3;
                                }
                            }
                            else
                            {
                                var restElementLength = 0;
                                if (!argv.IsEmpty && argv0 != argv[0])
                                {
                                    argv[..(m1 + o)].CopyTo(registers[1..]);
                                }
                                if (r > 0)
                                {
                                    restElementLength = argc - m1 - o - m2;
                                    var rest = MRubyValue.From(state.NewArray(argv.Slice(m1 + o, restElementLength)));
                                    registers[m1 + o + 1] = rest;
                                }

                                if (m2 > 0 && argc - m2 > m1)
                                {
                                    argv[(m1 + o + restElementLength)..].CopyTo(registers[(m1 + o + r + 1)..]);
                                }
                                callInfo.ProgramCounter += o * 3;
                            }

                            // need to be update blk first to protect blk from GC
                            var keywordPos = mandantryTotalRequired + (hasAnyKeyword ? 1 : 0);
                            var blockPos = keywordPos + 1;
                            registers[blockPos] = block;
                            if (hasAnyKeyword)
                            {
                                if (kdict.IsNil) kdict = MRubyValue.From(state.NewHash(0));
                                registers[keywordPos] = kdict;
                                callInfo.MarkAsKeywordArgumentPacked();
                            }

                            // format arguments for generated code
                            callInfo.ArgumentCount = (byte)mandantryTotalRequired;
                            // clear local (but non-argument) variables
                            if (irep.LocalVariablesBackingField.Length - blockPos - 1 > 0)
                            {
                                registers.Slice(blockPos + 1, irep.LocalVariablesBackingField.Length - blockPos - 1).Clear();
                            }
                        }
                    }

                    case OpCode.KArg:
                    {
                        Markers.KArg();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        // mrb_value k = mrb_symbol_value(irep->syms[b]);
                        var key = MRubyValue.From(Add(ref symbol0, Add(ref seqRef, 2)));
                        var kargOffset = callInfo.KeywordArgumentOffset;
                        if (kargOffset < 0)
                        {
                            RaiseMissingKeywordError(key);
                        }
                        var kdict = Add(ref register0, kargOffset);
                        var value = default(MRubyValue);
                        if (kdict.VType != MRubyVType.Hash ||
                            !As<RHash>(kdict.Union.RawObject).TryGetValue(key, out value))
                        {
                            RaiseMissingKeywordError(key);
                        }

                        registerA = value;
                        As<RHash>(kdict.Union.RawObject).TryDelete(key, out _);
                        goto Next;

                        [MethodImpl(MethodImplOptions.NoInlining)]
                        void RaiseMissingKeywordError(MRubyValue keyValue)
                        {
                            Raise(Names.ArgumentError, $"missing keyword: {Stringify(keyValue)}");
                        }
                    }
                    case OpCode.KeyP:
                    {
                        Markers.KeyP();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var kdict = Add(ref register0, callInfo.KeywordArgumentOffset).As<RHash>();
                        var key = MRubyValue.From(Add(ref symbol0, Add(ref seqRef, 2)));
                        registerA = MRubyValue.From(kdict.TryGetValue(key, out _));
                        goto Next;
                    }
                    case OpCode.KeyEnd:
                    {
                        Markers.KeyEnd();
                        callInfo.ProgramCounter++;
                        var kargOffset = callInfo.KeywordArgumentOffset;
                        if (kargOffset >= 0 &&
                            Add(ref register0, kargOffset).Object is RHash { Length: > 0 } hash)
                        {
                            var key1 = hash.Keys[0];
                            RaiseUnknownKeyword(key1);

                            [MethodImpl(MethodImplOptions.NoInlining)]
                            void RaiseUnknownKeyword(MRubyValue keyValue)
                            {
                                Raise(Names.ArgumentError, $"unknown keyword: {Stringify(keyValue)}");
                            }
                        }
                        goto Next;
                    }
                    case OpCode.Return:
                    {
                        Markers.Return();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        var returnValue = registerA;
                        if (TryReturnJump(ref callInfo, Context.CallDepth, returnValue))
                        {
                            goto JumpAndNext;
                        }
                        return returnValue;
                    }
                    case OpCode.ReturnBlk:
                    {
                        Markers.ReturnBlk();

                        if (callInfo.Proc is not { } proc || (proc.RawFlags & MRubyObjectFlags.ProcStrict) != 0 ||
                            proc.Scope is not REnv)
                        {
                            goto case OpCode.Return;
                        }
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        var dest = proc.FindReturningDestination(out var env);
                        if (dest.Scope is not REnv destEnv || destEnv.Context == Context)
                        {
                            // check jump destination
                            for (var i = Context.CallDepth; i >= 0; i--)
                            {
                                if (Context.CallStack[i].Scope == env)
                                {
                                    var returnValue = registerA;
                                    if (TryReturnJump(ref callInfo, i, returnValue))
                                    {
                                        goto JumpAndNext;
                                    }
                                    return returnValue;
                                }
                            }
                        }
                        // no jump destination
                        Raise(Names.LocalJumpError, "unexpected return"u8);
                        goto Next; // not reached
                    }
                    case OpCode.Break:
                    {
                        Markers.Break();
                        if (callInfo.Proc is { } proc)
                        {
                            if ((proc.RawFlags & MRubyObjectFlags.ProcStrict) != 0)
                            {
                                goto case OpCode.Return;
                            }

                            callInfo.ProgramCounter += 2;
                            if ((proc.RawFlags & MRubyObjectFlags.ProcOrphan) == 0 &&
                                proc.Scope is REnv env && env.Context == Context)
                            {
                                var dest = proc.Upper;
                                for (var i = Context.CallDepth; i > 0; i--)
                                {
                                    if (Context.CallStack[i - 1].Proc == dest)
                                    {
                                        var returnValue = registerA;
                                        if (TryReturnJump(ref callInfo, i, returnValue))
                                        {
                                            goto JumpAndNext;
                                        }
                                        return returnValue;
                                    }
                                }
                            }
                        }

                        Raise(Names.LocalJumpError, "break from proc-closure"u8);
                        goto Next; // not reached
                    }
                    case OpCode.BlkPush:
                    {
                        Markers.BlkPush();
                        BlkPush(this, ref callInfo, registers, sequence);

                        static void BlkPush(MRubyState state, ref MRubyCallInfo callInfo, Span<MRubyValue> registers, ReadOnlySpan<byte> sequence)
                        {
                            var bs = OperandBS.Read(sequence, ref callInfo.ProgramCounter);
                            var b = bs.B;
                            var m1 = (b >> 11) & 0x3f;
                            var r = (b >> 10) & 0x1;
                            var m2 = (b >> 5) & 0x1f;
                            var kd = (b >> 4) & 0x1;
                            var lv = (b >> 0) & 0xf;
                            var offset = m1 + r + m2 + kd;

                            ReadOnlySpan<MRubyValue> stack;
                            if (lv == 0)
                            {
                                stack = registers[1..];
                            }
                            else
                            {
                                var env = callInfo.Proc?.FindUpperEnvTo(lv - 1);
                                if (env == null ||
                                    env is { OnStack: false, MethodId.Value: 0 } ||
                                    env.Stack.Length <= offset + 1)
                                {
                                    state.Raise(Names.LocalJumpError, "unexpected yield"u8);
                                }
                                stack = env!.Stack[1..];
                            }

                            var block = stack[offset];
                            if (block.IsNil)
                            {
                                state.Raise(Names.LocalJumpError, "unexpected yield"u8);
                            }
                            registers[bs.A] = block;
                        }

                        goto Next;
                    }
                    case OpCode.Add:
                    case OpCode.Sub:
                    case OpCode.Mul:
                    case OpCode.Div:
                        Markers.Add();
                        Markers.Sub();
                        Markers.Mul();
                        Markers.Div();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        ref var rhs = ref Add(ref registerA, 1);
                        // fast path for integer and float because MRubyValue has TypeObjectUnion as offset 0
                        // and MRubyVType.Integer is 7, MRubyVType.Float is 6 so we can assume immediateType can be safely casted to MRubyVType
                        var lhsImmediateVType = (MRubyVType)As<MRubyValue, nuint>(ref registerA);
                        var rhsImmediateVType = (MRubyVType)As<MRubyValue, nuint>(ref rhs);
                        // MRubyVType.Integer * MRubyVType.Integer == 49 which is unique value
                        if ((int)lhsImmediateVType * (int)rhsImmediateVType == (byte)MRubyVType.Integer * (byte)MRubyVType.Integer)
                        {
                            var leftInt = registerA.Bits;
                            var rightInt = rhs.Bits;
                            try
                            {
                                registerA = MRubyValue.From(opcode switch
                                {
                                    //OpCode.Add => checked(leftInt + rightInt),
                                    OpCode.Sub => checked(leftInt - rightInt),
                                    OpCode.Mul => checked(leftInt * rightInt),
                                    OpCode.Div => leftInt / rightInt,
                                    _ => checked(leftInt + rightInt), // OpCode.Add
                                });
                            }
                            catch (Exception e)
                            {
                                OnCatchIntegerArithmeticError(this, opcode, e);
                            }

                            static void OnCatchIntegerArithmeticError(MRubyState state, OpCode opcode, Exception ex)
                            {
                                switch (ex)
                                {
                                    case OverflowException:
                                        IntegerMembers.RaiseIntegerOverflowError(state, opcode);
                                        break;
                                    case DivideByZeroException:
                                        IntegerMembers.RaiseDivideByZeroError(state);
                                        break;
                                    default: return;
                                }
                            }

                            goto Next;
                        }

                        // (MRubyVType.Float-6) | (MRubyVType.Float-6) == 0 | 0 == 0
                        // (MRubyVType.Float-6) | (MRubyVType.Integer-6) == 0 | 1 == 1
                        if ((((int)lhsImmediateVType - (int)MRubyVType.Float) | ((int)rhsImmediateVType - (int)MRubyVType.Float)) is 0 or 1)
                        {
                            var leftVal = lhsImmediateVType == MRubyVType.Integer ? registerA.Bits : registerA.RawFloat;
                            var rightVal = rhsImmediateVType == MRubyVType.Integer ? rhs.Bits : rhs.RawFloat;

                            registerA = MRubyValue.From(opcode switch
                            {
                                //OpCode.Add => leftVal + rightVal,
                                OpCode.Sub => leftVal - rightVal,
                                OpCode.Mul => leftVal * rightVal,
                                OpCode.Div => leftVal / rightVal,
                                _ => leftVal + rightVal, //OpCode.Add
                            });
                            goto Next;
                        }

                        if (opcode == OpCode.Add && registerA.VType == MRubyVType.String && rhs.VType == MRubyVType.String)
                        {
                            registerA = MRubyValue.From(As<RString>(registerA.Union.RawObject) + As<RString>(rhs.Union.RawObject));
                            goto Next;
                        }

                    {
                        // Jump to send : + or :- or :* or :/
                        callInfo = ref GetNextCallInfo(callInfo.StackPointer + a, opcode, 1);
                        goto case OpCode.SendInternal;
                    }
                    case OpCode.AddI:
                    case OpCode.SubI:
                        Markers.AddI();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                    {
                        var rV = opcode == OpCode.AddI ? Add(ref seqRef, 2) : -Add(ref seqRef, 2);
                        switch ((MRubyVType)As<MRubyValue, nuint>(ref registerA))
                        {
                            case MRubyVType.Integer:
                                try
                                {
                                    registerA = MRubyValue.From(checked(registerA.Bits + rV));
                                }
                                catch (OverflowException)
                                {
                                    IntegerMembers.RaiseIntegerOverflowError(this, opcode);
                                }
                                goto Next;
                            case MRubyVType.Float:
                                registerA = MRubyValue.From(registerA.RawFloat + rV);
                                goto Next;
                        }

                        // Jump to send :+ or :-
                        Add(ref registerA, 1) = MRubyValue.From(rV);
                        callInfo = ref GetNextCallInfo(callInfo.StackPointer + a, opcode, 1);
                        goto case OpCode.SendInternal;
                    }
                    case OpCode.EQ:
                    case OpCode.LT:
                    case OpCode.LE:
                    case OpCode.GT:
                    case OpCode.GE:
                        Markers.EQ();
                        Markers.LT();
                        Markers.LE();
                        Markers.GT();
                        Markers.GE();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        rhs = ref Add(ref registerA, 1);

                        if (opcode == OpCode.EQ)
                        {
                            if (registerA.Equals(rhs))
                            {
                                registerA = MRubyValue.True;
                                goto Next;
                            }
                            if (registerA.IsSymbol)
                            {
                                registerA = MRubyValue.False;
                                goto Next;
                            }
                        }

                        lhsImmediateVType = (MRubyVType)As<MRubyValue, nuint>(ref registerA);
                        rhsImmediateVType = (MRubyVType)As<MRubyValue, nuint>(ref rhs);

                        // (MRubyVType.Float+1) * (MRubyVType.Float+1) == 49 which is unique value
                        if (((byte)lhsImmediateVType + 1) * ((byte)rhsImmediateVType + 1) == (byte)(MRubyVType.Float + 1) * (byte)(MRubyVType.Float + 1))
                        {
                            var leftVal = registerA.RawFloat;
                            var rightVal = rhs.RawFloat;
                            registerA = MRubyValue.From(opcode switch
                            {
                                OpCode.LT => leftVal < rightVal,
                                OpCode.LE => leftVal <= rightVal,
                                OpCode.GT => leftVal > rightVal,
                                OpCode.GE => leftVal >= rightVal,
                                // ReSharper disable once CompareOfFloatsByEqualityOperator
                                _ => leftVal == rightVal //OpCode.EQ
                            });
                            goto Next;
                        }

                        // lhs can be Integer or Float, rhs can be Integer or Float
                        // (MRubyVType.Float-6) | (MRubyVType.Float-6) == 0 | 0 == 0
                        // (MRubyVType.Float-6) | (MRubyVType.Integer-6) == 0 | 1 == 1
                        if ((((int)lhsImmediateVType - (int)MRubyVType.Float) | ((int)rhsImmediateVType - (int)MRubyVType.Float)) is 0 or 1)
                        {
                            var leftVal = lhsImmediateVType == MRubyVType.Integer ? registerA.Bits : (long)registerA.RawFloat;
                            var rightVal = rhsImmediateVType == MRubyVType.Integer ? rhs.Bits : (long)rhs.RawFloat;
                            {
                                registerA = MRubyValue.From(opcode switch
                                {
                                    OpCode.LT => leftVal < rightVal,
                                    OpCode.LE => leftVal <= rightVal,
                                    OpCode.GT => leftVal > rightVal,
                                    OpCode.GE => leftVal >= rightVal,
                                    _ => leftVal == rightVal, // OpCode.EQ
                                });
                            }
                            goto Next;
                        }
                    {
                        // Jump to send method
                        callInfo = ref GetNextCallInfo(callInfo.StackPointer + a, opcode, 1);
                        goto case OpCode.SendInternal;
                    }
                    case OpCode.Array:
                    {
                        Markers.Array();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var values = registers.Slice(a, Add(ref seqRef, 2));
                        registerA = MRubyValue.From(NewArray(values));
                        goto Next;
                    }
                    case OpCode.Array2:
                    {
                        Markers.Array2();
                        //OperandBBB bbb;
                        callInfo.ProgramCounter += 4;
                        b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 2));
                        var values = registers.Slice(b2.Bytes[0], b2.Bytes[1]);
                        registerA = MRubyValue.From(NewArray(values));
                        goto Next;
                    }
                    case OpCode.AryCat:
                    {
                        Markers.AryCat();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        var splat = SplatArray(Add(ref registerA, 1));
                        if (registerA.IsNil)
                        {
                            registerA = splat;
                        }
                        else
                        {
                            EnsureValueType(registerA, MRubyVType.Array);
                            var array = As<RArray>(registerA.Union.RawObject);
                            array.Concat(As<RArray>(splat.Union.RawObject));
                        }
                        goto Next;
                    }
                    case OpCode.ARef:
                    {
                        Markers.ARef();
                        //OperandBBB bbb;
                        callInfo.ProgramCounter += 4;
                        b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 2));
                        var v = Add(ref register0, b2.Bytes[0]);
                        if (v.VType == MRubyVType.Array)
                        {
                            registerA = As<RArray>(v.Union.RawObject)[b2.Bytes[1]];
                        }
                        else
                        {
                            if (b2.Bytes[1] == 0)
                            {
                                registerA = v;
                            }
                            else
                            {
                                registerA = default;
                            }
                        }
                        goto Next;
                    }
                    case OpCode.ASet:
                    {
                        Markers.ASet();
                        //OperandBBB bbb;
                        callInfo.ProgramCounter += 4;
                        b2 = ReadUnaligned<Byte2>(ref Add(ref seqRef, 2));
                        var array = Add(ref register0, b2.Bytes[0]).As<RArray>();
                        array[b2.Bytes[1]] = registerA;
                        goto Next;
                    }
                    case OpCode.APost:
                    {
                        Markers.APost();
                        bbb = OperandBBB.Read(sequence, ref callInfo.ProgramCounter);
                        if (registerA.Object is not RArray array)
                        {
                            array = NewArray(registerA);
                        }
                        int pre = bbb.B;
                        int post = bbb.C;
                        if (array.Length > pre + post)
                        {
                            APostShort(this, array, bbb, pre, post, ref registerA);

                            static void APostShort(MRubyState state, RArray array, OperandBBB bbb, int pre, int post, ref MRubyValue registerA)
                            {
                                var slice = array.AsSpan().Slice(bbb.B, array.Length - pre - post);
                                registerA = MRubyValue.From(state.NewArray(slice));
                                registerA = ref Add(ref registerA, 1);
                                while (post-- > 0)
                                {
                                    registerA = array[array.Length - post - 1];
                                    registerA = ref Add(ref registerA, 1);
                                }
                            }
                        }
                        else
                        {
                            APostLong(this, array, pre, post, ref registerA);

                            static void APostLong(MRubyState state, RArray array, int pre, int post, ref MRubyValue registerA)
                            {
                                registerA = MRubyValue.From(state.NewArray(0));
                                registerA = ref Add(ref registerA, 1);
                                int i;
                                for (i = 0; i + pre < array.Length; i++)
                                {
                                    Add(ref registerA, i) = array[pre + i];
                                }
                                while (i < post)
                                {
                                    Add(ref registerA, i) = default;
                                    i++;
                                }
                            }
                        }
                        goto Next;
                    }
                    case OpCode.AryPush:
                    {
                        Markers.AryPush();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        EnsureNotFrozen(registerA);

                        var array = registerA.As<RArray>();
                        array.PushRange(registers.Slice(a + 1, Add(ref seqRef, 2)));
                        goto Next;
                    }
                    case OpCode.ArySplat:
                    case OpCode.Intern:
                        Markers.ArySplat();
                        Markers.Intern();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA = opcode == OpCode.ArySplat ? SplatArray(registerA) : MRubyValue.From(Intern(registerA.As<RString>()));
                        goto Next;
                    case OpCode.Symbol:
                    case OpCode.String:
                    {
                        Markers.Symbol();
                        Markers.String();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var str = irep.PoolValuesBackingField[Add(ref seqRef, 2)].As<RString>();
                        registerA = opcode == OpCode.Symbol ? MRubyValue.From(Intern(str)) : MRubyValue.From(str.Dup());
                        goto Next;
                    }

                    case OpCode.StrCat:
                        Markers.StrCat();
                        // OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA.As<RString>().Concat(Stringify(Add(ref registerA, 1)));
                        goto Next;
                    case OpCode.Hash:
                    case OpCode.HashAdd:
                    {
                        Markers.Hash();
                        Markers.HashAdd();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var bbB = Add(ref seqRef, 2);
                        if (opcode == OpCode.Hash)
                        {
                            var hash = NewHash(bbB);
                            hash.AddRange(ref registerA, bbB);
                            registerA = MRubyValue.From(hash);
                        }
                        else
                        {
                            EnsureValueType(registerA, MRubyVType.Hash);
                            var hash = As<MRubyValue,RHash>(ref registerA);
                            hash.AddRange(ref Add(ref registerA, 1), bbB);
                        }

                        goto Next;
                    }
                    case OpCode.HashCat:
                        Markers.HashCat();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        EnsureNotFrozen(registerA);
                        registerA.As<RHash>().Merge(Add(ref registerA, 1).As<RHash>());
                        goto Next;
                    case OpCode.Lambda:
                    case OpCode.Block:
                    case OpCode.Method:
                    {
                        Markers.Lambda();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var bbB = Add(ref seqRef, 2);
                        var proc = opcode == OpCode.Method ? NewProc(irep.ChildrenBackingField[bbB]) : NewClosure(irep.ChildrenBackingField[bbB]);
                        if (opcode != OpCode.Block)
                        {
                            proc.RawFlags |= (MRubyObjectFlags.ProcStrict | MRubyObjectFlags.ProcScope);
                        }
                        registerA = MRubyValue.From(proc);
                        goto Next;
                    }
                    case OpCode.RangeInc:
                    case OpCode.RangeExc:
                        Markers.RangeInc();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                    {
                        var begin = registerA;
                        var end = Add(ref registerA, 1);
                        var range = new RRange(begin, end, opcode == OpCode.RangeExc, RangeClass);
                        range.MarkAsFrozen();
                        registerA = MRubyValue.From(range);
                        goto Next;
                    }
                    case OpCode.OClass:
                        Markers.OClass();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA = MRubyValue.From(ObjectClass);
                        goto Next;
                    case OpCode.Class:
                    {
                        Markers.Class();
                        Class(this, irep, registers, sequence, ref callInfo);

                        goto Next;

                        static void Class(MRubyState state, Irep irep, Span<MRubyValue> registers, ReadOnlySpan<byte> sequence, ref MRubyCallInfo callInfo)
                        {
                            var bb = OperandBB.Read(sequence, ref callInfo.ProgramCounter);
                            var id = irep.Symbols[bb.B];
                            var outer = registers[bb.A];
                            var super = registers[bb.A + 1];

                            RClass outerClass;
                            if (outer.IsNil)
                            {
                                outerClass = callInfo.Proc?.ScopeBackingField?.TargetClass ?? state.ObjectClass;
                            }
                            else
                            {
                                state.EnsureClassOrModule(outer);
                                outerClass = As<RClass>(outer.Union.RawObject);
                            }

                            // mrb_vm_define_class
                            RClass? superClass = null;
                            RClass definedClass;
                            if (!super.IsNil)
                            {
                                if (super.Object is RClass sc)
                                {
                                    superClass = sc;
                                }
                                else
                                {
                                    RaiseSuperClassMustBeClass(super);

                                    [MethodImpl(MethodImplOptions.NoInlining)]
                                    void RaiseSuperClassMustBeClass(MRubyValue superValue)
                                    {
                                        state.Raise(Names.TypeError, $"superclass must be a Class ({state.Stringify(superValue)} given)");
                                    }
                                }
                            }

                            if (state.ConstDefinedAt(id, outerClass))
                            {
                                var old = state.GetConst(id, outerClass);
                                if (!old.IsClass)
                                {
                                    RaiseNotAClass(old);

                                    [MethodImpl(MethodImplOptions.NoInlining)]
                                    void RaiseNotAClass(MRubyValue oldValue)
                                    {
                                        state.Raise(Names.TypeError, $"{state.StringifyAny(oldValue)} is not a class");
                                    }
                                }

                                definedClass = As<RClass>(old.Union.RawObject);
                                if (superClass != null)
                                {
                                    // check super class
                                    if (definedClass.Super.GetRealClass() != superClass)
                                    {
                                        RaiseSuperClassMismatch(old);

                                        [MethodImpl(MethodImplOptions.NoInlining)]
                                        void RaiseSuperClassMismatch(MRubyValue oldValue)
                                        {
                                            state.Raise(Names.TypeError, $"superclass mismatch for {state.StringifyAny(oldValue)}");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                superClass ??= state.ObjectClass;
                                definedClass = state.DefineClass(id, superClass, superClass.InstanceVType, outerClass);
                                state.ClassInheritedHook(superClass.GetRealClass(), definedClass);
                            }
                            registers[bb.A] = MRubyValue.From(definedClass);
                        }
                    }

                    case OpCode.Module:
                    {
                        Markers.Module();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var id = Add(ref symbol0, Add(ref seqRef, 2));
                        RClass outerClass;
                        if (registerA.IsNil)
                        {
                            outerClass = callInfo.Proc?.ScopeBackingField?.TargetClass ?? ObjectClass;
                        }
                        else
                        {
                            EnsureClassOrModule(registerA);
                            outerClass = As<RClass>(registerA.Union.RawObject);
                        }

                        RClass definedModule;
                        if (ConstDefinedAt(id, outerClass))
                        {
                            var old = GetConst(id, outerClass);
                            if (old.VType != MRubyVType.Module)
                            {
                                RaiseNotAModule(old);

                                [MethodImpl(MethodImplOptions.NoInlining)]
                                void RaiseNotAModule(MRubyValue oldValue)
                                {
                                    Raise(Names.TypeError, $"{StringifyAny(oldValue)} is not a module");
                                }
                            }
                            definedModule = As<RClass>(old.Union.RawObject);
                        }
                        else
                        {
                            definedModule = DefineModule(id, outerClass);
                        }
                        registerA = MRubyValue.From(definedModule);
                        goto Next;
                    }
                    case OpCode.Exec:
                    {
                        Markers.Exec();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var receiver = registerA;
                        var targetIrep = irep.ChildrenBackingField[Add(ref seqRef, 2)];

                        // prepare closure
                        var proc = NewProc(targetIrep, receiver.As<RClass>());
                        proc.RawFlags |= MRubyObjectFlags.ProcScope;

                        // prepare callstack
                        ref var nextCallInfo = ref Context.PushCallStack();
                        nextCallInfo.StackPointer = callInfo.StackPointer + a;
                        nextCallInfo.CallerType = CallerType.InVmLoop;
                        nextCallInfo.Scope = As<RClass>(receiver.Union.RawObject);
                        nextCallInfo.Proc = proc;
                        nextCallInfo.MethodId = default;
                        nextCallInfo.ArgumentCount = 0;
                        nextCallInfo.KeywordArgumentCount = 0;
                        nextCallInfo.ProgramCounter = 0;

                        // modify local variable and jump
                        callInfo = ref nextCallInfo;

                        irep = callInfo.Proc!.IrepBackingField;
                        sequence = irep.SequenceBackingField.AsSpan();
                        symbols = irep.SymbolsBackingField;
                        Context.ExtendStack(callInfo.StackPointer + irep.RegisterVariableCountBackingField + 1);
                        Context.ClearStack(callInfo.StackPointer + 1, irep.RegisterVariableCountBackingField - 1);

                        registers = Context.Stack.AsSpan(nextCallInfo.StackPointer);

                        seq0 = ref MemoryMarshal.GetReference(sequence);
                        register0 = ref MemoryMarshal.GetReference(registers);
                        symbol0 = ref MemoryMarshal.GetReference(symbols);
                        goto Next;
                    }
                    case OpCode.Def:
                    {
                        Markers.Def();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var target = registerA.As<RClass>();
                        var proc = Add(ref registerA, 1).As<RProc>();
                        var methodId = Add(ref symbol0, Add(ref seqRef, 2));

                        DefineMethod(target, methodId, new MRubyMethod(proc));
                        MethodAddedHook(target, methodId);
                        registerA = MRubyValue.From(methodId);
                        goto Next;
                    }
                    case OpCode.Alias:
                    {
                        Markers.Alias();
                        //OperandBB bb;
                        callInfo.ProgramCounter += 3;
                        var c = callInfo.Scope.TargetClass;
                        var newMethodId = Add(ref symbol0, a);
                        var oldMethodId = Add(ref symbol0, Add(ref seqRef, 2));
                        AliasMethod(c, newMethodId, oldMethodId);
                        MethodAddedHook(c, newMethodId);
                        goto Next;
                    }
                    case OpCode.Undef:
                    {
                        Markers.Undef();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        var c = callInfo.Scope.TargetClass;
                        var methodId = Add(ref symbol0, a);
                        UndefMethod(c, methodId);
                        goto Next;
                    }
                    case OpCode.SClass:
                    {
                        Markers.SClass();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA = MRubyValue.From(SingletonClassOf(registerA));
                        goto Next;
                    }
                    case OpCode.TClass:
                    {
                        Markers.TClass();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        registerA = MRubyValue.From(callInfo.Scope.TargetClass);
                        goto Next;
                    }
                    case OpCode.Err:
                    {
                        Markers.Err();
                        //OperandB b;
                        callInfo.ProgramCounter += 2;
                        RaiseErr(this, irep.PoolValuesBackingField[a]);

                        static void RaiseErr(MRubyState state, MRubyValue message)
                        {
                            state.Raise(state.GetExceptionClass(Names.LocalJumpError), message.As<RString>());
                        }

                        goto Next;
                    }
                    case OpCode.Stop:
                    {
                        Markers.Stop();
                        var returnValue = Exception switch
                        {
                            MRubyRaiseException x => MRubyValue.From(x.ExceptionObject),
                            MRubyBreakException x => MRubyValue.From(x.BreakObject),
                            _ => default
                        };
                        if (TryUnwindEnsureJump(ref callInfo, Context.CallDepth, BreakTag.Stop, returnValue))
                        {
                            goto JumpAndNext;
                        }
                        if (Exception != null) throw Exception;
                        return Add(ref register0, irep.LocalVariablesBackingField.Length);
                    }
                    default:
                    {
                        ThrowInvalidOpCode(opcode);
                        return default;

                        static void ThrowInvalidOpCode(OpCode opcode)
                        {
                            throw new NotSupportedException($"Invalid opcode {opcode}");
                        }
                    }
                }

#pragma warning disable CS0162 // Unreachable code detected
                goto Next;
#pragma warning restore CS0162 // Unreachable code detected

                JumpAndNext:
                callInfo = ref Context.CurrentCallInfo;
                irep = callInfo.Proc!.IrepBackingField;
                registers = Context.Stack.AsSpan(callInfo.StackPointer);
                sequence = irep.SequenceBackingField.AsSpan();
                symbols = irep.SymbolsBackingField;

                seq0 = ref MemoryMarshal.GetReference(sequence);
                register0 = ref MemoryMarshal.GetReference(registers);
                symbol0 = ref MemoryMarshal.GetReference(symbols);
            }
            catch (MRubyRaiseException ex)
            {
                Exception = ex;
                if (TryRaiseJump(ref Context.CurrentCallInfo))
                {
                    callInfo = ref Context.CurrentCallInfo;
                    irep = callInfo.Proc!.IrepBackingField;
                    registers = Context.Stack.AsSpan(callInfo.StackPointer);
                    sequence = irep.SequenceBackingField.AsSpan();
                    seq0 = ref MemoryMarshal.GetReference(sequence);
                    register0 = ref MemoryMarshal.GetReference(registers);
                    symbols = irep.SymbolsBackingField;
                    symbol0 = ref MemoryMarshal.GetReference(symbols);
                }
                else
                {
                    throw;
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        ref MRubyCallInfo GetNextCallInfo(int nextStackPointer, OpCode code, byte argCount)
        {
            ref var callInfo = ref Context.PushCallStack();
            callInfo.CallerType = CallerType.InVmLoop;
            callInfo.StackPointer = nextStackPointer;
            callInfo.MethodId = SymbolHelpers.GetOpCodeSymbol(code);
            callInfo.ArgumentCount = argCount;
            callInfo.KeywordArgumentCount = 0;
            return ref callInfo;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte ReadOperandB(ReadOnlySpan<byte> sequence, ref int pc)
    {
        pc += 2;
        var result = Add(ref MemoryMarshal.GetReference(sequence), pc - 1);
        return result;
    }

    /// I don't know why, but introducing this method makes the code faster.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static short ReadOperandS(ReadOnlySpan<byte> sequence, ref int pc)
    {
        return OperandS.Read(sequence, ref pc).A;
    }

    bool TryReturnJump(ref MRubyCallInfo callInfo, int returnDepth, MRubyValue returnValue)
    {
        while (true)
        {
            if (TryUnwindEnsureJump(ref callInfo, returnDepth, BreakTag.Break, returnValue))
            {
                return true;
            }

            if (Context.CallDepth == returnDepth)
            {
                break;
            }

            var callerType = callInfo.CallerType;
            Context.PopCallStack();
            callInfo = ref Context.CurrentCallInfo;
            if (callerType != CallerType.InVmLoop)
            {
                Exception = new MRubyBreakException(this, new RBreak
                {
                    BreakIndex = returnDepth,
                    Tag = BreakTag.Break,
                    Value = returnValue
                });
                Context.VmExecutedByFiber = false;
                throw Exception;
            }
        }
        Exception = null; // Clear break object

        // root
        if (Context.CallDepth == 0)
        {
            if (Context == ContextRoot)
            {
                // toplevel return
                return false;
            }

            Context.Fiber?.Terminate(ref callInfo);

            // case using Fiber#transfer in resume
            if (Context.VmExecutedByFiber || (Context == ContextRoot && Context.CallDepth <= 0))
            {
                Context.VmExecutedByFiber = false;
                return false;
            }

            callInfo = Context.CurrentCallInfo;
        }

        if (Context.VmExecutedByFiber && !callInfo.KeepContext)
        {
            Context.VmExecutedByFiber = false;
            return false;
        }

        var returnOffset = callInfo.StackPointer;
        Context.PopCallStack();
        if (callInfo.CallerType is CallerType.VmExecuted or CallerType.MethodCalled)
        {
            return false;
        }

        Context.Stack[returnOffset] = returnValue;
        return true;
    }

    bool TryUnwindEnsureJump(ref MRubyCallInfo callInfo, int returnDepth, BreakTag tag, MRubyValue value)
    {
        if (callInfo.Proc is { Irep: { CatchHandlers.Length: > 0 } irep } &&
            irep.TryFindCatchHandler(callInfo.ProgramCounter, CatchHandlerType.Ensure, out var catchHandler))
        {
            PrepareTaggedBreak(tag, returnDepth, value);
            callInfo.ProgramCounter = (int)catchHandler.Target;
            return true;
        }
        return false;
    }

    bool TryRaiseJump(ref MRubyCallInfo callInfo)
    {
        while (true)
        {
            if (callInfo.Proc is { Irep: { CatchHandlers.Length: > 0 } irep } &&
                irep.TryFindCatchHandler(callInfo.ProgramCounter, CatchHandlerType.All, out var catchHandler))
            {
                callInfo.ProgramCounter = (int)catchHandler.Target;
                return true;
            }

            if (Context.CallDepth > 0)
            {
                var callerType = callInfo.CallerType;
                Context.PopCallStack();
                callInfo = ref Context.CurrentCallInfo;
                if (callerType == CallerType.VmExecuted)
                {
                    return false;
                }
            }
            else if (Context == ContextRoot)
            {
                // top-level
                return false;
            }
            else
            {
                // Fiber context
                Context.Fiber?.Terminate(ref callInfo);
                callInfo = ref Context.CurrentCallInfo;
                if (!Context.VmExecutedByFiber)
                {
                    return TryRaiseJump(ref callInfo);
                }
                return false;
            }
        }
    }

    void PrepareTaggedBreak(BreakTag tag, int callDepth, MRubyValue returnValue)
    {
        if (Exception is MRubyBreakException ex)
        {
            ex.BreakObject.Tag = tag;
        }
        else
        {
            Exception = new MRubyBreakException(this, new RBreak
            {
                BreakIndex = callDepth,
                Tag = tag,
                Value = returnValue
            });
        }
    }

    MRubyMethod PrepareMethodMissing(
        ref MRubyCallInfo callInfo,
        MRubyValue receiver,
        Symbol methodId,
        Action<MRubyState, MRubyValue, Symbol>? raise = null)
    {
        var receiverClass = ClassOf(receiver);
        var args = Context.GetRestArgumentsAfter(ref callInfo, 0);
        if (!TryFindMethod(receiverClass, Names.MethodMissing, out var method, out _) ||
            method == BasicObjectMembers.MethodMissing)
        {
            _Raise(args);
        }

        // call :method_missing

        if (!TryFindMethod(callInfo.Scope.TargetClass, Names.MethodMissing, out var methodMissing, out _))
        {
            _Raise(args);
        }

        Context.ExtendStack(callInfo.StackPointer + 5);
        var registers = Context.Stack.AsSpan(callInfo.StackPointer);

        registers[1] = MRubyValue.From(NewArray(args));
        if (callInfo.KeywordArgumentCount == 0)
        {
            registers[2] = args[callInfo.BlockArgumentOffset];
        }
        else if (callInfo.KeywordArgumentPacked)
        {
            registers[2] = args[callInfo.ArgumentCount];
            registers[3] = args[callInfo.BlockArgumentOffset];
        }
        else
        {
            var hash = NewHash(callInfo.KeywordArgumentCount);
            foreach (var (key, value) in Context.GetKeywordArgs(ref callInfo))
            {
                hash[MRubyValue.From(key)] = value;
            }
            registers[2] = MRubyValue.From(hash);
            registers[3] = args[callInfo.BlockArgumentOffset];
        }

        callInfo.MarkAsArgumentPacked();
        callInfo.MarkAsKeywordArgumentPacked();
        callInfo.MethodId = Names.MethodMissing;
        if (methodId != Names.MethodMissing)
        {
            callInfo.Scope = receiverClass;
        }

        return methodMissing;

        void _Raise(ReadOnlySpan<MRubyValue> args)
        {
            if (raise != null)
            {
                raise(this, receiver, methodId);
            }
            else
            {
                RaiseMethodMissing(methodId, receiver, MRubyValue.From(NewArray(args)));
            }
        }
    }
}
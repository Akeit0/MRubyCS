using System;

namespace MRubyCS;

partial class MRubyState
{
    public bool ConstDefinedAt(Symbol id, RClass? module = null, bool recursive = false)
    {
        module ??= ObjectClass;
        if (module.InstanceVariables.Defined(id)) return true;

        if (recursive)
        {
            module = module.Super;
            do
            {
                if (module.InstanceVariables.Defined(id)) return true;
                module = module.Super;
            } while (module == ObjectClass);
        }
        return false;
    }

    public bool TryGetConst(Symbol name, out MRubyValue value)
    {
        return TryGetConst(name, ObjectClass, out value);
    }

    public bool TryGetConst(Symbol name, RClass module, out MRubyValue value)
    {
        var c = module;
        while (c != null!)
        {
            if (!c.HasFlag(MRubyObjectFlags.ClassPrepended) &&
                c.InstanceVariables.TryGet(name, out value))
            {
                return true;
            }
            c = c.Super;
            if (c == ObjectClass) break;
        }
        value = default;
        return false;
    }

    public MRubyValue GetConst(Symbol name, RClass module)
    {
        if (TryGetConst(name, module, out var result))
        {
            return result;
        }
        return Send(MRubyValue.From(module), Intern("const_missing"u8), MRubyValue.From(name));
    }

    public void SetConst(Symbol name, RClass mod, MRubyValue value)
    {
        EnsureConstName(name);
        if (value.VType is MRubyVType.Class or MRubyVType.Module)
        {
            TrySetClassPathLink(mod, ClassOf(value), name);
        }
        mod.InstanceVariables.Set(name, value);
    }

    public MRubyValue GetClassVariable(Symbol id)
    {
        ref var ci = ref context.CurrentCallInfo;
        RProc? proc = ci.Proc;
        RClass? c;
        while (true)
        {
            c = proc?.Scope?.TargetClass;
            if ((c != null && c.VType != MRubyVType.SClass) || proc == null)
            {
                break;
            }
            proc = proc?.Upper;
        }
        return c == null! ? MRubyValue.Nil : GetClassVariable(c, id);
    }

    public MRubyValue GetClassVariable(RClass c, Symbol id)
    {
        var target = c;
        var given = false;
        var result = MRubyValue.Nil;
        while (c != null!)
        {
            if (c.ClassInstanceVariableTable.TryGet(id, out var v))
            {
                given = true;
                result = v;
            }
            c = c.Super;
        }
        if (given) return result;

        if (target.VType == MRubyVType.SClass)
        {
            c = target.InstanceVariables.Get(Names.AttachedKey).As<RClass>();
            if (c.VType is MRubyVType.Class or MRubyVType.Module)
            {
                given = false;
                while (c != null!)
                {
                    if (c.InstanceVariables.TryGet(id, out var v))
                    {
                        given = true;
                        result = v;
                    }
                    c = c.Super;
                }
                if (given) return result;
            }
        }

        Raise(Names.NameError, NewString($"uninitialized class variable {NameOf(id)} in {NameOf(c!)}"));
        return default;
    }

    public void SetClassVariable(Symbol id, MRubyValue value)
    {
        RProc? p = context.CurrentCallInfo.Proc;
        RClass? c;
        while (true)
        {
            c = p?.Scope as RClass;
            if (c != null & c?.VType != MRubyVType.SClass)
            {
                break;
            }
            p = p?.Upper;
        }

        if (c is null) return;
        SetClassVariable(c, id, value);
    }

    public void SetClassVariable(RClass c, Symbol id, MRubyValue value)
    {
        var targetClass = c;
        while (targetClass != null!)
        {
            if (targetClass.InstanceVariables.TryGet(id, out _))
            {
                EnsureNotFrozen(value);
                targetClass.InstanceVariables.Set(id, value);
                return;
            }
            targetClass = targetClass.Super;
        }

        if (c.VType == MRubyVType.SClass)
        {
            var attachedValue = c.InstanceVariables.Get(Names.AttachedKey);
            switch (attachedValue.VType)
            {
                case MRubyVType.Class:
                case MRubyVType.Module:
                case MRubyVType.SClass:
                    targetClass = attachedValue.As<RClass>();
                    break;
                default:
                    targetClass = c;
                    break;
            }
        }
        else if (c.VType == MRubyVType.IClass)
        {
            targetClass = c.Class;
        }
        else
        {
            targetClass = c;
        }

        EnsureNotFrozen(targetClass);
        targetClass.InstanceVariables.Set(id, value);
    }
}

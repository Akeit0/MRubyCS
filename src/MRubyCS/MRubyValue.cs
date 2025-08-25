using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MRubyCS.Internals;
using Utf8StringInterpolation;

namespace MRubyCS;

public enum MRubyVType
{
    Nil = 0,
    False,
    True,
    Symbol,
    Undef,
    Free,
    Float,
    Integer,
    CPtr,
    Object,
    Class,
    Module,
    IClass, // Include class
    SClass, // Singleton class
    Proc,
    Array,
    Hash,
    String,
    Range,
    Exception,
    Env,
    CData,
    Fiber,
    Struct,
    Istruct,
    Break,
    Complex,
    Rational,
    BigInt,
}

public static class MRubyVTypeExtensions
{
    public static ReadOnlySpan<byte> ToUtf8String(this MRubyVType vType)
    {
        return Utf8String.Format($"{vType}");
    }

    public static bool IsClass(this MRubyVType vType) => vType is MRubyVType.Class or MRubyVType.SClass or MRubyVType.Module;
}
[StructLayout(LayoutKind.Explicit)]
public readonly struct MRubyValue : IEquatable<MRubyValue>
{
    public static MRubyValue Nil => default;
    public static MRubyValue False => new(MRubyVType.False, 0);
    public static MRubyValue True => new(MRubyVType.True, 0);
    public static MRubyValue Undef => new(MRubyVType.Undef, 0);

    public static MRubyValue From(bool value) => new(value ? MRubyVType.True : MRubyVType.False, 0);

    public static MRubyValue From(RObject obj) => new(obj);
    public static MRubyValue From(RString obj) => new(obj, MRubyVType.String);
    public static MRubyValue From(RArray obj) => new(obj, MRubyVType.Array);
    public static MRubyValue From(RHash obj) => new(obj, MRubyVType.Hash);
    public static MRubyValue From(RRange obj) => new(obj, MRubyVType.Range);
    public static MRubyValue From(RClass obj) => new(obj);
    public static MRubyValue From(RProc obj) => new(obj, MRubyVType.Proc);
    public static MRubyValue From(RBreak obj) => new(obj, MRubyVType.Break);
    public static MRubyValue From(long value) => new(MRubyVType.Integer, value);
    public static MRubyValue From(Symbol symbol) => new(MRubyVType.Symbol, symbol.Value);

    public static MRubyValue From(double value)
    {
        // Assume that MRB_USE_FLOAT32 is not defined
        // Assume that MRB_WORDBOX_NO_FLOAT_TRUNCATE is not defined
        return new MRubyValue(MRubyVType.Float,
#if NET6_0_OR_GREATER
            Unsafe.BitCast<double, long>(value)
#else
            Unsafe.As<double, long>(ref value)
#endif
);
    }

    public RObject? Object
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Union.Object;
    }

    public MRubyVType VType => Union.IsType ? Union.TypeValue : Object!.VType;
    internal MRubyVType ImmediateVType => Union.TypeValue;
    internal nint RawUnionValue => Union.RawValue;
    public bool IsNil => Union.RawValue == 0;
    public bool IsFalse => Union.RawValue ==(long) MRubyVType.False;
    public bool IsTrue => Union.RawValue == (long)MRubyVType.True;
    public bool IsUndef => Union.RawValue == (long)MRubyVType.Undef;
    public bool IsSymbol => Union.RawValue == (long)MRubyVType.Symbol;
    public bool IsObject => Union.IsObject;
    public bool IsImmediate => Union.IsType;

    public T As<T>() where T : RObject => (T)Object!;
    internal T UnsafeAs<T>() where T : RObject => Unsafe.As<T>(Union.RawObject);

    public bool Truthy => 1 < Union.RawValue;
    public bool Falsy => (nuint)Union.RawValue <= 1;

    public bool IsInteger => Union.RawValue == (long)MRubyVType.Integer;
    public bool IsFloat => Union.RawValue ==(long) MRubyVType.Float;
    internal bool IsNumeric => (long)Union.RawValue is (long)MRubyVType.Integer or (long)MRubyVType.Float;
    public bool IsBreak => Union.IsObject && Bits == (long)MRubyVType.Break;
    public bool IsProc => Union.IsObject && Bits == (long)MRubyVType.Proc;
    public bool IsClass => Union.IsObject && Bits is (long)MRubyVType.Class or (long)MRubyVType.SClass or  (long)MRubyVType.Module;
    public bool IsNamespace => Union.IsObject && Bits is (long)MRubyVType.Class or (long)MRubyVType.Module;

    public bool BoolValue => (long)Union.RawValue is (long)MRubyVType.True or (long)MRubyVType.False;

    public Symbol SymbolValue => new((uint)Bits);

    public long IntegerValue => Bits;

    public double FloatValue =>
        // Assume that MRB_USE_FLOAT32 is not defined
        // Assume that MRB_WORDBOX_NO_FLOAT_TRUNCATE is not defined
        RawFloat;

    public long ObjectId
    {
        get
        {
            if (Union.IsObject) return Union.RawObject.GetHashCode();
            switch (Union.TypeValue)
            {
                case MRubyVType.Free:
                case MRubyVType.Undef: return 0;
                case MRubyVType.Symbol:
                case MRubyVType.Integer: return Bits;
                case MRubyVType.Float: return FloatValue.GetHashCode();
            }
            return Union.RawValue;
        }
    }

    [FieldOffset(0)]
    internal readonly TypeObjectUnion Union;
    [FieldOffset(8)]
    internal readonly long Bits;
    [FieldOffset(8)]
    internal readonly double RawFloat;

    MRubyValue(RObject obj)
    {
        Union = new(obj);
        Bits = (long)obj.VType;
    }

    MRubyValue(RObject obj, MRubyVType type)
    {
        Union = new(obj);
        Bits = (long)type;
    }

    MRubyValue(MRubyVType type, long bits)
    {
        Union = new(type);
        this.Bits = bits;
    }

    public bool Equals(MRubyValue other) => Bits == other.Bits &&
                                            Union == other.Union;

    public static bool operator ==(MRubyValue a, MRubyValue b) => a.Equals(b);
    public static bool operator !=(MRubyValue a, MRubyValue b) => !a.Equals(b);

    public override bool Equals(object? obj)
    {
        return obj is MRubyValue other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Object == null
            ? Bits.GetHashCode()
            : Object?.GetHashCode() ?? 0;
    }

    public override string ToString()
    {
        if (Object is { } x) return x.ToString()!;
        if (IsNil) return "nil";

        switch (VType)
        {
            case MRubyVType.False:
                return "false";
            case MRubyVType.True:
                return "true";
            case MRubyVType.Undef:
                return "undef";
            case MRubyVType.Symbol:
                return SymbolValue.ToString();
            case MRubyVType.Float:
                return FloatValue.ToString(CultureInfo.InvariantCulture);
            case MRubyVType.Integer:
                return IntegerValue.ToString(CultureInfo.InvariantCulture);
            default:
                return VType.ToString();
        }
    }
}
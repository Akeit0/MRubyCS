using System.Runtime.InteropServices;

namespace MRubyCS.Internals;

[StructLayout(LayoutKind.Explicit)]
internal struct MRubyValueImmediateUnsafeAlias
{
    [FieldOffset(0)]
    public nuint Type;

    [FieldOffset(8)]
    public long Bits;

    [FieldOffset(8)]
    public double Float;
}

[StructLayout(LayoutKind.Explicit)]
internal struct MRubyValueObjectUnsafeAlias
{
    [FieldOffset(0)]
    public object Object;

    [FieldOffset(8)]
    public nuint Type;
}
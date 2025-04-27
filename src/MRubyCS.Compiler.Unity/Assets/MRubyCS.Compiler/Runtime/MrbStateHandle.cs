using System;
using System.Runtime.InteropServices;

namespace MRubyCS.Compiler
{
class MrbStateHandle : SafeHandle
{
    public MrbStateHandle(IntPtr invalidHandleValue) : base(invalidHandleValue, true)
    {
    }

    public static unsafe MrbStateHandle Create()
    {
        var ptr = NativeMethods.MrbOpen();
        return new MrbStateHandle((IntPtr)ptr);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public unsafe MrbStateNative* DangerousGetPtr() => (MrbStateNative*)DangerousGetHandle();

    protected override unsafe bool ReleaseHandle()
    {
        if (IsClosed) return false;
        NativeMethods.MrbClose(DangerousGetPtr());
        return true;
    }
}
}

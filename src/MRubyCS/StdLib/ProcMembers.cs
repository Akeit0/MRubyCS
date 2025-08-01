namespace MRubyCS.StdLib;

static class ProcMembers
{
    [MRubyMethod(BlockArgument = true)]
    public static MRubyMethod New = new((state, self) =>
    {
        var block = state.GetBlockArgument(false);
        var proc = block!.Dup();
        var procValue = MRubyValue.From(proc);
        state.Send(procValue, Names.Initialize, procValue);
        if (!proc.HasFlag(MRubyObjectFlags.ProcStrict) &&
            state.CheckProcIsOrphan(proc))
        {
            proc.SetFlag(MRubyObjectFlags.ProcOrphan);
        }
        return procValue;
    });

    [MRubyMethod(RequiredArguments = 1)]
    public static MRubyMethod Eql = new((state, self) =>
    {
        var other = state.GetArgumentAt(0);
        if (other.VType != MRubyVType.Proc)
        {
            return MRubyValue.False;
        }
        return MRubyValue.From(self.As<RProc>() == other.As<RProc>());
    });
}

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// Single point where entity identifiers are generated.
    /// <para>
    /// Version 7 rather than version 4: a random v4 primary key scatters inserts
    /// across the index and fragments it as the table grows, while v7 is
    /// time-ordered and appends. The difference is invisible at development
    /// scale and expensive to reverse afterwards.
    /// </para>
    /// <para>
    /// The layout was written out here by hand until 2026-08-29, because
    /// <c>Guid.CreateVersion7()</c> arrived in .NET 9 and the project targeted
    /// .NET 8. **The wrapper stays** rather than being inlined at the forty-odd
    /// call sites: the sentence above is a decision, and a decision wants one
    /// place to live.
    /// </para>
    /// </summary>
    public static class Uuid
    {
        public static Guid New() => Guid.CreateVersion7();
    }
}

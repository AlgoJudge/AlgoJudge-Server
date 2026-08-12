namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// What an operator sees about storage. **Never served publicly** — every
    /// field here is something A65c keeps out of an anonymous answer.
    /// </summary>
    public record StorageReportDto
    {
        public required IReadOnlyList<StoreStatusDto> Stores { get; init; }

        /// <summary>
        /// Store ids that files name and the configuration does not have.
        /// <para>
        /// Empty is the ordinary state. Anything here means those files answer
        /// <c>503</c> until the store returns under the same id — a store id is
        /// permanent once a row names it, and this is what it looks like when
        /// that rule was broken.
        /// </para>
        /// </summary>
        public required IReadOnlyList<string> Unconfigured { get; init; }
    }

    public record StoreStatusDto
    {
        public required string Id { get; init; }

        /// <summary>Whether the store answered at all.</summary>
        public required bool Reachable { get; init; }

        /// <summary>Whether a write, a read back and a checksum comparison agreed.</summary>
        public required bool SmokeTestPassed { get; init; }

        /// <summary>What went wrong, when something did. Absent when nothing did.</summary>
        public string? Detail { get; init; }

        /// <summary>How many files name this store. <b>Zero means safe to switch off.</b></summary>
        public required long Files { get; init; }

        public required long SizeBytes { get; init; }

        /// <summary>Whether new writes go here.</summary>
        public required bool IsDefault { get; init; }
    }
}

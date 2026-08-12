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

        /// <summary>
        /// The live migration, or the last one. Absent on an installation that
        /// has never run one — §11 asks for the progress of a running migration,
        /// and "there isn't one" is best said by there being nothing here.
        /// </summary>
        public StorageMigrationDto? Migration { get; init; }
    }

    public record StorageMigrationDto
    {
        public required string Id { get; init; }

        /// <summary>`requested`, `running`, `finished`, `refused` or `cancelled`.</summary>
        public required string State { get; init; }

        public required string TargetStoreId { get; init; }
        public required string RequestedAt { get; init; }
        public string? StartedAt { get; init; }
        public string? FinishedAt { get; init; }

        public required int FilesMoved { get; init; }
        public required long BytesMoved { get; init; }

        /// <summary>How many files are not on the target yet.</summary>
        public required long FilesRemaining { get; init; }

        /// <summary>
        /// What it is waiting for, or why it stopped. The answer to "why is it
        /// not moving", which is the question an operator actually has.
        /// </summary>
        public string? Detail { get; init; }
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

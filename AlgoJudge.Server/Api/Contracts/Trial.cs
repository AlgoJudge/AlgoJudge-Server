namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// A package somebody asked to have run, attached to no problem.
    /// <para>
    /// Deliberately not shaped like a submission (D-9). There is no score, no
    /// verdict and no problem — a trial answers "how long does this take", and
    /// giving it a verdict-shaped body would invite a screen to render it as a
    /// result.
    /// </para>
    /// </summary>
    public record TrialDto
    {
        public required string Id { get; init; }
        /// <summary>
        /// Absent when the trial was asked for against the **library** — a
        /// manager calibrating a problem that belongs to no activity yet.
        /// </summary>
        public string? ActivityId { get; init; }
        /// <summary>`queued` | `running` | `completed` | `failed` | `cancelled`.</summary>
        public required string State { get; init; }
        public required string ProblemType { get; init; }
        public required string CreatedAt { get; init; }
        public string? FinishedAt { get; init; }

        /// <summary>
        /// Why it failed, where it did. **Never a verdict** — a trial that could
        /// not run is a broken run, not a wrong answer.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// What was measured, as the problem type states it. Opaque here: the
        /// Server stores the string and never reads it, which is the same rule
        /// that lets a type be added without a Server release.
        /// </summary>
        public string? Measurement { get; init; }

        /// <summary>
        /// Whether the uploaded package still exists.
        /// <para>
        /// False once the trial finishes, because the bytes are removed then
        /// (D-12). Sent so a screen can say "measured, package gone" rather than
        /// offering a download that would 404.
        /// </para>
        /// </summary>
        public required bool HasPackage { get; init; }
    }

    /// <summary>
    /// What is asked for. The bytes are already stored; this names them.
    /// </summary>
    public record NewTrialDto
    {
        /// <summary>Matched against a Runner's own types by equality, never parsed here.</summary>
        public required string ProblemType { get; init; }
        public required Guid PackageFileId { get; init; }
        /// <summary>
        /// Where permission is asked for. **Absent means the library**, and
        /// then `trial:run` has to be held globally — which is what a manager
        /// calibrating a problem before it is attached anywhere needs.
        /// </summary>
        public string? ActivityIdOrSlug { get; init; }
    }

    /// <summary>What a Runner is handed when it claims a trial.</summary>
    public record ClaimedTrialDto
    {
        public required string TrialId { get; init; }
        public required string LeaseToken { get; init; }
        public required string LeaseExpiresAt { get; init; }
        public required string ProblemType { get; init; }
        public required string PackageFileId { get; init; }
        public required string PackageSha256 { get; init; }
    }

    /// <summary>
    /// What a Runner says when it is done.
    /// <para>
    /// One shape for both outcomes: either a measurement arrived or a reason it
    /// did not. Never both, and never a score.
    /// </para>
    /// </summary>
    public record TrialReportInputDto
    {
        public required string LeaseToken { get; init; }
        public string? Measurement { get; init; }
        public string? FailureReason { get; init; }
    }

    /// <summary>
    /// A renewed lease. Its own record rather than `LeaseDto`, which names a
    /// job: a reply that called a trial a job would be the first step towards
    /// treating it as one.
    /// </summary>
    public record TrialLeaseDto
    {
        public required string TrialId { get; init; }
        public required string LeaseToken { get; init; }
        public required string LeaseExpiresAt { get; init; }
    }

    public record TrialReportAcceptedDto
    {
        public required string TrialId { get; init; }
        public required string State { get; init; }
        /// <summary>True when this report was already recorded. Reporting twice is not an error.</summary>
        public required bool Duplicate { get; init; }
    }
}

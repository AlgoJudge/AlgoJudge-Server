namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// The manager-facing wire contract for the M1 slice, mirroring
    /// `AlgoJudge-Client/src/api/ManagerApi.ts`. Only what the end-to-end path
    /// needs is here; the rest of the panel arrives with M3.
    /// </summary>

    public record PermissionDefinitionDto
    {
        public required string Key { get; init; }
        /// <summary>`global` | `activity` | `both`.</summary>
        public required string Scope { get; init; }
        public required string Group { get; init; }
        /// <summary>
        /// Whether the participant template grants it by default — what the
        /// editor starts a new participant with.
        /// </summary>
        public required bool Participant { get; init; }
        /// <summary>
        /// Whether a grant carrying it is systemic, and so forces `isSystem`
        /// and leaves the participant count and the ranking.
        /// <para>
        /// Published rather than inferred from `participant`. The two differ
        /// for `trial:run` — outside the default template, held without
        /// ceasing to compete — and a Client negating the other flag draws a
        /// switch the Server disagrees with.
        /// </para>
        /// </summary>
        public required bool Systemic { get; init; }
    }

    public record AttachmentRuleDto
    {
        /// <summary>The name within the submission — `source`, `log`, `details`.</summary>
        public required string Name { get; init; }
        /// <summary>`managersOnly` | `participant`.</summary>
        public required string Visibility { get; init; }
    }

    /// <summary>
    /// The lookup a picker cannot do without — deliberately not the full model.
    /// </summary>
    public record ManagedActivitySummaryDto
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
    }

    public record ManagedActivityDto
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public required string Type { get; init; }
        public required string RankingType { get; init; }
        public required string TimeZone { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        public required ActivityModulesDto Modules { get; init; }
        public required IReadOnlyList<ActivityDocumentRefDto> Documents { get; init; }
        public required string ScoreVisibility { get; init; }
        /// <summary>One row per name. <b>A name with no row is `managersOnly`.</b></summary>
        public required IReadOnlyList<AttachmentRuleDto> AttachmentVisibility { get; init; }
        /// <summary>Display metadata. Opaque — absent means none, never `{}`.</summary>
        public object? Props { get; init; }
        public required string JoinPolicy { get; init; }
        public required bool Unlisted { get; init; }
        /// <summary>A join code, not a credential. A manager reads it back on purpose.</summary>
        public string? JoinPassword { get; init; }
        public required bool HideEndedSeriesProblems { get; init; }
        public required long MaxUploadBytes { get; init; }
        public required int MaxAttachments { get; init; }
        public int? MaxSubmissionsPerProblem { get; init; }
        public string? ArchivedAt { get; init; }
        public required int SeriesCount { get; init; }
        public required int ProblemCount { get; init; }
        /// <summary>Read from the grants, never stored, and staff are excluded.</summary>
        public required int ParticipantCount { get; init; }

        /// <summary>Which Runners judge this activity. Empty is the default pool.</summary>
        public required IReadOnlyList<string> RunnerTags { get; init; }

        /// <summary>
        /// How many approved Runners those tags reach.
        /// <para>
        /// <b>It counts the tags and nothing else</b> — not problem types, not
        /// whether a Runner forwards work. A larger number than zero is
        /// therefore not a promise that anything can be judged; zero is a
        /// promise that nothing can, which is the answer worth having on the
        /// screen where the tags are typed.
        /// </para>
        /// </summary>
        public required int MatchingRunners { get; init; }
    }

    public record ActivityInputDto
    {
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public required string Type { get; init; }
        public required string RankingType { get; init; }
        public required string TimeZone { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        public ActivityModulesDto? Modules { get; init; }
        public string? ScoreVisibility { get; init; }
        public IReadOnlyList<AttachmentRuleDto>? AttachmentVisibility { get; init; }
        /// <summary>
        /// Display metadata, and the <b>first</b> way of writing it: this field
        /// did not exist, so `Activity.Props` was readable by participants and
        /// writable by nobody.
        /// </summary>
        public object? Props { get; init; }
        public string? JoinPolicy { get; init; }
        public bool? Unlisted { get; init; }
        /// <summary>Absent or empty removes it. Meaningful only under `password`.</summary>
        public string? JoinPassword { get; init; }
        public bool? HideEndedSeriesProblems { get; init; }
        public long? MaxUploadBytes { get; init; }
        public int? MaxAttachments { get; init; }
        public int? MaxSubmissionsPerProblem { get; init; }

        /// <summary>
        /// Which Runners judge this activity. Absent leaves it alone; empty is
        /// the default pool. A round may override it.
        /// </summary>
        public IReadOnlyList<string>? RunnerTags { get; init; }
    }

    public record ManagedSeriesProblemDto
    {
        public required string Id { get; init; }
        public required string SeriesId { get; init; }
        public required string ProblemId { get; init; }
        public required string ProblemSlug { get; init; }
        public required string ProblemName { get; init; }
        /// <summary>Unique across the whole activity, which the database enforces.</summary>
        public required string Slug { get; init; }
        public string? Name { get; init; }
        public required int Order { get; init; }
        /// <summary>Set when the problem is attached, to the library's current version.</summary>
        public string? PinnedProblemVersionId { get; init; }
        public int? PinnedVersion { get; init; }
        public required int CurrentVersion { get; init; }
        /// <summary>Nothing judges without one.</summary>
        public required bool HasPackage { get; init; }
        /// <summary>Detaching is refused above zero.</summary>
        public required int SubmissionCount { get; init; }
        /// <summary>
        /// Anything that changes the verdict — limits, the allowed languages.
        /// Opaque; absent means none, never `{}`.
        /// </summary>
        public object? Config { get; init; }
        /// <summary>What the Client draws and validates the submit form from.</summary>
        public object? Spec { get; init; }
        /// <summary>Display only. Wrong here means an ugly screen and nothing else.</summary>
        public object? Props { get; init; }
        /// <summary>A point value, not a multiplier. Absent keeps the problem's own scale.</summary>
        public int? MaxPoints { get; init; }
        public long? MaxUploadBytes { get; init; }
        public int? MaxAttachments { get; init; }
        public int? MaxSubmissions { get; init; }
    }

    public record ManagedSeriesDto
    {
        public required string Id { get; init; }
        public required string ActivityId { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public required int Order { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        public required bool IsOpen { get; init; }
        public string? PausedAt { get; init; }
        /// <summary>
        /// Whether the pause in force took the statements with it.
        /// <para>
        /// A manager answers this as they pause, and until 2026-08-08 nothing
        /// ever told them what they had answered: reloading the screen lost it.
        /// Manager-only — the participant's <c>SeriesDto</c> has no counterpart,
        /// because the Server withholds by leaving <c>problems</c> out rather
        /// than by asking the reader to.
        /// </para>
        /// </summary>
        public required bool HideProblemsWhilePaused { get; init; }
        public required bool RevealProblemCount { get; init; }
        public string? RankingFreezeAt { get; init; }
        public string? RankingRevealAt { get; init; }
        public string? RankingVisibleFrom { get; init; }
        public string? RankingVisibleTo { get; init; }

        /// <summary>
        /// How much this round outranks the rest while it runs. A number, and
        /// the number is the meaning — the Client names it and prints the rank
        /// beside the name, because a manager choosing one has to see what it
        /// loses to. `docs/specs/SERIES_LOCKDOWN.md`.
        /// </summary>
        public required int Importance { get; init; }

        /// <summary>
        /// How far that rank reaches: `activity` for this activity's own rounds,
        /// `installation` for everything the reader takes part in.
        /// </summary>
        public required string ImportanceScope { get; init; }

        /// <summary>The ranges it may be reached from. Empty means anywhere.</summary>
        public required IReadOnlyList<AddressRuleDto> AddressRules { get; init; }

        /// <summary>
        /// Off, this round neither hides nor locks — and keeps its rules, so
        /// turning it back on restores them.
        /// </summary>
        public required bool RestrictionsEnabled { get; init; }

        /// <summary>
        /// This round's own Runner pools, or **null** where it takes its
        /// activity's. Null and empty are the same answer; a round that wants
        /// the general Runners while its activity is pinned carries `default`.
        /// </summary>
        public IReadOnlyList<string>? RunnerTags { get; init; }

        /// <summary>
        /// How many approved Runners the tags in force here reach. See
        /// <see cref="ManagedActivityDto.MatchingRunners"/> for what it does not
        /// count.
        /// </summary>
        public required int MatchingRunners { get; init; }

        public required IReadOnlyList<ManagedSeriesProblemDto> Problems { get; init; }
    }

    public record AddressRuleDto
    {
        /// <summary>`10.0.5.0/24`, `2001:db8::/32`. A single machine is a `/32`.</summary>
        public required string Network { get; init; }
        /// <summary>What a manager calls it — a room number, a building.</summary>
        public string? Note { get; init; }
    }

    public record SeriesInputDto
    {
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        public bool? RevealProblemCount { get; init; }
        public string? RankingFreezeAt { get; init; }
        public string? RankingRevealAt { get; init; }
        public string? RankingVisibleFrom { get; init; }
        public string? RankingVisibleTo { get; init; }

        /// <summary>
        /// Absent leaves it as it is; on a new round that means `normal`.
        /// Refused with the rules below on a round without both dates.
        /// </summary>
        public int? Importance { get; init; }

        /// <summary>`activity` or `installation`. Absent leaves it as it is.</summary>
        public string? ImportanceScope { get; init; }

        /// <summary>
        /// The whole list, replaced — as `sharedWith` is on a problem. Absent
        /// leaves it alone; empty clears it.
        /// </summary>
        public IReadOnlyList<AddressRuleDto>? AddressRules { get; init; }

        public bool? RestrictionsEnabled { get; init; }

        /// <summary>
        /// Which Runners judge this round, overriding the activity's. Absent
        /// leaves it alone; **empty goes back to inheriting**, as `addressRules`
        /// above clears itself.
        /// <para>
        /// A round that wants the general Runners while its activity is pinned
        /// writes `["default"]` rather than an empty list — the two would
        /// otherwise be two spellings of one state and one spelling of another.
        /// </para>
        /// </summary>
        public IReadOnlyList<string>? RunnerTags { get; init; }
    }

    public record SeriesProblemInputDto
    {
        public required string ProblemId { get; init; }
        public required string Slug { get; init; }
        public string? Name { get; init; }
        public string? PinnedProblemVersionId { get; init; }
        public object? Config { get; init; }
        public object? Spec { get; init; }
        public object? Props { get; init; }
        public int? MaxPoints { get; init; }
        public long? MaxUploadBytes { get; init; }
        public int? MaxAttachments { get; init; }
        public int? MaxSubmissions { get; init; }
    }

    public record ManagedProblemDto
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public required string Type { get; init; }
        /// <summary>
        /// Whether judging it sends the submission outside this installation.
        /// Set at creation and permanent.
        /// </summary>
        public required bool External { get; init; }
        public required string OwnerUserId { get; init; }
        public required string OwnerName { get; init; }
        /// <summary>`private` | `shared` | `instance`.</summary>
        public required string Visibility { get; init; }
        public required IReadOnlyList<string> SharedWith { get; init; }
        public string? ArchivedAt { get; init; }
        public required int CurrentVersion { get; init; }
        public required int VersionCount { get; init; }
        public required string CreatedAt { get; init; }
        /// <summary>Deletion is refused while this is above zero.</summary>
        public required int AttachedCount { get; init; }
    }

    public record ProblemInputDto
    {
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public required string Type { get; init; }

        /// <summary>
        /// Whether judging it sends the submission outside this installation.
        /// <para>
        /// Read at creation and never again — there is no endpoint that changes
        /// it. A local equivalent of an external problem is a new problem with
        /// its own slug, which is a thing somebody decides rather than a
        /// checkbox they clear.
        /// </para>
        /// </summary>
        public bool External { get; init; }
    }

    public record ProblemFileDto
    {
        public required string Name { get; init; }
        /// <summary>`participant` | `manager` | `runner`.</summary>
        public required string Scope { get; init; }
        public required string MimeType { get; init; }
        public required long SizeBytes { get; init; }
        public required string Sha256 { get; init; }
        public string? Url { get; init; }
    }

    public record ManagedProblemVersionDto
    {
        public required string Id { get; init; }
        public required int Version { get; init; }
        public required string CreatedAt { get; init; }
        public string? CreatedByName { get; init; }
        public string? Note { get; init; }
        /// <summary>
        /// What the problem type needs about this version — `uva@1`'s archive
        /// problem number, for instance. Opaque; absent means none, never `{}`.
        /// <b>Not a configuration layer</b>: the chain is the package and the
        /// assignment.
        /// </summary>
        public object? Props { get; init; }
        public required bool HasPackage { get; init; }
        public required IReadOnlyList<ProblemFileDto> Files { get; init; }
    }

    /// <summary>
    /// A statement, as a reference. The name follows from the language, so
    /// nobody types it and nobody can mistype it.
    /// </summary>
    public record NewStatementDto
    {
        public string? Language { get; init; }
        public required string FileId { get; init; }
    }

    public record NewProblemFileDto
    {
        public required string FileId { get; init; }
        /// <summary>The name within this version. One already held is refused.</summary>
        public required string Name { get; init; }
        public required string Scope { get; init; }
    }

    public record NewProblemPackageDto
    {
        public required string FileId { get; init; }
        /// <summary>The example tests as the participant receives them.</summary>
        public string? SamplesFileId { get; init; }
    }

    /// <summary>
    /// A version is published whole, in one request. Everything travels as a
    /// reference, the statement included.
    /// </summary>
    public record ProblemVersionInputDto
    {
        public string? Note { get; init; }
        /// <summary>Absent carries the previous forward. An empty array is refused.</summary>
        public IReadOnlyList<NewStatementDto>? Statements { get; init; }
        /// <summary>Absent carries the previous version's forward.</summary>
        public object? Props { get; init; }
        public IReadOnlyList<NewProblemFileDto>? Files { get; init; }
        public IReadOnlyList<string>? RemovedFiles { get; init; }
        public NewProblemPackageDto? Package { get; init; }
    }
}

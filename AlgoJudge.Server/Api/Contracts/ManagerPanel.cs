namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// The rest of the manager surface, mirroring `ManagerApi.ts`.
    /// </summary>

    // ── Permission templates and grants ──────────────────────────────────────

    public record PermissionTemplateDto
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required IReadOnlyList<string> Permissions { get; init; }
        /// <summary>One of the three shipped. Deleting one is refused.</summary>
        public required bool IsBuiltIn { get; init; }
    }

    public record PermissionTemplateInputDto
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required IReadOnlyList<string> Permissions { get; init; }
    }

    public record GrantDto
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        /// <summary>
        /// Sent rather than looked up elsewhere, so what a row shows does not
        /// depend on that person happening to be in some other answer.
        /// </summary>
        public required string UserName { get; init; }
        public required string UserLogin { get; init; }
        public string? ActivityId { get; init; }
        public string? ActivityName { get; init; }
        public required IReadOnlyList<string> Permissions { get; init; }
        /// <summary>
        /// A membership that runs the activity rather than takes part in it.
        /// <b>Forced true for a staff grant</b>, and the Server decides.
        /// </summary>
        public required bool IsSystem { get; init; }
        /// <summary>Where the set started. Informational — <b>not</b> a reference.</summary>
        public string? CreatedFromTemplate { get; init; }
        /// <summary>`invited` | `active`.</summary>
        public required string State { get; init; }
        public required string CreatedAt { get; init; }

        /// <summary>
        /// `manual` | `provider`. **Where this contribution came from.**
        /// <para>
        /// At system scope a person's permissions are the union of one manual
        /// contribution and one per linked provider, so no single row is the
        /// answer to "what may they do" — and a screen that cannot say where a
        /// right came from is one nobody can act on.
        /// </para>
        /// </summary>
        public required string Source { get; init; }
        public string? SourceProviderId { get; init; }
        /// <summary>The provider's display name, so a row reads without a second lookup.</summary>
        public string? SourceProviderName { get; init; }

        /// <summary>
        /// Whether this contribution is rewritten from its provider's mapping at
        /// every sign-in — and therefore **not editable here**. True exactly when
        /// `source` is `provider`; sent as its own field so a screen disables a
        /// control on a fact rather than on a string comparison.
        /// </summary>
        public required bool Managed { get; init; }

        /// <summary>
        /// This activity grant is authoritative inside its activity, and system
        /// contributions do not reach it. **Setting it on somebody who holds
        /// system permissions demotes them there**, and the screen has to say so
        /// at the moment of the act.
        /// </summary>
        public required bool OverrideSystem { get; init; }
    }

    public record GrantInputDto
    {
        public required string UserId { get; init; }
        public string? ActivityId { get; init; }
        public required IReadOnlyList<string> Permissions { get; init; }
        /// <summary>Ignored where the permissions already settle it. The Server decides.</summary>
        public bool? IsSystem { get; init; }
        public string? CreatedFromTemplate { get; init; }
        public string? State { get; init; }

        /// <summary>
        /// Make this activity grant authoritative inside its activity. Ignored at
        /// system scope, where there is nothing to override.
        /// <para>
        /// There is deliberately **no** field naming a provider: this endpoint
        /// writes the manual contribution and only that one. A managed
        /// contribution belongs to its provider's mapping and is rewritten at
        /// every sign-in.
        /// </para>
        /// </summary>
        public bool? OverrideSystem { get; init; }
    }

    // ── Users ────────────────────────────────────────────────────────────────

    public record ManagedUserSummaryDto
    {
        public required string Id { get; init; }
        public required string Username { get; init; }
        public required string Name { get; init; }
        public string? Email { get; init; }
    }

    public record ManagedUserDto
    {
        public required string Id { get; init; }
        /// <summary>The only required identifier, and fixed at creation.</summary>
        public required string Username { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string? Email { get; init; }
        /// <summary>Distinct from approval on purpose: two facts, two fields.</summary>
        public required bool EmailConfirmed { get; init; }
        /// <summary>Absent means <b>pending</b>.</summary>
        public string? ApprovedAt { get; init; }
        /// <summary>A sentence about the account, written by staff. Not a tag.</summary>
        public string? Note { get; init; }
        public required IReadOnlyList<string> Tags { get; init; }
        public required bool IsTemporary { get; init; }
        public string? ExpiresAt { get; init; }
        /// <summary>Blocking is `LockoutEnd`, never a second boolean.</summary>
        public string? BlockedAt { get; init; }
        public string? BlockedReason { get; init; }
        public required string CreatedAt { get; init; }
        public string? LastSeenAt { get; init; }
        /// <summary>How many scopes they hold a grant in, the system scope included.</summary>
        public required int GrantCount { get; init; }
    }

    public record UserInputDto
    {
        public required string Username { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string? Email { get; init; }
    }

    /// <summary>The username is excluded: it is fixed at creation, as a slug is.</summary>
    public record UserUpdateInputDto
    {
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string? Email { get; init; }
        public string? Note { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
    }

    public record BulkUserInputDto
    {
        /// <summary>`contest` gives `contest-001`, `contest-002`, …</summary>
        public required string Prefix { get; init; }
        public required int Count { get; init; }
        public string? ExpiresAt { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
        /// <summary>Enrol them all into one activity as they are created.</summary>
        public string? ActivityId { get; init; }
        /// <summary>Ignored without an activity.</summary>
        public IReadOnlyList<string>? Permissions { get; init; }
    }

    /// <summary>
    /// Handed over once. The Server keeps a hash; this is the only readable copy.
    /// </summary>
    public record CreatedCredentialDto
    {
        public required string UserId { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
    }

    public record UserSessionDto
    {
        public required string Id { get; init; }
        /// <summary>
        /// How many WebSockets the Server holds for this session <b>at the moment
        /// it answered</b>. Connection state, not stored state — zero means
        /// signed in but not connected.
        /// </summary>
        public required int Connections { get; init; }
        public required string StartedAt { get; init; }
        public string? LastRequestAt { get; init; }
        /// <summary>An API path, not the screen somebody was looking at.</summary>
        public string? LastRequestPath { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
        public string? ExpiresAt { get; init; }
        public required bool IsCurrent { get; init; }
    }

    // ── Runners ──────────────────────────────────────────────────────────────

    public record RunnerAttachmentDto
    {
        public required string Id { get; init; }
        /// <summary>The name is the tab's label.</summary>
        public required string Name { get; init; }
        public required string MimeType { get; init; }
        public required long SizeBytes { get; init; }
        public required string Sha256 { get; init; }
        public required string UploadedAt { get; init; }
    }

    public record MachineDto
    {
        public string? Os { get; init; }
        public string? Cpu { get; init; }
        public int? Cores { get; init; }
        /// <summary>How much memory the machine has, in **bytes**.</summary>
        public long? MemoryBytes { get; init; }
    }

    public record ManagedRunnerDto
    {
        public required string Id { get; init; }
        /// <summary>Reported by the Runner. Not unique, and not an identifier.</summary>
        public required string Name { get; init; }
        public required string Product { get; init; }
        public required string Version { get; init; }
        /// <summary>Matched by equality. Never parsed.</summary>
        public required IReadOnlyList<string> ProblemTypes { get; init; }
        /// <summary>
        /// Whether it sends submissions outside the installation. Shown because
        /// approval is the moment an administrator decides to trust it with
        /// somebody else's work leaving the building.
        /// </summary>
        public required bool External { get; init; }
        /// <summary>Free labels an operator sets.</summary>
        public required IReadOnlyList<string> Tags { get; init; }
        /// <summary>Where the Server saw the connection come from. Never reported.</summary>
        public required string Address { get; init; }
        public required string PublicKey { get; init; }
        public required string Fingerprint { get; init; }
        /// <summary>`pendingApproval` | `approved` | `revoked`.</summary>
        public required string State { get; init; }
        /// <summary>Says nothing about approval.</summary>
        public required bool IsConnected { get; init; }
        public string? LastSeenAt { get; init; }
        public required string RegisteredAt { get; init; }
        public string? ApprovedAt { get; init; }
        public string? RevokedAt { get; init; }
        public string? RevokedReason { get; init; }
        public MachineDto? Machine { get; init; }
        public string? CurrentSubmissionId { get; init; }
        public required int CompletedJobs { get; init; }
        public required IReadOnlyList<RunnerAttachmentDto> Attachments { get; init; }
    }

    public record RevokeRunnerInputDto
    {
        public string? Reason { get; init; }
    }

    public record RunnerTagsInputDto
    {
        public required IReadOnlyList<string> Tags { get; init; }
    }

    // ── Questions, as a manager sees them ───────────────────────────────────

    public record ManagedQuestionDto
    {
        public required string Id { get; init; }
        public required string ActivityId { get; init; }
        public required string ActivitySlug { get; init; }
        public required string Kind { get; init; }
        public required string Topic { get; init; }
        public required string Body { get; init; }
        /// <summary>Absent for an announcement: nobody asked it.</summary>
        public string? AuthorUserId { get; init; }
        public string? AuthorName { get; init; }
        public required string CreatedAt { get; init; }
        public string? SeriesId { get; init; }
        public string? SeriesName { get; init; }
        public string? SeriesProblemId { get; init; }
        public string? ProblemSlug { get; init; }
        public string? ProblemName { get; init; }
        public QuestionAnswerDto? Answer { get; init; }
        /// <summary>Published means every participant sees it, not only the asker.</summary>
        public required bool IsPublished { get; init; }
        /// <summary>Only meaningful once published.</summary>
        public required int ReadCount { get; init; }
    }

    public record AnswerInputDto
    {
        public required string Body { get; init; }
        /// <summary>Answer and publish in one act.</summary>
        public bool? Publish { get; init; }
    }

    public record AnnouncementInputDto
    {
        public required string Topic { get; init; }
        public required string Body { get; init; }
        public string? SeriesId { get; init; }
    }

    public record PublishInputDto
    {
        public required bool Published { get; init; }
    }

    // ── Submissions, as a manager sees them ─────────────────────────────────

    public record ManagedSubmissionDto
    {
        public required string Id { get; init; }
        public required string ActivityId { get; init; }
        public required string ActivitySlug { get; init; }
        public required string SeriesId { get; init; }
        public required string SeriesName { get; init; }
        /// <summary>The assignment, not the library entry.</summary>
        public required string SeriesProblemId { get; init; }
        public required string ProblemSlug { get; init; }
        public required string ProblemName { get; init; }
        public required string UserId { get; init; }
        public required string UserName { get; init; }
        public required string SubmittedAt { get; init; }
        public string? Language { get; init; }
        public required string State { get; init; }
        public string? Verdict { get; init; }
        public double? Score { get; init; }
        public double? MaxScore { get; init; }
        /// <summary>How many evaluation jobs it has had. A rejudge adds one.</summary>
        public required int Attempts { get; init; }
    }

    /// <summary>The unit a rejudge creates and a cancellation stops.</summary>
    public record ManagedAttemptDto
    {
        public required string Id { get; init; }
        public required int Attempt { get; init; }
        public required string State { get; init; }
        public required string StartedAt { get; init; }
        public string? FinishedAt { get; init; }
        public string? RunnerName { get; init; }
        /// <summary>A manager sees every one of them, whatever the activity's table says.</summary>
        public required IReadOnlyList<SubmissionFileDto> Files { get; init; }
    }

    public record ManagedSubmissionDetailDto : ManagedSubmissionDto
    {
        public required string ProblemType { get; init; }
        /// <summary>Newest first.</summary>
        public required IReadOnlyList<ManagedAttemptDto> AttemptList { get; init; }
        public required IReadOnlyList<SubmissionFileDto> Files { get; init; }
    }

    // ── Small inputs ────────────────────────────────────────────────────────

    public record ArchivedInputDto
    {
        public required bool Archived { get; init; }
    }

    public record BlockedInputDto
    {
        public required bool Blocked { get; init; }
        public string? Reason { get; init; }
    }

    public record VisibilityInputDto
    {
        /// <summary>`private` | `shared` | `instance`.</summary>
        public required string Visibility { get; init; }
        public IReadOnlyList<string>? SharedWith { get; init; }
    }

    public record OrderInputDto
    {
        public required IReadOnlyList<string> OrderedIds { get; init; }
    }

    /// <summary>
    /// A delta, not two dates: two managers moving the same delayed round by ten
    /// minutes would otherwise both compute +10 from what they read, and one of
    /// the shifts would be lost.
    /// </summary>
    public record ShiftInputDto
    {
        public required int Minutes { get; init; }
    }

    public record PauseInputDto
    {
        /// <summary>Take the statements away as well, not only the clock.</summary>
        public required bool HideProblems { get; init; }
    }

    public record ResumeInputDto
    {
        /// <summary>Move the end by however long the pause lasted.</summary>
        public required bool ExtendEnd { get; init; }
    }

    /// <summary>
    /// Where this installation may fetch a document from, and whether it may at
    /// all. Manager-only — the destinations are operational detail, unlike the
    /// boolean, which every screen may read.
    /// </summary>
    /// <summary>
    /// A named secret is set, and when. <b>Never its value.</b> An administrator
    /// needs to know one is configured, which is a different question from what
    /// it is.
    /// </summary>
    public record AccessKeyDto
    {
        public required string Name { get; init; }
        public required string UpdatedAt { get; init; }
    }

    public record AccessKeyInputDto
    {
        /// <summary>Empty removes it, which is how an installation stops holding a secret.</summary>
        public required string Value { get; init; }
    }

    /// <summary>
    /// The one answer that carries a secret. Handed only to a caller whose
    /// permission covers what the key is for.
    /// </summary>
    public record AccessKeyValueDto
    {
        public required string Name { get; init; }
        public required string Value { get; init; }
    }

    public record ExternalContentDto
    {
        /// <summary>The instance switch. Read-only here; it is set with the rest of the settings.</summary>
        public required bool Enabled { get; init; }
        public required IReadOnlyList<string> Hosts { get; init; }
    }

    public record ExternalContentInputDto
    {
        /// <summary>The whole list. An empty one means this installation fetches nothing.</summary>
        public required IReadOnlyList<string> Hosts { get; init; }
    }

    public record InstanceSettingsInputDto
    {
        public string? Name { get; init; }
        public required bool LocalRegistrationEnabled { get; init; }
        public required bool RequireEmail { get; init; }
        public required bool RequireConfirmedEmail { get; init; }
        public required bool ShowLogo { get; init; }
        public required bool ShowLocalSignIn { get; init; }

        /// <summary>
        /// Whether a person may remove their own account. **Shipped on** — it is
        /// a data-protection right before it is a feature — and settable because
        /// a setting nothing can change is not a setting.
        /// </summary>
        public required bool AccountDeletionEnabled { get; init; }

        /// <summary>
        /// Whether this installation may send submissions to a service it does
        /// not run. **Shipped off**, so the privacy paragraph it needs belongs to
        /// whoever turns it on.
        /// <para>
        /// <b>Optional, where every field beside it is required, and the
        /// difference is deliberate.</b> This endpoint replaces the whole
        /// settings object, so a new required field would refuse every request
        /// written before it existed — including the one this Server's own suite
        /// sends. Making it optional-and-absent mean <i>leave it alone</i> is the
        /// only reading that is safe in both directions: an older caller saving
        /// an unrelated setting must not silently switch external judging off,
        /// which is exactly what a plain <c>bool</c> defaulting to false would
        /// have done.
        /// </para>
        /// </summary>
        public bool? ExternalJudgingEnabled { get; init; }
    }

    public record InstanceLogoInputDto
    {
        /// <summary>Absent removes the mark.</summary>
        public string? FileId { get; init; }
        /// <summary>Absent sets the default mark.</summary>
        public string? Language { get; init; }
    }

    /// <summary>Publishing adds a revision; it replaces none.</summary>
    public record PublishDocumentInputDto
    {
        public required IReadOnlyList<NewStatementDto> Statements { get; init; }
        public string? Title { get; init; }
        public string? ValidFrom { get; init; }
    }
}

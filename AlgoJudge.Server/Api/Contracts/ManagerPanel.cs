namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// The rest of the manager surface, mirroring `ManagerApi.ts`.
    /// </summary>

    // ── Roles and grants ─────────────────────────────────────────────────────

    public record RoleDto
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required IReadOnlyList<string> Permissions { get; init; }
        /// <summary>One of the three shipped. Deleting one is refused.</summary>
        public required bool IsBuiltIn { get; init; }

        /// <summary>
        /// Which shipped role this is — <c>participant</c>, <c>manager</c> or
        /// <c>admin</c> — or null for one an installation invented. Every
        /// enrollment path resolves a shipped role by this, so a screen can say
        /// which role a rename was applied to.
        /// </summary>
        public string? BuiltInKey { get; init; }

        /// <summary>
        /// Null for a role the installation shares; otherwise the activity that
        /// owns it, where only that activity's grants may link to it.
        /// </summary>
        public string? ActivityId { get; init; }
        public string? ActivityName { get; init; }

        /// <summary>
        /// How many grants link this role — how many people an edit reaches.
        /// <para>
        /// Sent because a role fails <b>open</b>: the whole risk of editing one
        /// is not knowing how far the edit goes, and a number is the cheapest
        /// answer to that.
        /// </para>
        /// <para>
        /// It counts the contributions a provider wrote as well. It did not
        /// until 2026-09-19, when those were copies rather than links, so a role
        /// eight hundred people held through a directory group reported that an
        /// edit reached nobody.
        /// </para>
        /// </summary>
        public required int Grants { get; init; }

        /// <summary>
        /// The providers whose rules name this role, by slug. Empty for a role
        /// nothing maps onto. An edit reaches these people at their next sign-in
        /// rather than at once, which is a different sentence for the screen to
        /// say.
        /// </summary>
        public required IReadOnlyList<string> MappedBy { get; init; }
    }

    public record RoleInputDto
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required IReadOnlyList<string> Permissions { get; init; }

        /// <summary>
        /// The activity that owns it, or null for a role the installation
        /// shares. Only settable at creation: moving a role between scopes would
        /// silently change who every linked grant answers to.
        /// </summary>
        public string? ActivityId { get; init; }
    }

    /// <summary>Several people competing as one, in one activity.</summary>
    public record ActivityGroupDto
    {
        public required string Id { get; init; }
        public required string ActivityId { get; init; }
        public required string Name { get; init; }
        /// <summary>A short line beside the name, shown in the ranking.</summary>
        public string? Description { get; init; }
        /// <summary>
        /// Kept out of results and out of the ranking. <b>It still submits and
        /// still spends its allowance</b> — what it does not do is appear.
        /// </summary>
        public required bool IsSystem { get; init; }
        public required int MemberCount { get; init; }
        /// <summary>
        /// How many submissions were sent under it. <b>A group with any cannot
        /// be deleted</b>: the stamp on a submission is the record of what
        /// competed, and removing the row would make each of them say it was
        /// sent by nobody.
        /// </summary>
        public required int SubmissionCount { get; init; }
        public required string CreatedAt { get; init; }
    }

    public record ActivityGroupInputDto
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public bool IsSystem { get; init; }
    }

    /// <summary>
    /// Which group somebody competes as, or none.
    /// <para>
    /// <b>A move is allowed at any time and moves nothing already sent.</b> Each
    /// submission stamped its group when it was made, so this changes what
    /// happens next and leaves every ranking that has already been read alone.
    /// </para>
    /// </summary>
    public record GrantGroupInputDto
    {
        /// <summary>Null takes them out of every group.</summary>
        public string? GroupId { get; init; }
    }

    /// <summary>One role a grant links, and where it came from.</summary>
    public record GrantRoleDto
    {
        public required string RoleId { get; init; }
        public required string Name { get; init; }
        public required IReadOnlyList<string> Permissions { get; init; }

        /// <summary>Null for a role belonging to the installation.</summary>
        public string? ActivityId { get; init; }

        /// <summary>
        /// Who put it here: null for a person, otherwise the provider or the
        /// platform that asserted it. A manager deciding whether to remove a
        /// role needs to know which of those it is.
        /// </summary>
        public string? SourceProviderId { get; init; }
        public string? SourceProviderName { get; init; }

        /// <summary>When somebody took it away, on a dismissed role only.</summary>
        public string? DismissedAt { get; init; }
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

        /// <summary>
        /// This grant's <b>own</b> entries — what it adds on top of its roles, or
        /// the whole set where it links none.
        /// <para>
        /// What the person actually holds is this unioned with every role in
        /// <see cref="Roles"/>. Separate fields rather than one union, because a
        /// screen has to edit these and may not edit a role, and a single list
        /// could not say which was which.
        /// </para>
        /// </summary>
        public required IReadOnlyList<string> Permissions { get; init; }

        /// <summary>
        /// The roles this grant links, with what each one carries, so a row
        /// reads without a second lookup. Empty where the grant holds its own
        /// set alone.
        /// </summary>
        public required IReadOnlyList<GrantRoleDto> Roles { get; init; }

        /// <summary>
        /// Roles somebody took away from this grant. They confer nothing; they
        /// are sent so a screen can say why a launch does not put them back, and
        /// so re-adding one is a visible act rather than a guess.
        /// </summary>
        public required IReadOnlyList<GrantRoleDto> DismissedRoles { get; init; }

        /// <summary>
        /// A membership that runs the activity rather than takes part in it.
        /// <b>Derived</b>: true when the permissions make it staff, or when
        /// somebody said so. The Server decides.
        /// </summary>
        public required bool IsSystem { get; init; }

        /// <summary>
        /// Whether <see cref="IsSystem"/> is somebody's decision rather than
        /// something the permissions imply. The flag the screen offers, and the
        /// only half of <c>isSystem</c> a person may set.
        /// </summary>
        public required bool StaffByHand { get; init; }
        /// <summary>`invited` | `active`.</summary>
        public required string State { get; init; }
        /// <summary>The group this person competes as, or null for themselves.</summary>
        public string? GroupId { get; init; }
        public string? GroupName { get; init; }
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
        /// every sign-in — and therefore **not editable here**.
        /// <para>
        /// True for a system-scope grant a provider wrote, and for nothing else.
        /// An activity grant is a person's membership whoever created it, so a
        /// launch no longer makes one unmanageable: a manager may edit and
        /// revoke it, and what a platform asserted is marked on the roles rather
        /// than on the row.
        /// </para>
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

        /// <summary>
        /// The grant's own entries. With roles linked these are additions to
        /// them; without any they are the whole set.
        /// </summary>
        public required IReadOnlyList<string> Permissions { get; init; }

        /// <summary>
        /// The roles this grant is to link, as a whole set: a role missing from
        /// the list is taken away, and taking one away is what stops a launch
        /// putting it back.
        /// <para>
        /// <b>Absent leaves the roles as they are; an empty list takes them
        /// all away.</b> The two are different instructions, and a write that
        /// says nothing about roles must not silently strip them — an
        /// enrollment, a group move and a staff flag are all edits to a grant
        /// that have no opinion about its roles.
        /// </para>
        /// <para>
        /// Installation roles, or roles belonging to this grant's activity. The
        /// excess rule is applied to what is <b>added</b>, so re-sending what is
        /// already there is never refused.
        /// </para>
        /// </summary>
        public IReadOnlyList<string>? RoleIds { get; init; }

        /// <summary>
        /// Mark this membership as staff whatever its permissions imply — a jury
        /// member holding a participant's keys. Clearing it does not make
        /// somebody a competitor whose permissions say otherwise: the Server
        /// derives the rest.
        /// </summary>
        public bool? StaffByHand { get; init; }
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
        /// <summary>Enroll them all into one activity as they are created.</summary>
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
        /// <summary>
        /// The IANA zone this row's activity keeps its clock in, e.g.
        /// `Europe/Warsaw`.
        /// <para>
        /// **Here because the panel lists rows from several activities at once.**
        /// The Client shows every instant in the reader's own zone and names the
        /// activity's beside it; without this field these rows could name
        /// nothing, and a manager arguing about whether a submission beat a
        /// deadline would have had no way to see the clock the deadline was set
        /// on.
        /// </para>
        /// </summary>
        public required string TimeZone { get; init; }
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
        /// <summary>
        /// The IANA zone this row's activity keeps its clock in, e.g.
        /// `Europe/Warsaw`.
        /// <para>
        /// **Here because the panel lists rows from several activities at once.**
        /// The Client shows every instant in the reader's own zone and names the
        /// activity's beside it; without this field these rows could name
        /// nothing, and a manager arguing about whether a submission beat a
        /// deadline would have had no way to see the clock the deadline was set
        /// on.
        /// </para>
        /// </summary>
        public required string TimeZone { get; init; }
        public required string SeriesId { get; init; }
        public required string SeriesName { get; init; }
        /// <summary>The assignment, not the library entry.</summary>
        public required string SeriesProblemId { get; init; }
        public required string ProblemSlug { get; init; }
        public required string ProblemName { get; init; }
        public required string UserId { get; init; }
        public required string UserName { get; init; }
        public required string SubmittedAt { get; init; }
        /// <summary>What the participant declared beside the bytes. Opaque.</summary>
        public object? Props { get; init; }
        public required string State { get; init; }
        public string? Verdict { get; init; }
        public double? Score { get; init; }
        public double? MaxScore { get; init; }
        /// <summary>How many evaluation jobs it has had. A rejudge adds one.</summary>
        public required int Attempts { get; init; }

        /// <summary>
        /// A manager ruled that this counts toward no standing. On the list as
        /// well as the detail, unlike the origin fields: no disclosure question,
        /// and a manager scanning two hundred rows should not open each.
        /// </summary>
        public required bool Excluded { get; init; }
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

        /// <summary>
        /// Where this arrived from — for a judge asking whether a solution came
        /// from outside the examination room.
        /// <para>
        /// <b>On the detail and deliberately not on the list.</b> A column of
        /// addresses across two hundred rows is exposure for a question nobody
        /// asked of most of them; a judge who wants one opens one.
        /// </para>
        /// <para>
        /// Null once past the retention window, which is the honest answer: it
        /// is not held anymore.
        /// </para>
        /// </summary>
        public string? IpAddress { get; init; }

        /// <summary>The browser session, or null if it had none yet.</summary>
        public string? SessionId { get; init; }

        /// <summary>
        /// The name the browser gave itself. <b>Not evidence</b> — a page writes
        /// it, and a room imaged from one disk reports one for every machine.
        /// It answers <i>the same browser, two accounts</i>.
        /// </summary>
        public string? DeviceId { get; init; }

        /// <summary>When the ruling was made. Null unless `excluded`.</summary>
        public string? ExcludedAt { get; init; }

        /// <summary>Who made it, by display name.</summary>
        public string? ExcludedBy { get; init; }

        /// <summary>
        /// Why, in the manager's own words. Not on the participant's screen —
        /// theirs carries the marker alone — but writing about them, so their
        /// data export carries it.
        /// </summary>
        public string? ExclusionReason { get; init; }

        /// <summary>Newest first.</summary>
        public required IReadOnlyList<ManagedAttemptDto> AttemptList { get; init; }
        public required IReadOnlyList<SubmissionFileDto> Files { get; init; }
    }

    /// <summary>
    /// A manager's ruling that a submission counts toward no standing.
    /// <c>excluded: false</c> lifts it and clears the reason with it — a
    /// sentence about a state that no longer holds is worse than none.
    /// </summary>
    public record ExcludedInputDto
    {
        public required bool Excluded { get; init; }
        public string? Reason { get; init; }
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

    /// <summary>What a copy of a round needs that cannot be derived from it.</summary>
    public record DuplicateSeriesDto
    {
        /// <summary>
        /// Where the copy goes. <b>Absent copies in place</b>, into the round's
        /// own activity, which is how a second session of the same round is made.
        /// </summary>
        public Guid? TargetActivityId { get; init; }

        /// <summary>The copy's slug, unique within the target activity.</summary>
        public string? Slug { get; init; }

        /// <summary>
        /// When the copy begins. Its end and its ranking dates move by the same
        /// amount, on the wall clock of the activity it is copied into.
        /// </summary>
        public required DateTime StartsAt { get; init; }
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

        /// <summary>
        /// When this value stops working, if it ever does.
        /// <para>
        /// <b>Absent today, and present here on purpose.</b> Every key this
        /// installation holds is one an administrator typed, shared by everyone
        /// who may read it, and good until somebody changes it — so nothing sets
        /// this yet.
        /// </para>
        /// <para>
        /// It is in the answer already because the direction is known: keys
        /// minted per person, for one call, with a life. That changes what
        /// stands behind a name, not the question asked of it — but a caller
        /// written without this field would cache a value past its death and
        /// have no way to find out. **A reader must not use the value after
        /// this instant, and must ask again rather than assume.**
        /// </para>
        /// </summary>
        public string? ExpiresAt { get; init; }
    }

    public record ExternalContentDto
    {
        /// <summary>The instance switch. Read-only here; it is set with the rest of the settings.</summary>
        public required bool Enabled { get; init; }
        public required IReadOnlyList<string> Hosts { get; init; }
    }

    /// <summary>
    /// One redirect column, as it is rather than as it is served.
    /// <para>
    /// <c>getInstanceInfo</c> answers <i>what should a signed-out screen do
    /// now</i>, so it names a slug only while that provider is enabled. The
    /// panel's form asks a different question — <i>what is in the column</i> —
    /// and answering it from the first one is how the form came to show
    /// <c>None</c> for a setting that was there.
    /// </para>
    /// </summary>
    public record InstanceRedirectDto
    {
        /// <summary>What the column holds. Absent means no redirect is set.</summary>
        public string? Slug { get; init; }

        /// <summary>
        /// The name of the provider registered under that slug, enabled or not.
        /// Absent when none is: pre-configuration may name a slug before any
        /// provider exists, and this answer has to be able to say so.
        /// </summary>
        public string? DisplayName { get; init; }

        /// <summary>
        /// <c>none</c>, <c>inForce</c>, <c>disabled</c> or <c>unregistered</c>.
        /// <para>
        /// Exhaustive rather than inferred: the panel has a different sentence
        /// for each, and reading <c>unregistered</c> out of an absent
        /// <see cref="DisplayName"/> is the cleverness that rots. <c>inForce</c>
        /// is the one state in which the public answer names this slug too.
        /// </para>
        /// </summary>
        public required string State { get; init; }
    }

    public record InstanceRedirectsDto
    {
        public required InstanceRedirectDto SignIn { get; init; }
        public required InstanceRedirectDto Register { get; init; }
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

        /// <summary>
        /// Whether a series may hide or lock anything at all. Absent leaves it
        /// alone, for the reason above.
        /// <para>
        /// <b>The switch for the morning nobody yet knows which round is at
        /// fault.</b> A wrong address list locks out a whole cohort at once, and
        /// whoever takes that call has to be able to clear it without first
        /// finding the round that did it.
        /// </para>
        /// </summary>
        public bool? SeriesRestrictionsEnabled { get; init; }

        /// <summary>
        /// Whether the home page opens with the product's own introduction.
        /// Absent leaves it alone, for the reason above.
        /// </summary>
        public bool? ShowHero { get; init; }

        /// <summary>
        /// The slug of the provider the sign-in screen sends the browser straight
        /// to. Blank clears it.
        /// <para>
        /// <b>Three states, and the C# says which is which.</b> Absent — the
        /// property stays <c>null</c> — means <i>leave it alone</i>, for the
        /// reason <see cref="ExternalJudgingEnabled"/> gives above. An
        /// <b>empty string</b> clears it, and a slug sets it.
        /// </para>
        /// <para>
        /// <b>That is deliberately not how <see cref="Name"/> reads</b>, where
        /// absent and blank are one thing. A request written before this field
        /// existed omits it, and reading that omission as "clear" would take a
        /// working sign-in path off an installation while somebody was saving an
        /// unrelated switch — and three such requests exist today, including the
        /// one the manager panel sends.
        /// </para>
        /// </summary>
        public string? SignInRedirectProvider { get; init; }

        /// <summary>The same, for the registration screen. Absent leaves it alone.</summary>
        public string? RegisterRedirectProvider { get; init; }
    }

    public record InstanceLogoInputDto
    {
        /// <summary>Absent removes the mark.</summary>
        public string? FileId { get; init; }
        /// <summary>Absent sets the default mark.</summary>
        public string? Language { get; init; }
    }

    /// <summary>
    /// Setting the theme, by either of its two doors.
    /// <para>
    /// <b>One mechanism, not two.</b> The panel's form sends
    /// <see cref="Theme"/> and this Server writes the canonical YAML; an operator
    /// with a file of their own sends <see cref="FileId"/>. Both end at the same
    /// published file, so there is one thing in force and one thing to download.
    /// Exactly one of the two, because a request stating both is a request whose
    /// author disagrees with themselves.
    /// </para>
    /// </summary>
    public record InstanceThemeInputDto
    {
        public string? FileId { get; init; }
        public ThemeColorsInputDto? Theme { get; init; }
    }

    /// <summary>
    /// The form's own shape. Mirrors <see cref="ThemeColorsDto"/> in both
    /// schemes; an empty string is <b>absent</b>, because the form sends every
    /// field and an untouched one means the default.
    /// </summary>
    public record ThemeColorsInputDto
    {
        public ThemeColorsDto? Light { get; init; }
        public ThemeColorsDto? Dark { get; init; }
        public string? FontFamily { get; init; }
        public string? FontFamilyHeadings { get; init; }
        /// <summary>
        /// The faces, by the names they were uploaded under. Absent leaves the
        /// faces this instance already declares — the same reading every other
        /// absent field on this endpoint gets.
        /// </summary>
        public IReadOnlyList<ThemeFontInputDto>? Fonts { get; init; }
    }

    public record ThemeFontInputDto
    {
        public required string Family { get; init; }
        public required string File { get; init; }
        public int? Weight { get; init; }
        public string? Style { get; init; }
    }

    /// <summary>Publishing one face's file under a name the theme can call it by.</summary>
    public record InstanceFontInputDto
    {
        public required string FileId { get; init; }
        /// <summary>A file name ending <c>.woff2</c> — never a path, never a URL.</summary>
        public required string Name { get; init; }
    }

    /// <summary>Publishing adds a revision; it replaces none.</summary>
    public record PublishDocumentInputDto
    {
        public required IReadOnlyList<NewStatementDto> Statements { get; init; }
        public string? Title { get; init; }
        public string? ValidFrom { get; init; }
    }

    /* ── carrying one account's work onto another ─────────────────────────── */

    public record MergeInputDto
    {
        /// <summary>The account that keeps everything.</summary>
        public required string TargetUserId { get; init; }
    }

    /// <summary>
    /// What a merge would move, and what would stop it.
    /// <para>
    /// <b>The preview is the guard.</b> A manager decides that two accounts are
    /// one person, and nothing but their own care stands behind that — so the
    /// screen states whose work, how much of it, and onto whom, before anything
    /// moves.
    /// </para>
    /// </summary>
    public record MergePreviewDto
    {
        public required string SourceUserId { get; init; }
        public required string SourceLogin { get; init; }
        public required string SourceName { get; init; }
        public required bool SourceIsTemporary { get; init; }
        public required string TargetUserId { get; init; }
        public required string TargetLogin { get; init; }
        public required string TargetName { get; init; }
        public required int Submissions { get; init; }
        public required int Questions { get; init; }
        public required int Activities { get; init; }
        public required IReadOnlyList<string> ActivityNames { get; init; }

        /// <summary>
        /// Why it would be refused. Empty means it would go through.
        /// <para>
        /// There is one reason, and it is not about the work: an account holding
        /// permissions over the whole installation is refused, because grants
        /// move with a merge and a system grant is privilege rather than work.
        /// </para>
        /// </summary>
        public required IReadOnlyList<string> Blockers { get; init; }
    }

    public record AccountMergeDto
    {
        public required string Id { get; init; }
        public required string SourceUserId { get; init; }
        public required string TargetUserId { get; init; }
        public required string MergedAt { get; init; }
        public required string MergedByUserId { get; init; }

        /// <summary>
        /// When the emptied account is anonymized, which is also when an undo
        /// stops being offered. Until then it is only blocked, so an undo gives
        /// it back whole.
        /// </summary>
        public required string AnonymizeAfter { get; init; }

        public string? SourceAnonymizedAt { get; init; }
        public string? UndoneAt { get; init; }

        /// <summary>Whether an undo is still offered.</summary>
        public required bool CanUndo { get; init; }
    }

    // ── Printouts ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One row of the queue an operator works from. Metadata only: the source
    /// travels on <see cref="PrintoutSheetDto"/>, which is one request per sheet
    /// and carries a header saying not to store it.
    /// </summary>
    public record ManagedPrintoutDto
    {
        public required string Id { get; init; }
        public required string ActivityId { get; init; }
        public required string ActivityName { get; init; }
        /// <summary>
        /// Beside the name, as <c>ManagedSubmissionDto</c> and
        /// <c>ManagedQuestionDto</c> both carry it. This queue mixes activities
        /// more than either of them does — one person works several rooms — and a
        /// slug is what those rooms are called out loud.
        /// </summary>
        public required string ActivitySlug { get; init; }
        public required string RequestedByName { get; init; }
        /// <summary>The group as it was when the request was made, if any.</summary>
        public string? GroupName { get; init; }
        public string? Title { get; init; }
        public required string FileName { get; init; }
        public required long SizeBytes { get; init; }
        /// <summary>Printed in the sheet's footer, so paper matches a row.</summary>
        public required string Sha256 { get; init; }
        /// <summary>`requested` | `printing` | `printed` | `discarded`.</summary>
        public required string State { get; init; }
        public required string RequestedAt { get; init; }
        /// <summary>Who has it open at a printer, while somebody does.</summary>
        public string? ClaimedByName { get; init; }
        public string? ClaimedAt { get; init; }
        /// <summary>
        /// Whether the reader is the one holding it. What tells "you are
        /// printing this" from "somebody else is", which is the whole point of
        /// the state.
        /// </summary>
        public required bool ClaimedByMe { get; init; }
        public string? ResolvedAt { get; init; }
        public string? ResolvedByName { get; init; }
        /// <summary>Set once the source has gone. The row outlives the bytes.</summary>
        public string? SourceDisposedAt { get; init; }
    }

    /// <summary>
    /// Everything one sheet of paper carries, source included.
    /// <para>
    /// <see cref="Source"/> is null once the request is resolved, and the
    /// endpoint still answers 200: the printout exists and its record is the
    /// audit trail, so a 404 would be a different and false sentence.
    /// </para>
    /// </summary>
    public record PrintoutSheetDto
    {
        public required ManagedPrintoutDto Printout { get; init; }
        /// <summary>The activity's own zone, so a date on paper reads as the room did.</summary>
        public required string TimeZone { get; init; }
        /// <summary>Where the text came from, when it came from a submission.</summary>
        public string? ProblemSlug { get; init; }
        public string? ProblemName { get; init; }
        public string? Source { get; init; }
    }

    /// <summary>What the operator did with the paper.</summary>
    public record ResolvePrintoutInputDto
    {
        /// <summary>`printed` | `discarded`.</summary>
        public string? Outcome { get; init; }
    }

    /// <summary>One activity the caller may work the queue of.</summary>
    public record PrintoutActivityDto
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Slug { get; init; }
    }
}

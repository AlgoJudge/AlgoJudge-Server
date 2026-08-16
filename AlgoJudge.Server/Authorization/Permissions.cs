namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// Where a permission is meaningful. <c>Both</c> means either scope accepts it.
    /// </summary>
    public enum PermissionScope
    {
        Global,
        Activity,
        Both,
    }

    /// <summary>
    /// One entry in the catalogue: what it is called, where it applies, and
    /// whether an ordinary participant holds it.
    /// </summary>
    /// <param name="Key">The stored string, e.g. <c>problem:read:all</c>.</param>
    /// <param name="Group">How the grant editor groups it.</param>
    /// <param name="Scope">Where it may be granted.</param>
    /// <param name="Participant">
    /// Whether the participant template grants it by default. What the editor
    /// starts a new participant with — <b>not</b> what decides who competes.
    /// </param>
    /// <param name="Systemic">
    /// Whether a grant carrying it is systemic, and therefore out of the
    /// participant count and the ranking.
    /// <para>
    /// Two flags rather than one, because they are two questions and the answer
    /// differs for <c>trial:run</c>: outside the default template, yet held
    /// without ceasing to be a competitor. While the catalogue said only
    /// <see cref="Participant"/>, the Client had to infer this one by negating
    /// it — and inferred it wrongly the moment such a permission existed.
    /// </para>
    /// </param>
    public record PermissionDefinition(
        string Key,
        string Group,
        PermissionScope Scope,
        bool Participant,
        bool Systemic);

    /// <summary>
    /// The permission catalogue: the whole vocabulary, in one place, because the
    /// Server is what enforces it.
    /// <para>
    /// Mirrors <c>docs/specs/PERMISSIONS.md</c>. The Client fetches this rather
    /// than hard-coding it, so an installation that adds an entry does not need a
    /// Client release to show it.
    /// </para>
    /// <para>
    /// A permission is a string, not a column. A schema that enumerated these in
    /// columns could not express one that did not exist when the migration was
    /// written.
    /// </para>
    /// </summary>
    public static class Permissions
    {
        /// <summary>
        /// Bypasses every check, at every scope. A permission rather than a flag,
        /// so it needs no column of its own — and because a grant is a flat set,
        /// an administrator cannot be partially crippled: either they hold this
        /// or they do not.
        /// </summary>
        public const string SystemAdministrator = "system:administrator";

        public const string ActivityRead = "activity:read";
        public const string ActivityCreate = "activity:create";
        public const string ActivityUpdate = "activity:update";
        public const string ActivityArchive = "activity:archive";
        public const string ActivityDelete = "activity:delete";
        public const string ActivityEnroll = "activity:enroll";

        /// <summary>
        /// May ask for a package to be run without it being anybody's problem.
        /// <para>
        /// <b>Absent from the participant template on purpose.</b> A trial
        /// spends a Runner, so opening it is a manager's decision in one
        /// activity rather than a property of the installation — which is what
        /// granting it in an activity already expresses, with no new column
        /// anywhere.
        /// </para>
        /// </summary>
        public const string TrialRun = "trial:run";

        public const string ProblemReadOwn = "problem:read:own";
        public const string ProblemReadAll = "problem:read:all";
        public const string ProblemCreate = "problem:create";
        public const string ProblemUpdate = "problem:update";
        public const string ProblemDelete = "problem:delete";
        public const string ProblemShare = "problem:share";
        public const string ProblemArchive = "problem:archive";
        public const string ProblemAttach = "problem:attach";

        /// <summary>
        /// May create a problem whose slug carries a reserved prefix.
        /// <para>
        /// A prefix is reserved so that a person cannot hand-make a problem in a
        /// namespace an importer owns — two problems called <c>Imported-100</c>, one
        /// imported and one typed in, is a confusion nobody can undo afterwards.
        /// The reserved list is <b>configuration</b>, never a literal in this
        /// code: the Server must not learn the name of any particular archive.
        /// </para>
        /// </summary>
        public const string ProblemImportExternal = "problem:import:external";

        public const string SubmissionReadOwn = "submission:read:own";
        public const string SubmissionReadAll = "submission:read:all";
        public const string SubmissionCreate = "submission:create";
        public const string SubmissionSourceReadAll = "submission:source:read:all";
        public const string SubmissionRejudge = "submission:rejudge";
        public const string SubmissionCancel = "submission:cancel";

        public const string ResultReadOwn = "result:read:own";
        public const string ResultReadAll = "result:read:all";
        public const string ResultLogReadAll = "result:log:read:all";

        public const string QuestionReadOwn = "question:read:own";
        public const string QuestionReadAll = "question:read:all";
        public const string QuestionCreate = "question:create";
        public const string QuestionAnswer = "question:answer";
        public const string QuestionPublish = "question:publish";
        public const string AnnouncementCreate = "announcement:create";

        public const string RankingRead = "ranking:read";
        public const string RankingReadUnfrozen = "ranking:read:unfrozen";
        public const string RankingUnfreeze = "ranking:unfreeze";

        public const string UserReadAll = "user:read:all";
        public const string UserCreate = "user:create";
        public const string UserUpdate = "user:update";
        public const string UserBlock = "user:block";
        public const string UserCreateTemporary = "user:create:temporary";

        public const string GrantReadAll = "grant:read:all";
        public const string GrantUpdate = "grant:update";

        public const string TemplateRead = "template:read";
        public const string TemplateManage = "template:manage";

        public const string RunnerRead = "runner:read";
        public const string RunnerApprove = "runner:approve";
        public const string RunnerRevoke = "runner:revoke";
        public const string RunnerUpdate = "runner:update";

        public const string InstanceUpdate = "instance:update";

        /// <summary>
        /// Register identity providers, edit their claim mapping, enable and
        /// disable them.
        /// <para>
        /// <b>The most dangerous key here after <see cref="SystemAdministrator"/>.</b>
        /// A mapping rule decides what a token buys: holding this means deciding
        /// which of an external directory's groups become permissions in this
        /// installation. Two guards are what make it survivable, and both are
        /// enforced rather than documented — <see cref="SystemAdministrator"/> is
        /// unreachable through a mapping in every configuration, and nobody may
        /// map onto a permission they do not themselves hold.
        /// </para>
        /// </summary>
        public const string ProviderManage = "provider:manage";

        /// <summary>
        /// The seven an ordinary participant holds.
        /// <para>
        /// This list is load-bearing beyond the grant editor: it is what
        /// <see cref="IsStaff"/> measures against, and therefore what decides who
        /// appears in a ranking.
        /// </para>
        /// </summary>
        private static readonly string[] ParticipantKeys =
        [
            ActivityRead,
            SubmissionReadOwn,
            SubmissionCreate,
            ResultReadOwn,
            QuestionReadOwn,
            QuestionCreate,
            RankingRead,
        ];

        /// <summary>
        /// Keys a participant may hold **without becoming staff**.
        /// <para>
        /// <see cref="IsStaff"/> used to measure against
        /// <see cref="ParticipantKeys"/> alone, which conflated two different
        /// ideas: what the participant template grants by default, and what
        /// makes somebody staff. They came apart the moment a permission existed
        /// that a participant may be given without it changing who they are —
        /// granting <c>trial:run</c> would have made every participant staff,
        /// and staff do not appear in a ranking. **A whole activity's board
        /// would have emptied because somebody was allowed to time their own
        /// code.**
        /// </para>
        /// <para>
        /// Staff is about seeing or changing other people's work. Spending your
        /// own time on your own package is not that.
        /// </para>
        /// </summary>
        private static readonly HashSet<string> NotStaffConferring =
            [.. ParticipantKeys, TrialRun];

        /// <summary>
        /// Both flags come from the same two lists <see cref="IsStaff"/> uses,
        /// so the catalogue cannot drift from what is enforced. Asserted in
        /// <c>StaffTests</c> rather than left to reading.
        /// </summary>
        private static PermissionDefinition Define(string key, string group, PermissionScope scope) =>
            new(key, group, scope, ParticipantKeys.Contains(key), !NotStaffConferring.Contains(key));

        public static readonly IReadOnlyList<PermissionDefinition> Catalogue =
        [
            Define(ActivityRead, "activity", PermissionScope.Activity),
            Define(ActivityCreate, "activity", PermissionScope.Global),
            Define(ActivityUpdate, "activity", PermissionScope.Both),
            Define(ActivityArchive, "activity", PermissionScope.Both),
            Define(ActivityDelete, "activity", PermissionScope.Both),
            Define(ActivityEnroll, "activity", PermissionScope.Both),
            Define(TrialRun, "activity", PermissionScope.Both),

            Define(ProblemReadOwn, "problem", PermissionScope.Global),
            Define(ProblemReadAll, "problem", PermissionScope.Global),
            Define(ProblemCreate, "problem", PermissionScope.Global),
            Define(ProblemUpdate, "problem", PermissionScope.Global),
            Define(ProblemDelete, "problem", PermissionScope.Global),
            Define(ProblemShare, "problem", PermissionScope.Global),
            Define(ProblemArchive, "problem", PermissionScope.Global),
            Define(ProblemAttach, "problem", PermissionScope.Both),
            Define(ProblemImportExternal, "problem", PermissionScope.Global),

            Define(SubmissionReadOwn, "submission", PermissionScope.Activity),
            Define(SubmissionReadAll, "submission", PermissionScope.Both),
            Define(SubmissionCreate, "submission", PermissionScope.Activity),
            Define(SubmissionSourceReadAll, "submission", PermissionScope.Both),
            Define(SubmissionRejudge, "submission", PermissionScope.Both),
            Define(SubmissionCancel, "submission", PermissionScope.Both),

            Define(ResultReadOwn, "result", PermissionScope.Activity),
            Define(ResultReadAll, "result", PermissionScope.Both),
            Define(ResultLogReadAll, "result", PermissionScope.Both),

            Define(QuestionReadOwn, "question", PermissionScope.Activity),
            Define(QuestionReadAll, "question", PermissionScope.Activity),
            Define(QuestionCreate, "question", PermissionScope.Activity),
            Define(QuestionAnswer, "question", PermissionScope.Activity),
            Define(QuestionPublish, "question", PermissionScope.Activity),
            Define(AnnouncementCreate, "question", PermissionScope.Activity),

            Define(RankingRead, "ranking", PermissionScope.Activity),
            Define(RankingReadUnfrozen, "ranking", PermissionScope.Both),
            Define(RankingUnfreeze, "ranking", PermissionScope.Both),

            Define(UserReadAll, "user", PermissionScope.Global),
            Define(UserCreate, "user", PermissionScope.Global),
            Define(UserUpdate, "user", PermissionScope.Global),
            Define(UserBlock, "user", PermissionScope.Global),
            Define(UserCreateTemporary, "user", PermissionScope.Both),

            Define(GrantReadAll, "grant", PermissionScope.Both),
            Define(GrantUpdate, "grant", PermissionScope.Both),

            Define(TemplateRead, "template", PermissionScope.Global),
            Define(TemplateManage, "template", PermissionScope.Global),

            Define(RunnerRead, "runner", PermissionScope.Global),
            Define(RunnerApprove, "runner", PermissionScope.Global),
            Define(RunnerRevoke, "runner", PermissionScope.Global),
            Define(RunnerUpdate, "runner", PermissionScope.Global),

            // Configuring the installation: its name, its mark, the documents it
            // publishes. Global by construction — there is one instance — and not
            // part of the manager set, because running an activity is not running
            // the installation it lives in.
            Define(InstanceUpdate, "instance", PermissionScope.Global),

            // Its own group rather than folded into `instance`: configuring what
            // the installation is called and deciding which external directory
            // may hand out permissions in it are not the same job, and a grant
            // editor that groups them invites giving away the second while
            // meaning the first.
            Define(ProviderManage, "provider", PermissionScope.Global),

            Define(SystemAdministrator, "system", PermissionScope.Global),
        ];

        public static readonly IReadOnlySet<string> Participant = ParticipantKeys.ToHashSet();

        private static readonly HashSet<string> Known = Catalogue.Select(d => d.Key).ToHashSet();

        /// <summary>
        /// Whether a permission set makes a grant a staff grant.
        /// <para>
        /// A key the catalogue does not describe counts as staff: an unknown
        /// right is more likely to be a new one somebody has been given than an
        /// ordinary participant's, and guessing the other way would quietly put
        /// them in the ranking.
        /// </para>
        /// </summary>
        public static bool IsStaff(IEnumerable<string> permissions) =>
            permissions.Any(key => !NotStaffConferring.Contains(key));

        /// <summary>Whether every key is one the catalogue describes.</summary>
        public static IReadOnlyList<string> Unknown(IEnumerable<string> permissions) =>
            permissions.Where(key => !Known.Contains(key)).Distinct().ToList();

        /// <summary>The three shipped templates, plus what each holds.</summary>
        public static readonly IReadOnlyList<string> ManagerTemplate =
        [
            .. ParticipantKeys,
            ActivityUpdate, ActivityArchive, ActivityEnroll,
            ProblemReadOwn, ProblemCreate, ProblemUpdate,
            ProblemShare, ProblemArchive, ProblemAttach,
            SubmissionReadAll, SubmissionSourceReadAll,
            SubmissionRejudge, SubmissionCancel,
            ResultReadAll, ResultLogReadAll,
            QuestionReadAll, QuestionAnswer, QuestionPublish,
            AnnouncementCreate,
            RankingReadUnfrozen, RankingUnfreeze,
            UserCreateTemporary,
            GrantReadAll, GrantUpdate,
        ];

        public static readonly IReadOnlyList<string> ParticipantTemplate = [.. ParticipantKeys];

        /// <summary>
        /// One entry, because it bypasses the rest. An administrator with a list
        /// of individual permissions is an administrator who can be trimmed.
        /// </summary>
        public static readonly IReadOnlyList<string> AdminTemplate = [SystemAdministrator];
    }
}

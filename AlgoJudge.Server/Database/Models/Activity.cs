using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    public class Activity
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// Human-readable alias used in URLs, for example <c>AMMPZ-2019</c>.
        /// Unique per installation, case-insensitively, and immutable once set.
        /// It is not an identifier: nothing references an activity by slug.
        /// </summary>
        public required string Slug { get; set; }

        public required string Name { get; set; }

        /// <summary>Type discriminator, <c>name@version</c>. Never interpreted here.</summary>
        public required string Type { get; set; }

        /// <summary>
        /// Which ranking the Client renders, for example <c>icpc</c> or
        /// <c>points</c>. Deliberately separate from <see cref="Type"/>, and
        /// deliberately never branched on in this project — the moment the
        /// Server reads it, adding a ranking format becomes a Server release.
        /// </summary>
        public required string RankingType { get; set; }

        /// <summary>
        /// IANA zone the activity's clock is displayed in, e.g. <c>Europe/Warsaw</c>.
        /// <para>
        /// A display and entry zone only. Every instant in the schema is stored
        /// UTC; this is what a manager's "18:00" means when they type it and what
        /// a participant's countdown is drawn against.
        /// </para>
        /// </summary>
        public required string TimeZone { get; set; }

        /// <summary>
        /// Optional explicit bounds. When absent the activity spans its series:
        /// the earliest start and the latest end.
        /// </summary>
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public bool HasQuestions { get; set; } = true;

        /// <summary>
        /// Who sees scores, which also decides whether the ranking exists at all.
        /// There is no second switch beside it: a board turned on where nobody
        /// may see a score shows nothing, and the two settings would disagree.
        /// </summary>
        public ScoreVisibility ScoreVisibility { get; set; } = ScoreVisibility.Everyone;

        public JoinPolicy JoinPolicy { get; set; } = JoinPolicy.Closed;

        /// <summary>
        /// The join password, under <see cref="JoinPolicy.Password"/>.
        /// <para>
        /// A join code, not a credential. It authenticates nobody and belongs to
        /// the activity rather than to a person, so it is neither an end-user
        /// password nor an exception to the rule that those do not live here. A
        /// manager reads it back on purpose: its whole use is to go in a link.
        /// </para>
        /// </summary>
        public string? JoinPassword { get; set; }

        /// <summary>
        /// Hidden from the activity list of anybody not enrolled — reachable by
        /// its address and nothing else. Independent of the policy, so an
        /// activity anybody may join can still be link-only.
        /// </summary>
        public bool Unlisted { get; set; }

        /// <summary>
        /// Take the problems of a finished series away rather than leaving them
        /// readable. Off by default: a round that is over is over, not secret.
        /// </summary>
        public bool HideEndedSeriesProblems { get; set; }

        /// <summary>
        /// The languages a solution may be written in here.
        /// <para>
        /// The activity's answer and the only one — nothing declares a problem's
        /// own languages yet. Narrowing it leaves what has already been sent
        /// alone: a result belongs to what it was judged against.
        /// </para>
        /// </summary>
        public List<string> Languages { get; set; } = [];

        /// <summary>
        /// Free display metadata, e.g. <c>Prowadzący: Jan Kowalski</c>. Never
        /// queried, never filtered on, so it is stored rather than modelled.
        /// </summary>
        public string? Props { get; set; }

        /// <summary>
        /// Ceiling the Server itself enforces on a participant upload. Per-problem
        /// limits live in <see cref="SeriesProblem.Config"/> and are opaque here;
        /// this one cannot be, because the Server is what rejects the request.
        /// </summary>
        public long MaxUploadBytes { get; set; } = 8 * 1024 * 1024;

        /// <summary>How many files one submission may carry.</summary>
        public int MaxAttachments { get; set; } = 1;

        /// <summary>Submissions one participant may make per problem. Null means unlimited.</summary>
        public int? MaxSubmissionsPerProblem { get; set; }

        /// <summary>
        /// Archived: still readable, accepting nothing new — no submissions, no
        /// questions, no edits.
        /// <para>
        /// This is the ordinary way an activity ends. Deleting one destroys
        /// submissions participants may still want to look back at, which is why
        /// it is a separate permission and not in the manager template.
        /// </para>
        /// </summary>
        public DateTime? ArchivedAt { get; set; }

        /// <summary>
        /// When somebody decided this exists for the people taking part. Null
        /// means never: it is being prepared.
        ///
        /// <para>
        /// <b>The whole activity, not each round.</b> An unpublished activity
        /// answers one question — "is there anything here yet" — and a copy of
        /// last year's course is the case it exists for: it has rounds, dates and
        /// problems, and none of it is meant for anybody until somebody has been
        /// through it. Hiding rounds one by one would allow a half-published copy
        /// that nobody chose and that reads, from outside, exactly like a
        /// deliberate schedule.
        /// </para>
        ///
        /// <para>
        /// <b>Nothing opens while it is null.</b> `SeriesScheduler` skips the
        /// whole activity, so a round whose window has passed does not spring
        /// open the moment somebody copies it. Whoever may edit the activity
        /// still reaches it — otherwise there would be no way to prepare it.
        /// </para>
        ///
        /// <para>
        /// A timestamp rather than a flag, like <see cref="ArchivedAt"/> beside
        /// it: "since when was this visible" is a question that gets asked, and a
        /// boolean cannot answer it.
        /// </para>
        /// </summary>
        public DateTime? PublishedAt { get; set; }

        public ICollection<Series> Series { get; set; } = new List<Series>();
        public ICollection<Question> Questions { get; set; } = new List<Question>();

        /// <summary>
        /// Who reads each named attachment a submission carries.
        /// <para>
        /// A table rather than a column, so the database holds one row per name
        /// and cannot store two answers for <c>log</c>. Replaces a single
        /// <c>LogVisibility</c> enum, which was this table with one row in it and
        /// no way to add another.
        /// </para>
        /// </summary>
        public ICollection<AttachmentRule> AttachmentRules { get; set; } = new List<AttachmentRule>();

        /// <summary>
        /// Who is in this activity and what they may do. A grant is the
        /// membership, so this is the participant list as well as the permission
        /// list — there is no second table that could disagree with it.
        /// </summary>
        public ICollection<Grant> Grants { get; set; } = new List<Grant>();
    }

    /// <summary>
    /// What an activity says about one attachment name.
    /// <para>
    /// Keyed on the name <b>within the submission</b> — <c>source</c>,
    /// <c>log</c>, <c>details</c> — never on the uploaded file name, which is
    /// <c>main.cpp</c> and differs per person.
    /// </para>
    /// <para>
    /// <b>A name with no row is <see cref="AttachmentVisibility.ManagersOnly"/>.</b>
    /// A Runner that starts attaching something new must not publish it by
    /// arriving: the cost of being wrong is asymmetric — an over-cautious
    /// default is one click, an under-cautious one leaks the tests during a
    /// contest.
    /// </para>
    /// </summary>
    public class AttachmentRule
    {
        public Guid ActivityId { get; set; }
        public Activity? Activity { get; set; }

        /// <summary>The name within the submission. Unique per activity.</summary>
        public required string Name { get; set; }

        public AttachmentVisibility Visibility { get; set; } = AttachmentVisibility.ManagersOnly;
    }
}

using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// One problem assigned to one series. This is where a problem stops being a
    /// library entry and becomes something a participant can solve, which is why
    /// the per-use configuration lives here and not on <see cref="Problem"/>.
    /// <para>
    /// The same problem may be assigned several times within one activity with
    /// different settings, so this points at exactly one series. Only the
    /// assignment's <see cref="Slug"/> is unique in an activity; the problem
    /// behind it is not.
    /// </para>
    /// </summary>
    public class SeriesProblem
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid SeriesId { get; set; }
        public Series? Series { get; set; }

        /// <summary>
        /// The activity this assignment belongs to, denormalised from
        /// <see cref="Series"/>.
        /// <para>
        /// Carried so the database can enforce that <see cref="Slug"/> is unique
        /// across the whole <b>activity</b> rather than within one series. That
        /// rule spans two tables, and a rule enforced only in a service is a rule
        /// two concurrent requests can both pass. Kept in step by the same
        /// transaction that writes <see cref="SeriesId"/>; a series never moves
        /// between activities.
        /// </para>
        /// </summary>
        public Guid ActivityId { get; set; }
        public Activity? Activity { get; set; }

        public Guid ProblemId { get; set; }
        public Problem? Problem { get; set; }

        /// <summary>
        /// Pins the content version this assignment evaluates against.
        /// <para>
        /// <b>Set when the problem is attached</b> (decided 2026-08-08), to the
        /// library's current version. Publishing a correction therefore does not
        /// change what a running round is judged against, and following it is a
        /// manager's deliberate act rather than a side effect of fixing a typo.
        /// </para>
        /// <para>
        /// Null still means "the current version at the moment a job is created",
        /// which a manager may choose, and which is only safe where the statement
        /// is not being edited underneath a running series.
        /// </para>
        /// </summary>
        public Guid? PinnedProblemVersionId { get; set; }
        public ProblemVersion? PinnedProblemVersion { get; set; }

        /// <summary>
        /// The label a participant sees and the URL segment, defaulting to a copy
        /// of the problem's own slug. Unique across the <b>whole activity</b>,
        /// not merely within the series.
        /// </summary>
        public required string Slug { get; set; }

        /// <summary>Overrides the problem's name for this assignment when set.</summary>
        public string? Name { get; set; }

        public int Order { get; set; }

        /// <summary>
        /// What the problem is worth <b>here</b> — a point value, not a
        /// multiplier, so a round's total is read off the column rather than
        /// worked out.
        /// <para>
        /// The Server rescales wherever it reports a number:
        /// <c>round(score / maxScore × MaxPoints)</c>. Null keeps the Runner's
        /// own scale. An ICPC board is unaffected — it counts solves and penalty
        /// minutes, and a point value has nowhere to land in it.
        /// </para>
        /// <para>
        /// A column rather than an entry in <see cref="Config"/> for the standing
        /// reason: the Server applies it, and it cannot apply what it does not
        /// read.
        /// </para>
        /// </summary>
        public int? MaxPoints { get; set; }

        /// <summary>
        /// <b>Anything that changes the verdict</b> — time and memory limits, the
        /// set of languages that may be submitted, whatever else the problem type
        /// enforces. Laid over the package's own <c>config.yml</c>, and since
        /// 2026-08-22 the <b>only</b> layer over it: <c>ProblemVersion.Config</c>
        /// is gone and the chain is two.
        /// <para>
        /// Stored as <c>jsonb</c> and <b>opaque to the Server</b>: written by a
        /// manager, read by the Runner. Anything the Server itself must enforce
        /// belongs in a column instead — it cannot police what it does not read.
        /// </para>
        /// <para>
        /// <b>It reaches the participant too</b>, deliberately, and that is a
        /// change: nothing in an assignment-level override is secret — limits and
        /// language ids are exactly what a problem page should show. The
        /// package's own <c>config.yml</c> stays unpublished, because it names
        /// the checker.
        /// </para>
        /// <para>
        /// Null means none. There is no <c>{}</c> here — an empty object beside
        /// null would be two ways of saying the same nothing.
        /// </para>
        /// </summary>
        public string? Config { get; set; }

        /// <summary>
        /// <b>What the Client needs to draw and validate the submit form</b> —
        /// which fields it has, which languages the select offers, what each is
        /// called. Read by the Client and by nothing else.
        /// <para>
        /// Separate from <see cref="Config"/> because they fail differently. A
        /// wrong <c>config</c> is a wrong result; a wrong <c>spec</c> is a broken
        /// form. <b>Where the two disagree about languages, <c>config</c> wins</b>
        /// — the Runner refuses whatever the assignment did not allow, whatever
        /// the form happened to offer.
        /// </para>
        /// <para>Null means none; never <c>{}</c>.</para>
        /// </summary>
        public string? Spec { get; set; }

        /// <summary>
        /// <b>Display only</b> — captions, an extra note, the languages written
        /// out for the header above the statement. If it is wrong the screen is
        /// ugly; nothing is judged differently and no form breaks.
        /// <para>
        /// Reaches everyone who may see the problem, so nothing goes in it that is
        /// not meant to be read.
        /// </para>
        /// <para>Null means none; never <c>{}</c>.</para>
        /// </summary>
        public string? Props { get; set; }

        /// <summary>
        /// Limits the <b>Server</b> enforces, narrowing the activity's. Null inherits.
        /// <para>
        /// Columns rather than entries in <see cref="Config"/> on purpose: the
        /// Server rejects an oversized or too-frequent submission before anything
        /// runs, and it cannot police a value it does not read. Time and memory
        /// are the Runner's and stay in the configuration chain, because they
        /// only become knowable while the solution is running.
        /// </para>
        /// </summary>
        public long? MaxUploadBytes { get; set; }
        public int? MaxAttachments { get; set; }
        public int? MaxSubmissions { get; set; }

        public ICollection<Submission> Submissions { get; set; } = new List<Submission>();
        public ICollection<Question> Questions { get; set; } = new List<Question>();
    }
}

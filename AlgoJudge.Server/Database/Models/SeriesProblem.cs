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
        /// Per-assignment configuration — time and memory limits, whatever a
        /// problem type needs. Stored as <c>jsonb</c> and <b>opaque to the
        /// Server</b>: it is written by a manager and read by the Client and the
        /// Runner. Anything the Server itself must enforce belongs in a column.
        /// <para>
        /// Null means none. There is no <c>{}</c> here — an empty object beside
        /// null would be two ways of saying the same nothing.
        /// </para>
        /// </summary>
        public string? Config { get; set; }

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

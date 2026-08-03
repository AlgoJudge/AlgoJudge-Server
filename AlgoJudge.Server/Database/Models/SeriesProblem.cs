using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// One problem assigned to one series. This is where a problem stops being a
    /// library entry and becomes something a participant can solve, which is why
    /// the per-use configuration lives here and not on <see cref="Problem"/>.
    /// <para>
    /// The same problem may be assigned several times within one activity with
    /// different settings, so this points at exactly one series. It used to hold
    /// a collection, which made per-assignment configuration meaningless; the
    /// many-to-many it suggested already exists correctly one level up, as
    /// <see cref="Problem"/> to <see cref="Series"/> through this table.
    /// </para>
    /// </summary>
    public class SeriesProblem
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid SeriesId { get; set; }
        public Series? Series { get; set; }

        public Guid ProblemId { get; set; }
        public Problem? Problem { get; set; }

        /// <summary>
        /// Pins the content version this assignment evaluates against. Null means
        /// the problem's current version at the moment a job is created, which is
        /// only safe where the statement is not being edited underneath a
        /// running series.
        /// </summary>
        public Guid? PinnedProblemVersionId { get; set; }
        public ProblemVersion? PinnedProblemVersion { get; set; }

        /// <summary>
        /// The label a participant sees and the URL segment, defaulting to a copy
        /// of the problem's own slug. Unique across the **whole activity**, not
        /// merely within the series.
        /// </summary>
        public required string Slug { get; set; }

        /// <summary>Overrides the problem's name for this assignment when set.</summary>
        public string? Name { get; set; }

        public int Order { get; set; }

        /// <summary>
        /// Per-assignment configuration — accepted languages, time and memory
        /// limits, upload ceiling, whatever a problem type needs. Stored as
        /// <c>jsonb</c> and <b>opaque to the Server</b>: it is written by a
        /// manager and read by the Client and the Runner. Anything the Server
        /// itself must enforce belongs on <see cref="Activity"/> instead.
        /// </summary>
        public string Config { get; set; } = "{}";

        public ICollection<Submission> Submissions { get; set; } = new List<Submission>();
        public ICollection<Question> Questions { get; set; } = new List<Question>();
    }
}

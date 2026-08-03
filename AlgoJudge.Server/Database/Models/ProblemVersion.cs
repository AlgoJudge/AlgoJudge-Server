using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// One immutable revision of a problem's content: statement, tests, limits.
    /// Rows are append-only — a correction publishes a new version rather than
    /// editing an old one, so a result stays attached to what was actually
    /// evaluated.
    /// <para>
    /// The Server stores the files and nothing else. There is no statement
    /// concept here: <c>content.json</c> is only a well-known name inside
    /// <see cref="Files"/>, understood by the Client.
    /// </para>
    /// </summary>
    public class ProblemVersion
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ProblemId { get; set; }
        public Problem? Problem { get; set; }

        /// <summary>Increments from 1 within its problem.</summary>
        public int Version { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string? CreatedByUserId { get; set; }
        public User? CreatedBy { get; set; }

        /// <summary>Free note explaining what changed, shown to managers only.</summary>
        public string? Note { get; set; }

        /// <summary>
        /// Limits and scoring for this version, as <c>jsonb</c> and **opaque to
        /// the Server**, which stores it and never reads it.
        /// <para>
        /// It is the middle of three layers: the package's own defaults, then
        /// this, then <see cref="SeriesProblem.Config"/>. Each overrides the one
        /// before, and the Client and the Runner both parse the result — the
        /// Client to show limits and draw a result, the Runner to enforce them.
        /// </para>
        /// <para>
        /// Nothing the Server has to enforce belongs here. A limit it cannot read
        /// is a limit it cannot police, so those are explicit columns instead.
        /// </para>
        /// </summary>
        public string Config { get; set; } = "{}";

        public ICollection<File> Files { get; set; } = new List<File>();
    }
}

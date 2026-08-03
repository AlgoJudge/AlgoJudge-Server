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

        public ICollection<File> Files { get; set; } = new List<File>();
    }
}

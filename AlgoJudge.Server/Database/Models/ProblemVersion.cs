using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// One immutable revision of a problem's content: statement, tests, limits.
    /// Rows are append-only — a correction publishes a new version rather than
    /// editing an old one, so a result stays attached to what was actually
    /// evaluated.
    /// <para>
    /// The Server stores references to files and nothing else. There is no
    /// statement concept here: <c>content.md</c> is only a well-known
    /// <see cref="FileReference.Name"/>, understood by the Client.
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

        // **`Config` was here, and it is gone (2026-08-22).** It was the middle
        // of three layers — package, version, assignment — and the middle one
        // earned nothing: a version that wants different limits is a version
        // with a different package, because the limits are calibrated against
        // the tests that version ships. Two layers say the same thing with one
        // fewer place for them to disagree.
        //
        // Nothing migrated the values out. Nothing had written any: the field
        // was reachable through `ProblemVersionInputDto` and no screen sent it.

        /// <summary>
        /// Everything this version is made of — the statement and its
        /// translations, the figures, the package, the example archive — as
        /// references. The bytes are shared: carrying a figure forward into the
        /// next version is a second reference, not a second upload.
        /// </summary>
        public ICollection<FileReference> Files { get; set; } = new List<FileReference>();
    }
}

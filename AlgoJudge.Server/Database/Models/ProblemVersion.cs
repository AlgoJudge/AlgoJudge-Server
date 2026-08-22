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

        /// <summary>
        /// What the problem type needs to know about <b>this version of this
        /// problem</b>, as <c>jsonb</c> and <b>opaque to the Server</b>.
        /// <para>
        /// <b>Not a configuration layer.</b> `Config` was here until 2026-08-22
        /// as the middle of three — package, version, assignment — and the middle
        /// one earned nothing: a version wanting different limits is a version
        /// with a different package, because limits are calibrated against the
        /// tests that version ships. The chain is still two.
        /// </para>
        /// <para>
        /// What came back the same day is a different thing under a different
        /// name: **identity, not settings**. `uva@1` needs the archive's problem
        /// number, which is a fact about the problem rather than about one
        /// activity's use of it — copying it onto every assignment would be one
        /// number written in as many places as the problem is attached, and
        /// wrong in whichever of them somebody mistyped.
        /// </para>
        /// <para>
        /// <b>Optional here and required by some types.</b> The Server cannot
        /// tell which: it does not read this and must not branch on a problem
        /// type. A Runner that needs it and does not find it reports an
        /// infrastructure failure naming what is missing.
        /// </para>
        /// <para>Null means none; never <c>{}</c>.</para>
        /// </summary>
        public string? Props { get; set; }

        /// <summary>
        /// Everything this version is made of — the statement and its
        /// translations, the figures, the package, the example archive — as
        /// references. The bytes are shared: carrying a figure forward into the
        /// next version is a second reference, not a second upload.
        /// </summary>
        public ICollection<FileReference> Files { get; set; } = new List<FileReference>();
    }
}

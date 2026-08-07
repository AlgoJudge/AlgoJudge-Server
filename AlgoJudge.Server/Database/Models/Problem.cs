using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A problem in the installation's library. Its content lives in
    /// <see cref="ProblemVersion"/> rows, which are append-only, so a statement
    /// that changes never rewrites what an earlier evaluation was judged against.
    /// </summary>
    public class Problem
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>Unique per installation. Copied into a new assignment's slug by default.</summary>
        public required string Slug { get; set; }

        public required string Name { get; set; }

        /// <summary>
        /// Problem type discriminator, <c>name@version</c> — for example
        /// <c>standard-io@1</c>. This versions the <b>kind</b> of problem and its
        /// renderer and handler contract, which is a different axis from
        /// <see cref="ProblemVersion.Version"/>, the version of this problem's
        /// content.
        /// </summary>
        public required string Type { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Who owns it. A problem is <b>private by default</b>: only its author
        /// sees it and only its author may attach it to an activity.
        /// </summary>
        public required string OwnerUserId { get; set; }
        public User? Owner { get; set; }

        public ProblemVisibility Visibility { get; set; } = ProblemVisibility.Private;

        /// <summary>
        /// Archived: gone from the attach picker and taking no new versions,
        /// while every assignment already using it keeps working.
        /// <para>
        /// Retiring a problem must not break an activity that ran with it, which
        /// is why this exists and why deletion is refused while any
        /// <see cref="SeriesProblem"/> still points here — a rule the database
        /// enforces on its own through <c>DeleteBehavior.Restrict</c>.
        /// </para>
        /// </summary>
        public DateTime? ArchivedAt { get; set; }

        public ICollection<ProblemVersion> Versions { get; set; } = new List<ProblemVersion>();
        public ICollection<SeriesProblem> SeriesProblems { get; set; } = new List<SeriesProblem>();

        /// <summary>
        /// Who else may see it under <see cref="ProblemVisibility.Shared"/>.
        /// <para>
        /// This is an access control list, and it is the only one in the product.
        /// The permission model settles what a manager may <b>do</b> with a
        /// problem; this settles <b>which</b> problems that applies to. Keeping
        /// the two apart is what stops the exception becoming a second
        /// authorisation system — nothing else gets a list like this.
        /// </para>
        /// <para>
        /// A table rather than a <c>jsonb</c> array because the Server authorises
        /// on it and the library listing filters by it: it is a join, and a join
        /// written as a document is a join the database cannot index.
        /// </para>
        /// </summary>
        public ICollection<ProblemShare> SharedWith { get; set; } = new List<ProblemShare>();
    }

    /// <summary>One user a shared problem is shared with.</summary>
    public class ProblemShare
    {
        public Guid ProblemId { get; set; }
        public Problem? Problem { get; set; }

        public required string UserId { get; set; }
        public User? User { get; set; }
    }
}

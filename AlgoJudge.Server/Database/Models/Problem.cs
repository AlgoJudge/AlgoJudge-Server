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
        /// Who owns it. A problem is **private by default**: only its author sees
        /// it and only its author may attach it to an activity.
        /// </summary>
        public required string OwnerUserId { get; set; }
        public User? Owner { get; set; }

        public ProblemVisibility Visibility { get; set; } = ProblemVisibility.Private;

        /// <summary>
        /// Who else may see it when <see cref="Visibility"/> is
        /// <see cref="ProblemVisibility.Shared"/>, as a <c>jsonb</c> array of user ids.
        /// <para>
        /// This is an access control list, and it is the only one in the product.
        /// The permission model settles what a manager may **do** with a problem;
        /// this settles **which** problems that applies to. Keeping the two apart
        /// is what stops the exception from becoming a second authorisation
        /// system — nothing else gets a list like this.
        /// </para>
        /// </summary>
        public string SharedWith { get; set; } = "[]";

        public ICollection<ProblemVersion> Versions { get; set; } = new List<ProblemVersion>();
        public ICollection<SeriesProblem> SeriesProblems { get; set; } = new List<SeriesProblem>();
    }
}

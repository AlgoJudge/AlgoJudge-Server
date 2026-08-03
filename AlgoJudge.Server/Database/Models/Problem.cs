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

        public ICollection<ProblemVersion> Versions { get; set; } = new List<ProblemVersion>();
        public ICollection<SeriesProblem> SeriesProblems { get; set; } = new List<SeriesProblem>();
    }
}

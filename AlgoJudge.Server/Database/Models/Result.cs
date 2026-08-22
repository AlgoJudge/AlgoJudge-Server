using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// The outcome of one completed <see cref="EvaluationJob"/> — and only that.
    /// It is not the job record: the claim, the lease and the Runner live on the
    /// job, and the Runner is reached through <see cref="EvaluationJob"/> rather
    /// than copied here.
    /// <para>
    /// Four columns are everything the Server reads of a result. Per-test rows,
    /// the compiler log and anything else the Runner produced are
    /// <b>attachments</b> — <see cref="FileReference"/> rows under the attempt,
    /// named <c>log</c> and <c>details</c> — because a per-test table is a
    /// hundred and twenty bytes a row times two thousand tests times every
    /// attempt, and a database column is the wrong place for it.
    /// </para>
    /// </summary>
    public class Result
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid EvaluationJobId { get; set; }
        public EvaluationJob? EvaluationJob { get; set; }

        /// <summary>
        /// Copied from the job. Unlike the Runner, this is duplicated on purpose:
        /// what a result was judged against has to stay pinned to it, and the job
        /// is not where somebody looks a year later.
        /// </summary>
        public Guid ProblemVersionId { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The Runner's own scale, before any rescaling by
        /// <see cref="SeriesProblem.MaxPoints"/>. Kept so the Server can order
        /// and paginate without parsing anything; its meaning is the problem
        /// type's business.
        /// </summary>
        public double? Score { get; set; }
        public double? MaxScore { get; set; }

        /// <summary>
        /// Short outcome label produced by the Runner, e.g. <c>Accepted</c>.
        /// An opaque string: the Server matches it exactly where a filter asks
        /// for it and never parses it, so a problem type may invent a verdict
        /// without a Server release.
        /// </summary>
        public string? Verdict { get; set; }

        /// <summary>
        /// Whatever else the problem type wants a <b>board</b> to have — cycles
        /// used, peak memory, a domain measure. Filled by the Runner, stored
        /// without being read, shaped by the problem type.
        /// <para>
        /// Public by construction: everyone who may see the board is sent this,
        /// so nothing goes in it that is not meant to be seen. It rides in a
        /// list, multiplied by every submission of every contestant, which is why
        /// its ceiling is a hundredth of a configuration document's — 2 kB, and
        /// over it is refused rather than truncated.
        /// </para>
        /// <para>Null means none; never <c>{}</c>.</para>
        /// </summary>
        public string? Extra { get; set; }

        /// <summary>
        /// What the problem type wants <b>this participant</b> to be shown beside
        /// their own result — the toolchain that compiled it, a per-language note,
        /// whatever the type's renderer draws.
        /// <para>
        /// The pair to <see cref="Extra"/>, and the distinction is the audience,
        /// not the content. <c>Extra</c> is on a board everyone reads, so its
        /// ceiling is a hundredth of this one and nothing private may go in it.
        /// This one travels with one submission to one reader, under the
        /// activity's own attachment rules.
        /// </para>
        /// <para>Null means none; never <c>{}</c>.</para>
        /// </summary>
        public string? Props { get; set; }

        /// <summary>Which Runner build produced this, for reproducing a disputed result.</summary>
        public string? RunnerVersion { get; set; }
    }
}

using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// The outcome of one completed <see cref="EvaluationJob"/> — and only that.
    /// It is not the job record: the claim, the lease and the Runner live on the
    /// job, and the Runner is reached through <see cref="EvaluationJob"/> rather
    /// than copied here.
    /// <para>
    /// Per-test rows, metrics and anything a ranking needs are in
    /// <see cref="Detail"/>, which the Server stores and never parses. That is
    /// what lets a new ranking format or a new metric ship without a migration.
    /// </para>
    /// </summary>
    public class Result
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid EvaluationJobId { get; set; }
        public EvaluationJob? EvaluationJob { get; set; }

        /// <summary>
        /// Copied from the job. Unlike the Runner, this is duplicated on purpose:
        /// what a result was judged against has to stay pinned to it.
        /// </summary>
        public Guid ProblemVersionId { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Kept so the Server can order and paginate without parsing
        /// <see cref="Detail"/>. Its meaning is the problem type's business.
        /// </summary>
        public double? Score { get; set; }
        public double? MaxScore { get; set; }

        /// <summary>Short outcome label produced by the Runner, e.g. <c>Accepted</c>.</summary>
        public string? Verdict { get; set; }

        /// <summary>
        /// Evaluation log. Released according to the activity's
        /// <see cref="LogVisibility"/> — it is produced while running hidden
        /// tests and can carry test fragments, checker internals and paths.
        /// </summary>
        public string? Log { get; set; }

        /// <summary>
        /// The document the Runner attaches, as <c>jsonb</c>, opaque here and
        /// consumed by the Client. Its entries carry their own scope so the
        /// Server can project what another participant may see — for a ranking,
        /// for instance — without understanding any of it.
        /// </summary>
        public string Detail { get; set; } = "{}";

        /// <summary>Which Runner build produced this, for reproducing a disputed result.</summary>
        public string? RunnerVersion { get; set; }
    }
}

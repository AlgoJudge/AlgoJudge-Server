using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// One attempt at evaluating a submission. A Runner claims a queued job
    /// atomically — <c>SELECT ... FOR UPDATE SKIP LOCKED</c> — and holds it under
    /// a lease.
    /// <para>
    /// The Runner is stateless apart from a package cache, so a Runner that dies
    /// mid-job resumes nothing. Recovery is the Server reclaiming the job once
    /// <see cref="LeaseExpiresAt"/> passes, which makes the lease a correctness
    /// mechanism rather than a safety net: it has to outlast the slowest real
    /// evaluation.
    /// </para>
    /// </summary>
    public class EvaluationJob
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid SubmissionId { get; set; }
        public Submission? Submission { get; set; }

        /// <summary>Increments from 1 within its submission. A rejudge adds an attempt.</summary>
        public int Attempt { get; set; }

        /// <summary>
        /// The content version being evaluated. This is the source of truth and is
        /// copied onto <see cref="Result"/>, so a later republication of the
        /// problem cannot change what a finished result was judged against.
        /// </summary>
        public Guid ProblemVersionId { get; set; }
        public ProblemVersion? ProblemVersion { get; set; }

        /// <summary>Null until a Runner claims the job.</summary>
        public Guid? RunnerId { get; set; }
        public Runner? Runner { get; set; }

        public EvaluationJobState State { get; set; } = EvaluationJobState.Queued;

        /// <summary>
        /// Handed to the Runner on claim and required back when it reports.
        /// This is what makes result submission idempotent: a Runner may safely
        /// resend, and a Runner whose lease has already been reclaimed is
        /// rejected instead of overwriting a newer attempt.
        /// </summary>
        public Guid? LeaseToken { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ClaimedAt { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        /// <summary>Why the job failed, when it failed for a reason that is not a verdict.</summary>
        public string? FailureReason { get; set; }

        public Result? Result { get; set; }
    }
}

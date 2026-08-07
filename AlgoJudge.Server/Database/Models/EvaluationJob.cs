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
    /// <para>
    /// Permitted transitions, validated by the Server:
    /// <c>Queued → Running</c> on claim; <c>Running → Completed</c> on a verdict;
    /// <c>Running → Failed</c> on an infrastructure failure; <c>Running →
    /// Cancelled</c> by a manager; <c>Running → Queued</c> when the lease
    /// expires. A rejudge is a <b>new job</b>, never a transition, and a finished
    /// job never leaves its state.
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

        /// <summary>
        /// How many times this job has been handed out. Incremented on every
        /// claim, including the ones that followed a lease expiry.
        /// <para>
        /// A job that keeps being reclaimed is a job that keeps killing Runners,
        /// and retrying it for ever is how one bad package stops an installation.
        /// Past the configured cap it goes to <see cref="EvaluationJobState.Failed"/>
        /// with a reason rather than back into the queue.
        /// </para>
        /// </summary>
        public int Deliveries { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ClaimedAt { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        /// <summary>
        /// Why the job failed, when it failed for a reason that is not a verdict.
        /// A package whose checksum does not match is an infrastructure failure
        /// and lands here — it is not a wrong answer, and must never be scored
        /// as one.
        /// </summary>
        public string? FailureReason { get; set; }

        /// <summary>
        /// Optimistic concurrency. The claim is already atomic through
        /// <c>SKIP LOCKED</c>; this guards everything else that touches a job —
        /// the reaper, a cancellation and a report arriving at once.
        /// </summary>
        public uint Version { get; set; }

        public Result? Result { get; set; }

        /// <summary>
        /// What the Runner attached for this attempt: its <c>log</c>, its
        /// <c>details</c>, whatever else the problem type produces. On the
        /// attempt rather than on the submission, because a rejudge makes new
        /// ones — the source is what was sent once, the log is what one run said.
        /// </summary>
        public ICollection<FileReference> Files { get; set; } = new List<FileReference>();
    }
}

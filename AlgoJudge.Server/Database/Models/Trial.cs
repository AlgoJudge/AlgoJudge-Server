using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A package somebody asked to have run, attached to no problem.
    /// <para>
    /// <b>Not a submission, and deliberately its own table.</b> A trial produces
    /// timings rather than a verdict; it belongs to nobody's standing, counts
    /// against no ceiling, and appears on no board. Carrying it on
    /// <see cref="EvaluationJob"/> with its links left empty would have meant
    /// every query over submissions, results, rankings and attempt counts
    /// remembering to exclude it — and forgetting one would not break, it would
    /// quietly show somebody's private trial as a result.
    /// </para>
    /// <para>
    /// <b>The package does not survive it.</b> A trial is a question, not
    /// content: the bytes are removed the moment it finishes, successfully or
    /// not. That is why there is no name, no description and no history here —
    /// there is nothing to come back to.
    /// </para>
    /// <para>
    /// Who may ask is <c>trial:run</c>, granted in an activity. It is absent
    /// from the participant template by default, so an installation opens trials
    /// to participants by a manager's decision in one activity rather than
    /// everywhere at once.
    /// </para>
    /// </summary>
    public class Trial
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// The activity whose rules allowed this, or **none**.
        /// <para>
        /// A trial belongs to a person; an activity is where the permission to
        /// ask for one may be granted, not what the trial is part of. Absent
        /// means it was asked for against the **library** — a manager
        /// calibrating a problem before it belongs to any activity, permitted
        /// by a global `trial:run`.
        /// </para>
        /// <para>
        /// Nullable rather than a second table, because everything else about
        /// the two is the same: the queue, the lease, the delivery count, the
        /// measurement and the package that does not survive it. What differs
        /// is only which scope was asked for permission.
        /// </para>
        /// </summary>
        public Guid? ActivityId { get; set; }
        public Activity? Activity { get; set; }

        /// <summary>Who asked. A trial is never anonymous.</summary>
        public required string UserId { get; set; }

        /// <summary>
        /// The uploaded package. <b>Nullable because it is deleted when the
        /// trial finishes</b> — the row outlives the bytes, so that somebody can
        /// still read what was measured.
        /// </summary>
        public Guid? PackageFileId { get; set; }

        /// <summary>
        /// Matched against a Runner's own types by equality, exactly as a job
        /// is. The Server does not parse it here either.
        /// </summary>
        public required string ProblemType { get; set; }

        public EvaluationJobState State { get; set; } = EvaluationJobState.Queued;

        public Guid? RunnerId { get; set; }
        public Runner? Runner { get; set; }

        /// <summary>
        /// The same lease the job queue uses, for the same reason: a Runner that
        /// dies mid-trial resumes nothing, and the deadline is what returns the
        /// work rather than losing it.
        /// </summary>
        public Guid? LeaseToken { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public int Deliveries { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ClaimedAt { get; set; }
        public DateTime? FinishedAt { get; set; }

        /// <summary>Why it failed, where it did. Never a verdict.</summary>
        public string? FailureReason { get; set; }

        /// <summary>
        /// What was measured, stored opaquely and never read here.
        /// <para>
        /// A time per test per solution, and a peak memory where the Runner
        /// could measure one honestly. What it means is the problem type's
        /// business — the same rule that lets a type be added without a Server
        /// change applies to what a trial of that type produces.
        /// </para>
        /// </summary>
        public string? Measurement { get; set; }

        /// <summary>Optimistic concurrency, as elsewhere: PostgreSQL's `xmin`.</summary>
        public uint Version { get; set; }
    }
}

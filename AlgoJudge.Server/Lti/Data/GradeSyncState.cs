using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// What one person's grade in one column <b>should</b> be, and what the
    /// platform was last told.
    /// <para>
    /// <b>The grade sync is a state, not an event</b> (§6.4). Posting on the
    /// result event alone stops being sufficient the moment posting can be
    /// deferred, and at least six things detach the gradebook from what AlgoJudge
    /// holds: a deferred score whose freeze was released, the platform being down
    /// when the event fired, a rejudge, a change to <c>MaxPoints</c> that
    /// rescales everything already posted, an account linked after it had already
    /// submitted, and a roster member with no resolvable account.
    /// </para>
    /// <para>
    /// Each of those becomes the same act — mark the row stale, let the worker
    /// run — instead of six code paths that each have to remember to post. It is
    /// the shape the evaluation lease already uses: the truth is a row, and
    /// recovery is a sweep over rows rather than a retry somebody remembered to
    /// write.
    /// </para>
    /// </summary>
    public class GradeSyncState
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ResourceLinkId { get; set; }

        public Guid LineItemId { get; set; }
        public LineItem? LineItem { get; set; }

        /// <summary>
        /// Whose grade. A string because that is what ASP.NET Identity's key is,
        /// and it stays resolvable after an account has been anonymised.
        /// </summary>
        public required string UserId { get; set; }

        /// <summary>
        /// The result the desired score was read from. Informational, and the
        /// answer to "where did this number come from" on a screen.
        /// </summary>
        public Guid? SourceResultId { get; set; }

        /// <summary>What the gradebook should say.</summary>
        public double DesiredScore { get; set; }

        /// <summary>What it was last told, if anything.</summary>
        public double? PostedScore { get; set; }

        public DateTime? PostedAt { get; set; }

        /// <summary>
        /// The timestamp carried on the last score posted.
        /// <para>
        /// <b>This is the trap in AGS, and it is silent</b> (§6.4). A score
        /// carries a timestamp and a platform rejects one older than what it
        /// holds — by returning success and changing nothing. A retry that reuses
        /// the original result's time is therefore dropped, not applied. Every
        /// attempt has to stamp monotonically per <c>(userId, lineItemId)</c>,
        /// which is what this column exists to guarantee.
        /// </para>
        /// </summary>
        public DateTime? LastTimestamp { get; set; }

        public GradeSyncStatus State { get; set; } = GradeSyncStatus.Pending;

        /// <summary>
        /// How many times the worker has tried. Drives the backoff, and the
        /// ceiling past which a grade is reported as failed rather than left
        /// looking like it is still coming.
        /// </summary>
        public int Attempts { get; set; }

        /// <summary>When the worker may next try. Null means now.</summary>
        public DateTime? NextAttemptAt { get; set; }

        /// <summary>
        /// Why the last attempt failed, in whatever the platform said. Shown to a
        /// manager, because "not synchronised" without a reason is a support
        /// ticket rather than an answer.
        /// </summary>
        public string? LastError { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Where one grade is between AlgoJudge and the gradebook.</summary>
    public enum GradeSyncStatus
    {
        /// <summary>Wanted, not yet posted. The worker will pick it up.</summary>
        Pending = 0,

        /// <summary>Posted, and the platform holds what we intended.</summary>
        Synchronised = 1,

        /// <summary>
        /// Held back on purpose — the participant may not see this score yet, so
        /// neither may the gradebook (§6.3). Not a failure and never reported as
        /// one.
        /// </summary>
        Deferred = 2,

        /// <summary>
        /// Withheld permanently: the activity's scores are for managers only, and
        /// posting would publish through Moodle what AlgoJudge withholds.
        /// </summary>
        Withheld = 3,

        /// <summary>
        /// Tried enough times to stop pretending. A grade that will never arrive
        /// has to look different from one that has not arrived yet.
        /// </summary>
        Failed = 4,
    }
}

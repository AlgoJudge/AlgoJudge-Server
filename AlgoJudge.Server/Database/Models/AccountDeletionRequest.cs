using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    public enum DeletionChannel
    {
        /// <summary>The account holder, from the Client, about their own link.</summary>
        Holder = 0,

        /// <summary>The provider, over the back channel, about somebody it deleted.</summary>
        Provider = 1,
    }

    public enum DeletionState
    {
        /// <summary>Inside its window, waiting to be carried out.</summary>
        Pending = 0,

        /// <summary>Carried out. The link is gone; the account may or may not have been emptied.</summary>
        Completed = 1,

        /// <summary>An administrator stopped it inside the window.</summary>
        Halted = 2,

        /// <summary>
        /// Carried out as far as it goes, and the account was <b>not</b> emptied
        /// because it holds system-scope permissions. Somebody has to look at it.
        /// </summary>
        NeedsAttention = 3,
    }

    /// <summary>
    /// One request to remove an account, from whichever direction it came.
    /// <para>
    /// It is a row rather than an action because of the machine channel: a
    /// provider's request opens a <b>24-hour window in which an administrator can
    /// stop it</b>, and a window needs something to wait in. The two
    /// user-facing channels are immediate and land here already
    /// <see cref="DeletionState.Completed"/> — recorded, not queued, because a
    /// person asking to leave should not have to wait a day to find out whether
    /// they did.
    /// </para>
    /// <para>
    /// <b>The window is the administrator's, not the person's.</b> Their account
    /// at the provider is gone, so they cannot sign in to cancel; describing it
    /// as theirs would promise a screen that cannot exist.
    /// </para>
    /// </summary>
    public class AccountDeletionRequest
    {
        public Guid Id { get; set; } = Uuid.New();

        public DeletionChannel Channel { get; set; }

        /// <summary>Null when the holder asked about every link at once.</summary>
        public Guid? ProviderId { get; set; }
        public IdentityProvider? Provider { get; set; }

        /// <summary>
        /// The provider's <c>sub</c>, on the machine channel. Kept even when it
        /// matched no account here, so a provider can be told the same answer
        /// twice without it meaning something different.
        /// </summary>
        public string? Subject { get; set; }

        /// <summary>Null when nothing here matched the subject.</summary>
        public string? UserId { get; set; }
        public User? User { get; set; }

        /// <summary>
        /// The provider's own identifier for this request. <b>Unique per
        /// provider</b>, which is what makes the channel idempotent: a webhook
        /// that is delivered three times removes one account once.
        /// </summary>
        public string? RequestId { get; set; }

        public DeletionState State { get; set; } = DeletionState.Pending;

        public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the sweeper may carry it out. Equal to
        /// <see cref="RequestedAt"/> on an immediate channel.
        /// </summary>
        public DateTime ExecuteAfter { get; set; }

        public DateTime? ResolvedAt { get; set; }

        public string? HaltedByUserId { get; set; }

        /// <summary>A sentence for whoever reads the queue. Never a stack trace.</summary>
        public string? Detail { get; set; }
    }
}

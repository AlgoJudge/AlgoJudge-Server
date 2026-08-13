using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// Proof that this browser arrived through a launch, handed over once.
    /// <para>
    /// <b>§5.2 says the embedded mode is entered because of how the session was
    /// established, not because a URL said so</b> — "a query parameter anybody
    /// may set is a way to make the full interface look confined". So the
    /// redirect carries a ticket rather than a mode: an opaque, single-use,
    /// short-lived value the Server issued, which the Client exchanges for the
    /// launch's context.
    /// </para>
    /// <para>
    /// A ticket is not authorization and does not pretend to be. What somebody
    /// may read and do is decided by their grants, exactly as everywhere else;
    /// this decides what the interface looks like around it.
    /// </para>
    /// </summary>
    public class LaunchTicket
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>The opaque value in the redirect. Looked up, then destroyed.</summary>
        public required string Ticket { get; set; }

        /// <summary>
        /// Whose launch it was. Checked against the signed-in caller when the
        /// ticket is claimed — a ticket that leaked is then useless to anybody
        /// but its owner.
        /// </summary>
        public required string UserId { get; set; }

        public Guid ResourceLinkId { get; set; }

        /// <summary>From `launch_presentation.locale` (§5.4).</summary>
        public string? Locale { get; set; }

        /// <summary>
        /// Whether the platform framed it. `iframe` is the learner's default and
        /// a manager's configuration work opens in a window instead (§5.2).
        /// </summary>
        public bool Embedded { get; set; }

        /// <summary>
        /// Where the platform wants a finished launch to return to, if the
        /// interface offers that.
        /// </summary>
        public string? ReturnUrl { get; set; }

        /// <summary>
        /// Very short. The Client claims it on the first render after the
        /// redirect; anything longer is a window in which a leaked URL is worth
        /// something.
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

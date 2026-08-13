using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// One launch in flight: the <c>state</c> and <c>nonce</c> a login initiation
    /// created, waiting for the <c>id_token</c> that answers them.
    /// <para>
    /// <b>This table exists because of a measurement, and would not otherwise.</b>
    /// The specification's own answer to third-party cookie loss is LTI Platform
    /// Storage — the login initiation carries an <c>lti_storage_target</c> and
    /// the tool parks its <c>state</c> in the <i>platform's</i> storage over
    /// <c>postMessage</c> instead of in a cookie of its own. Measured 2026-08-13
    /// against Moodle 4.5.13, 5.2.2 and 5.3dev: <b>none of them implement it</b>,
    /// and <c>mod/lti</c> contains no <c>postMessage</c> at all. So the tool
    /// carries the state itself, server-side, and the launch stops depending on a
    /// cookie surviving an iframe.
    /// </para>
    /// <para>
    /// <b>Used once and then gone.</b> A <c>nonce</c> that can be replayed is not
    /// a nonce; consumption is a conditional delete, so two arrivals of the same
    /// token race and exactly one wins.
    /// </para>
    /// </summary>
    public class LaunchState
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// The opaque <c>state</c> handed to the platform and expected back. The
        /// lookup key, and the CSRF defence.
        /// </summary>
        public required string State { get; set; }

        /// <summary>The <c>nonce</c> the returned <c>id_token</c> must carry.</summary>
        public required string Nonce { get; set; }

        public Guid PlatformId { get; set; }

        /// <summary>
        /// Where the launch was going, from the initiation's <c>target_link_uri</c>.
        /// Kept so the launch can finish where the platform intended rather than
        /// at a default the tool picked.
        /// </summary>
        public string? TargetLinkUri { get; set; }

        /// <summary>
        /// Short. A launch is a redirect chain a person is waiting through, not
        /// something that resumes tomorrow, and every minute this lives is a
        /// minute a stolen <c>state</c> is worth something.
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

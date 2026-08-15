using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// Permission, granted in advance, for one platform to register itself.
    ///
    /// <para>
    /// <b>Dynamic Registration is driven by the other side.</b> A Moodle
    /// administrator pastes the tool's registration address into their own site,
    /// and their Moodle opens it — in an iframe, anonymously, with an
    /// <c>openid_configuration</c> URL and a token it minted itself. Nothing in
    /// that flow proves to us who is at the other end, which is exactly why
    /// §10's decision says a dynamic registration <b>never confers identity
    /// authority</b>.
    /// </para>
    ///
    /// <para>
    /// Left open, that endpoint is two things nobody wants: a way for anybody to
    /// fill the platform list with rows, and a blind SSRF — we would fetch any
    /// URL a stranger put in a query string. So a registration has to be expected
    /// first: a manager here creates one of these, hands the address to whoever
    /// administers the platform, and the endpoint does nothing at all without a
    /// live code.
    /// </para>
    ///
    /// <para>
    /// Short-lived and single-use, because it is a handshake between two people
    /// arranging something now — not a credential to keep.
    /// </para>
    /// </summary>
    public class RegistrationInvitation
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// The secret in the address. Opaque and random: it is guessed or it is
        /// not, and there is nothing in it to reason about.
        /// </summary>
        public required string Code { get; set; }

        /// <summary>Who expected this registration, so the list says who let a platform in.</summary>
        public required string CreatedByUserId { get; set; }

        /// <summary>What the manager called it while waiting for the platform to arrive.</summary>
        public string? Note { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// When it was spent. <b>Set by a conditional update</b>, not by reading
        /// the row and writing it back: two platforms arriving at once would
        /// otherwise both find it unused.
        /// </summary>
        public DateTime? UsedAt { get; set; }

        /// <summary>What it turned into, once it was spent.</summary>
        public Guid? PlatformId { get; set; }
    }
}

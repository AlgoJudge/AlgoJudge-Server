using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// The RSA key pair this tool signs its own assertions with.
    /// <para>
    /// <b>Generated in the Server, and the private half never leaves it</b> —
    /// §9 of <c>LMS_INTEGRATION.md</c>, and one of the decisions the project
    /// owner approved. No endpoint returns it, no projection carries a field for
    /// it, no registration screen shows it and no export includes it. That is
    /// asserted by <c>ToolKeyDisclosureTests</c> rather than by this paragraph.
    /// </para>
    /// <para>
    /// The pair replaces the thesis's arrangement, where a key was generated
    /// elsewhere and pasted into configuration by hand — which is how a private
    /// key ends up in a chat message.
    /// </para>
    /// </summary>
    public class ToolKey
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// The key id, as it appears in the JWKS and in the header of every
        /// assertion this tool signs. Stable for the key's life: a platform
        /// caches the key set, and a kid that changes without the set changing is
        /// a signature nobody can check.
        /// </summary>
        public required string Kid { get; set; }

        /// <summary>The public half, PEM. Published at the JWKS endpoint.</summary>
        public required string PublicPem { get; set; }

        /// <summary>
        /// The private half, PEM. <b>Write-only in every sense the Server has:</b>
        /// read by the signing code and by nothing else.
        /// </summary>
        public required string PrivatePem { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this key stopped being used for new signatures.
        /// <para>
        /// A retired key <b>stays in the JWKS</b> until nothing it signed can
        /// still be in flight. Rotation without an overlap is an outage: a
        /// platform that cached the old set rejects everything signed with the
        /// new one, and a platform that fetched the new set rejects everything
        /// still in flight from the old.
        /// </para>
        /// </summary>
        public DateTime? RetiredAt { get; set; }
    }
}

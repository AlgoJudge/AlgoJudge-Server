using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// A link between one AlgoJudge account and one person as one platform knows
    /// them.
    /// <para>
    /// <b>Keyed on the platform and the platform's <c>sub</c>.</b> An LTI
    /// <c>sub</c> is opaque and scoped to the platform that issued it — it is not
    /// comparable with an OIDC <c>sub</c> from another issuer even for the same
    /// human, which is exactly why the link is stored rather than recomputed at
    /// every launch (§4.2).
    /// </para>
    /// <para>
    /// <b>Written once.</b> A later launch does not rewrite it. A launch
    /// asserting a different username for a subject already linked is a conflict
    /// to report, not a link to move — the alternative hands one person's history
    /// to another because somebody edited a field in Moodle (§4.3).
    /// </para>
    /// </summary>
    public class ExternalIdentity
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid PlatformId { get; set; }
        public Platform? Platform { get; set; }

        /// <summary>
        /// The platform's <c>sub</c>. Opaque, never parsed, never displayed as
        /// though it meant something.
        /// </summary>
        public required string Subject { get; set; }

        public required string UserId { get; set; }

        /// <summary>
        /// What this link is worth as evidence.
        /// <para>
        /// Two strengths exist because NRPS (milestone 2) makes links before
        /// anybody has launched, on weaker evidence — a roster carries a user id,
        /// a name, roles and usually an address, but not necessarily a username.
        /// Conflating the two would let the weaker one silently carry a grade
        /// (§4.4).
        /// </para>
        /// </summary>
        public LinkStrength Strength { get; set; } = LinkStrength.Confirmed;

        /// <summary>
        /// What the platform asserted when the link was made, kept so a later
        /// disagreement can be reported with both halves rather than as "does not
        /// match".
        /// </summary>
        public string? AssertedUsername { get; set; }

        public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastLaunchAt { get; set; }
    }

    /// <summary>How much a link is trusted, and therefore what it may carry.</summary>
    public enum LinkStrength
    {
        /// <summary>
        /// Made by a roster, matched on an address or a sourcedId. May receive a
        /// grade <b>only</b> where the platform is an identity authority (§4.5).
        /// </summary>
        Provisional = 0,

        /// <summary>
        /// Made by a launch, matched on the asserted username. A launch by that
        /// person upgrades a provisional link to this.
        /// </summary>
        Confirmed = 1,
    }
}

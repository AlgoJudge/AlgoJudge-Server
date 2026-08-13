using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// One LMS that may launch into this installation.
    /// <para>
    /// <b>A platform is two rows, not one.</b> This one holds everything the LTI
    /// protocol needs; an ordinary <c>IdentityProvider</c> row holds everything
    /// the rest of the Server already knows how to do with a provider — the
    /// claim path, the mapping allowlist onto templates, and being nameable by
    /// <c>Grant.SourceProviderId</c>. The module creates that row and owns its
    /// lifetime.
    /// </para>
    /// <para>
    /// Decided 2026-08-13 by the project owner, against the single-entity shape
    /// proposed in <c>ONE_PERSON_ONE_ACCOUNT_2026-08-10.md</c> §7. The reason is
    /// concrete rather than aesthetic: a course grant produced by a launch has to
    /// name its source, <c>Grant.SourceProviderId</c> already points at
    /// <c>IdentityProvider</c>, and the alternative was either a second source
    /// column on a core table or LTI-only columns on one. Both would have broken
    /// §8 of <c>LMS_INTEGRATION.md</c> — that this module can be deleted in one
    /// commit touching nothing else.
    /// </para>
    /// </summary>
    public class Platform
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// The <c>IdentityProvider</c> row this platform speaks through.
        /// <para>
        /// <b>A plain column, with no foreign key and no navigation</b>, because
        /// the row it names lives in a different <c>DbContext</c> — which is what
        /// keeps this module's tables out of <c>ApplicationDbContext</c>. The
        /// invariant is real and is enforced in <c>PlatformService</c>: the
        /// provider is created with the platform and deleted with it. It is the
        /// same trade already accepted for <c>FileContents</c>, and stated for
        /// the same reason: a constraint that cannot exist is better named than
        /// implied.
        /// </para>
        /// </summary>
        public Guid ProviderId { get; set; }

        /// <summary>What an operator calls it. Free text, changed freely.</summary>
        public required string DisplayName { get; set; }

        /// <summary>
        /// The platform's issuer, from its own configuration. Half of the key an
        /// <c>id_token</c> is validated against.
        /// </summary>
        public required string Issuer { get; set; }

        /// <summary>The client id this platform issued to us.</summary>
        public required string ClientId { get; set; }

        /// <summary>
        /// Which registration of the tool inside that platform. One Moodle can
        /// register the same tool twice; the deployment is what tells them apart,
        /// and it arrives in every launch.
        /// </summary>
        public required string DeploymentId { get; set; }

        /// <summary>Where the platform publishes the keys its tokens are signed with.</summary>
        public required string KeySetUrl { get; set; }

        /// <summary>Where the tool asks for an access token, for AGS and NRPS.</summary>
        public required string AuthTokenUrl { get; set; }

        /// <summary>Where a launch's OIDC login initiation is answered to.</summary>
        public required string AuthLoginUrl { get; set; }

        /// <summary>
        /// Whether this platform may assert who somebody is, inside
        /// <see cref="IdentityNamespace"/>.
        /// <para>
        /// <b>This is the flag §4.5 of <c>LMS_INTEGRATION.md</c> is about, and it
        /// is the most dangerous field in this module.</b> Matching a launch onto
        /// an account by an asserted username means trusting the platform to make
        /// claims inside the identity provider's namespace: a compromised or
        /// carelessly registered platform can assert any username and be handed
        /// the corresponding account.
        /// </para>
        /// <para>
        /// So it is off unless somebody turns it on by hand, and <b>Dynamic
        /// Registration does not confer it</b> (§4.5, milestone 3). A platform
        /// without it still works — its launches land on §4.4's page offering
        /// sign-in through SSO — and its roster produces no gradeable link.
        /// </para>
        /// </summary>
        public bool IsIdentityAuthority { get; set; }

        /// <summary>
        /// The suffix an asserted username must carry for
        /// <see cref="IsIdentityAuthority"/> to apply — a domain, typically.
        /// Empty with the flag set is refused: authority over everything is not a
        /// namespace.
        /// </summary>
        public string? IdentityNamespace { get; set; }

        /// <summary>
        /// Which custom parameter carries the username.
        /// <para>
        /// Configurable rather than fixed, because the platform side is a string
        /// an operator types into the tool's configuration. The default matches
        /// what the reference stack was registered with, and
        /// <c>$User.username</c> is what fills it — measured 2026-08-13 as
        /// substituted by Moodle 4.5.13, 5.2.2 and 5.3dev alike.
        /// </para>
        /// </summary>
        public string UsernameClaim { get; set; } = "username";

        /// <summary>
        /// Disabled refuses launches and stops grade traffic. It withdraws
        /// nothing already granted — the same rule the OIDC providers follow,
        /// and for the same reason: turning a platform off to reconfigure it
        /// should not silently remove a course from everybody in it.
        /// </summary>
        public bool Enabled { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<ResourceLink> ResourceLinks { get; set; } = new List<ResourceLink>();
    }
}

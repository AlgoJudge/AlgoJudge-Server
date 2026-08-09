using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// What happens when a token arrives carrying no value this installation maps.
    /// </summary>
    public enum UnmappedBehavior
    {
        /// <summary>
        /// Refuse the sign-in, and create no account on a first attempt.
        /// <para>
        /// The default, and safe as one only because the Server keeps its own
        /// Identity permanently: a claim path somebody mistyped cannot lock an
        /// installation out, because an administrator still signs in locally.
        /// </para>
        /// </summary>
        Deny = 0,

        /// <summary>Admit, and grant the provider's configured default template.</summary>
        DefaultTemplate = 1,
    }

    /// <summary>
    /// An OIDC provider this installation trusts.
    /// <para>
    /// Several may be registered at once — a university's SSO,
    /// <c>auth.algojudge.app</c>, and whatever else an operator adds — and each
    /// is edited from the panel rather than from configuration on disk. That was
    /// a deliberate choice with a recorded cost: a mapping somebody can edit in a
    /// browser is a privilege-escalation path unless the guards in
    /// <see cref="IdentityProviderMappingRule"/> hold, so those guards are
    /// validated rather than documented.
    /// </para>
    /// <para>
    /// The Server sees a <b>plain OIDC provider</b> and nothing more. That
    /// Authentik happens to sit behind one of these, and implements the deletion
    /// channel with an event matcher policy and a webhook, is known only to
    /// <c>AlgoJudge-Identity</c>. Nothing here may grow a field that only one
    /// product could fill.
    /// </para>
    /// </summary>
    public class IdentityProvider
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// Stable, lowercase, URL-safe: it appears in the sign-in path, so
        /// renaming one breaks every bookmark and every redirect URI registered
        /// on the provider's side.
        /// </summary>
        public required string Slug { get; set; }

        /// <summary>What the sign-in button says. Changed freely.</summary>
        public required string DisplayName { get; set; }

        /// <summary>
        /// The issuer, from which discovery finds everything else. Half of the
        /// federated key: an account is identified by issuer plus <c>sub</c>, so
        /// changing this on a provider that already has users repoints all of
        /// them.
        /// </summary>
        public required string Issuer { get; set; }

        public required string ClientId { get; set; }

        /// <summary>
        /// <b>Write-only.</b> Stored as it was given, and returned by nothing:
        /// no endpoint discloses it, no projection carries a field for it, and
        /// the panel shows whether one is set rather than what it is.
        /// <para>
        /// Plaintext by decision (2026-08-10), reversing an earlier requirement
        /// to encrypt it. Encryption would have relocated the secret rather than
        /// removed it — the key has to live outside the database, or a backup
        /// carries both halves — so the exposure is stated instead of engineered
        /// around: <b>a database backup carries a usable provider credential</b>
        /// and has to be handled as one.
        /// </para>
        /// </summary>
        public required string ClientSecret { get; set; }

        /// <summary>
        /// Space-separated, as the OIDC request carries them. <c>openid</c> is
        /// always requested whether or not it is listed here.
        /// </summary>
        public string Scopes { get; set; } = "openid profile email";

        /// <summary>
        /// Disabled hides it from the sign-in screen and refuses its callback.
        /// It does <b>not</b> withdraw what it has already contributed: that is a
        /// decision about people's access, and turning a provider off to
        /// reconfigure it should not silently demote everybody who signed in
        /// through it.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Where a person edits their own details, because they cannot edit them
        /// here. <b>Configuration, not discovery</b> — OIDC standardises no
        /// account-management URL, so there is nothing to look up and a guess
        /// would send people to a 404 on somebody else's domain.
        /// </summary>
        public string? AccountUrl { get; set; }

        /// <summary>
        /// Where in the token the mapped value lives, as a dotted path —
        /// <c>groups</c>, <c>realm_access.roles</c>, whatever this provider
        /// emits. A path and never an expression: an expression in provider
        /// configuration is code executed against the contents of a token, and
        /// that does not belong in the Server.
        /// </summary>
        public string ClaimPath { get; set; } = "groups";

        public UnmappedBehavior UnmappedBehavior { get; set; } = UnmappedBehavior.Deny;

        /// <summary>
        /// The template granted under <see cref="UnmappedBehavior.DefaultTemplate"/>.
        /// Null under <c>Deny</c>, where there is nothing to grant.
        /// </summary>
        public string? DefaultTemplateName { get; set; }

        /// <summary>
        /// Whether this provider may report a deleted account over the back
        /// channel. Per provider, because trusting one directory to say "this
        /// person is gone" says nothing about trusting another.
        /// </summary>
        public bool DeletionChannelEnabled { get; set; }

        /// <summary>
        /// The shared secret that channel authenticates with. <b>Write-only</b>,
        /// on the same terms as <see cref="ClientSecret"/>.
        /// </summary>
        public string? DeletionSecret { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The allowlist: which claim values map onto which template. Empty is a
        /// legitimate state and means every sign-in falls to
        /// <see cref="UnmappedBehavior"/>.
        /// </summary>
        public ICollection<IdentityProviderMappingRule> MappingRules { get; set; }
            = new List<IdentityProviderMappingRule>();

        public ICollection<UserIdentity> Identities { get; set; } = new List<UserIdentity>();
    }

    /// <summary>
    /// One line of the allowlist: this claim value grants this template.
    /// <para>
    /// <b>This is the one object that holds a live reference to a
    /// <see cref="PermissionTemplate"/>.</b> A grant does not — choosing a
    /// template copies its permissions and nothing points back afterwards, which
    /// is the whole reason it is a template rather than a role. A rule is
    /// different: the contribution is re-derived from it at every sign-in, so
    /// editing the template it names <i>does</i> reach people, at their next
    /// sign-in. Two objects, two lifetimes, and the panel has to say which is
    /// which or an administrator will edit a template expecting it to touch
    /// nobody.
    /// </para>
    /// <para>
    /// It names the template by <see cref="TemplateName"/> rather than by id
    /// because that is what an operator configures and what the provider's
    /// documentation will talk about. Deleting a named template is refused.
    /// </para>
    /// </summary>
    public class IdentityProviderMappingRule
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ProviderId { get; set; }
        public IdentityProvider? Provider { get; set; }

        /// <summary>
        /// The value to match at <see cref="IdentityProvider.ClaimPath"/>,
        /// compared by exact string equality. No patterns and no prefixes: a
        /// wildcard in an allowlist is how an allowlist stops being one.
        /// </summary>
        public required string ClaimValue { get; set; }

        /// <summary>The <see cref="PermissionTemplate.Name"/> this value grants.</summary>
        public required string TemplateName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A link between one AlgoJudge account and one identity at one provider.
    /// <para>
    /// <b>Keyed on issuer plus <c>sub</c>, never on the email address.</b> That
    /// is the finding this whole model is built around: an address is something a
    /// person changes at their provider, and a federation keyed on it hands the
    /// account to whoever inherits the address. <c>sub</c> is the only value a
    /// provider promises is stable and its own.
    /// </para>
    /// <para>
    /// An account may hold several of these — a university login and
    /// <c>auth.algojudge.app</c> are two ways into the same person — and losing
    /// one does not end the account while another way in remains.
    /// </para>
    /// </summary>
    public class UserIdentity
    {
        public Guid Id { get; set; } = Uuid.New();

        public required string UserId { get; set; }
        public User? User { get; set; }

        public Guid ProviderId { get; set; }
        public IdentityProvider? Provider { get; set; }

        /// <summary>
        /// The provider's <c>sub</c>. Opaque, never parsed, never displayed as
        /// though it meant something.
        /// </summary>
        public required string Subject { get; set; }

        public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastSignInAt { get; set; }
    }
}

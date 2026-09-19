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

        /// <summary>Admit, and grant the provider's configured default roles.</summary>
        DefaultRole = 1,
    }

    /// <summary>What a provider row is for.</summary>
    public enum ProviderKind
    {
        /// <summary>A door people sign in through, offered on the sign-in screen.</summary>
        SignIn = 0,

        /// <summary>
        /// A system that vouches for people without being a door: it names the
        /// source of a grant's roles and nothing else. Never listed as a
        /// provider, never enabled, never deleted from the providers screen.
        /// </summary>
        Attribution = 1,
    }

    /// <summary>What one mapping rule hands out.</summary>
    public enum MappingTarget
    {
        /// <summary>The role named by <see cref="IdentityProviderMappingRule.RoleId"/>.</summary>
        Role = 0,

        /// <summary>Whatever the activity enrolls participants into.</summary>
        ActivityParticipants = 1,

        /// <summary>Whatever the activity enrolls its managers into.</summary>
        ActivityManagers = 2,
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
    /// The Server sees a <b>plain OIDC provider</b> and nothing more.
    /// <b>Two</b> different products sit behind these in practice, and neither
    /// is named anywhere in this model. That one implements the deletion
    /// channel with an event matcher policy and a webhook, and the other with
    /// an event listener extension, is known only to those deployments.
    /// </para>
    /// <para>
    /// Nothing here may grow a field that only one product could fill. Two
    /// products behind one model is the evidence for that rule rather than an
    /// exception to it: what differs between them is which of the optional
    /// switches an operator sets, and every one of those already exists.
    /// <para>
    /// 2026-08-27 is the strongest evidence so far. The Keycloak deployment
    /// gained a deletion back channel it had been documented as unable to have,
    /// built a completely different way from the Authentik one — and
    /// <b>not one line here changed</b>. A model that had grown a field for "how
    /// this product reports a deletion" would have had to.
    /// </para>
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
        /// here. <b>Configuration, not discovery</b> — OIDC standardizes no
        /// account-management URL, so there is nothing to look up and a guess
        /// would send people to a 404 on somebody else's domain.
        /// </summary>
        public string? AccountUrl { get; set; }

        /// <summary>
        /// Where a person deletes their account <b>at the provider</b>.
        /// <para>
        /// A different address and a different act from <see cref="AccountUrl"/>:
        /// one is where they edit their details, this is where the identity
        /// itself ends. Keeping them apart matters because the account screen
        /// offers them side by side and they are not interchangeable — sending
        /// somebody who wants to leave to a profile editor is the kind of
        /// helpfulness that reads as a runaround.
        /// </para>
        /// <para>
        /// **Configuration, not discovery**, for the same reason as the other:
        /// OIDC standardizes no such URL. Absent means the account screen offers
        /// only what this installation can do by itself — which is honest, and
        /// is why nothing here guesses one.
        /// </para>
        /// </summary>
        public string? DeletionUrl { get; set; }

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
        /// What this row is for: somebody signing in through it, or somebody an
        /// LTI platform vouched for.
        /// <para>
        /// A platform needs a provider row so a grant's roles can say where they
        /// came from and so a launch can be attributed, but it is not a door
        /// anybody signs in through. Without this field the panel offered one as
        /// an ordinary provider, complete with an enable switch and a delete
        /// button that break every launch from that course.
        /// </para>
        /// <para>
        /// Named for what the row does rather than for the module that writes
        /// it: nothing in the core knows what an LTI platform is, and this field
        /// does not teach it.
        /// </para>
        /// </summary>
        public ProviderKind Kind { get; set; } = ProviderKind.SignIn;

        /// <summary>
        /// The roles granted under <see cref="UnmappedBehavior.DefaultRole"/>.
        /// Empty under <c>Deny</c>, where there is nothing to grant.
        /// <para>
        /// A set, like a rule's: a default that could name only one role while a
        /// matched rule may name several would be a distinction an operator
        /// trips over rather than one the model needs.
        /// </para>
        /// </summary>
        public ICollection<IdentityProviderDefaultRole> DefaultRoles { get; set; }
            = new List<IdentityProviderDefaultRole>();

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
        /// The allowlist: which claim values map onto which role. Empty is a
        /// legitimate state and means every sign-in falls to
        /// <see cref="UnmappedBehavior"/>.
        /// </summary>
        public ICollection<IdentityProviderMappingRule> MappingRules { get; set; }
            = new List<IdentityProviderMappingRule>();

        public ICollection<UserIdentity> Identities { get; set; } = new List<UserIdentity>();
    }

    /// <summary>
    /// One line of the allowlist: this claim value grants this role.
    /// <para>
    /// <b>The contribution this writes is a set of links.</b> A claim may match
    /// several rules at once and what the person holds is the union of every
    /// role they name — which is what a grant linking several roles can express
    /// and a single link could not. It is still rewritten from the rules at
    /// every sign-in, so a group taken away at the directory is taken away here.
    /// </para>
    /// <para>
    /// It names the role by <see cref="RoleId"/>. By name it was a reference
    /// nothing enforced: names are unique only within a scope, so renaming an
    /// activity's role rewrote the rules of every provider that named an
    /// installation role of the same name — one course's manager deciding what a
    /// directory group buys installation-wide. Only an installation role may be
    /// named, and deleting a named one is refused.
    /// </para>
    /// </summary>
    public class IdentityProviderMappingRule
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ProviderId { get; set; }
        public IdentityProvider? Provider { get; set; }

        /// <summary>
        /// The value to match at <see cref="IdentityProvider.ClaimPath"/>, or —
        /// for a platform — the role a launch carries, compared by exact string
        /// equality. No patterns and no prefixes: a wildcard in an allowlist is
        /// how an allowlist stops being one.
        /// </summary>
        public required string ClaimValue { get; set; }

        /// <summary>
        /// What this value grants: a named role, or one of the activity's two
        /// enrollment sets. Only a platform's rules may aim at a slot — an OIDC
        /// contribution is system scope, where there is no activity to resolve
        /// one against.
        /// </summary>
        public MappingTarget Target { get; set; } = MappingTarget.Role;

        /// <summary>
        /// The role this value grants, or null when <see cref="Target"/> names a
        /// slot instead.
        /// </summary>
        public Guid? RoleId { get; set; }
        public Role? Role { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// One role a provider hands out when a token matched no rule and
    /// <see cref="UnmappedBehavior.DefaultRole"/> is set.
    /// </summary>
    public class IdentityProviderDefaultRole
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ProviderId { get; set; }
        public IdentityProvider? Provider { get; set; }

        public Guid RoleId { get; set; }
        public Role? Role { get; set; }
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

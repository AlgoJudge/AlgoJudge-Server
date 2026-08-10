namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// The identity-provider surface, behind `provider:manage`.
    /// <para>
    /// <b>No type in this file has a field for a secret.</b> That is the whole
    /// enforcement of "write-only": a stored secret cannot leak through a
    /// projection that has nowhere to put it, and a reviewer can check the rule
    /// by reading one file rather than every call site. What the panel gets
    /// instead is <see cref="IdentityProviderDto.HasClientSecret"/> — whether one
    /// is set, which is the only thing a form needs to know.
    /// </para>
    /// </summary>

    /// <summary>
    /// What a signed-out screen is told about a provider: enough to draw a
    /// button and to build the link that starts a sign-in.
    /// <para>
    /// Deliberately two fields. Anything more would be describing an
    /// installation's identity configuration to anybody who can load its login
    /// page.
    /// </para>
    /// </summary>
    public record PublicProviderDto
    {
        public required string Slug { get; init; }
        public required string DisplayName { get; init; }
    }

    public record MappingRuleDto
    {
        /// <summary>The value at the provider's claim path, matched exactly.</summary>
        public required string ClaimValue { get; init; }

        /// <summary>The permission template that value grants.</summary>
        public required string TemplateName { get; init; }
    }

    public record IdentityProviderDto
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string DisplayName { get; init; }
        public required string Issuer { get; init; }
        public required string ClientId { get; init; }
        public required string Scopes { get; init; }
        public required bool Enabled { get; init; }
        public string? AccountUrl { get; init; }
        public required string ClaimPath { get; init; }

        /// <summary>`deny` | `defaultTemplate`.</summary>
        public required string UnmappedBehavior { get; init; }

        public string? DefaultTemplateName { get; init; }
        public required bool DeletionChannelEnabled { get; init; }

        /// <summary>
        /// Whether a secret is stored — never which. A form showing an empty box
        /// where a value exists reads as a loss and invites somebody to retype
        /// one they do not have.
        /// </summary>
        public required bool HasClientSecret { get; init; }
        public required bool HasDeletionSecret { get; init; }

        /// <summary>
        /// The path the provider must send the browser back to, **relative to
        /// this API's origin** — so `https://api.example.edu` plus this is what
        /// goes into the provider's redirect-URI allowlist.
        /// <para>
        /// Sent rather than left to the operator to work out. It is derived from
        /// the slug, it has to match exactly on both sides, and a registration
        /// that gets it wrong fails at the end of somebody's first sign-in with
        /// an error from the provider rather than from us.
        /// </para>
        /// </summary>
        public required string CallbackPath { get; init; }

        public required IReadOnlyList<MappingRuleDto> MappingRules { get; init; }

        /// <summary>
        /// How many accounts are linked. Shown because disabling or deleting a
        /// provider with people behind it is a different act from disabling an
        /// empty one, and the screen should say so before it is done.
        /// </summary>
        public required int LinkedAccounts { get; init; }

        public required string CreatedAt { get; init; }
    }

    public record IdentityProviderInputDto
    {
        public required string Slug { get; init; }
        public required string DisplayName { get; init; }
        public required string Issuer { get; init; }
        public required string ClientId { get; init; }

        /// <summary>
        /// Required when registering. On an update, <b>absent means "leave the
        /// stored one alone"</b> — the alternative would be a panel that has to
        /// send back a secret it was never given, which is how a write-only field
        /// quietly becomes a readable one.
        /// </summary>
        public string? ClientSecret { get; init; }

        public string? Scopes { get; init; }
        public bool Enabled { get; init; } = true;
        public string? AccountUrl { get; init; }
        public string? ClaimPath { get; init; }

        /// <summary>`deny` | `defaultTemplate`. Absent is `deny`.</summary>
        public string? UnmappedBehavior { get; init; }

        public string? DefaultTemplateName { get; init; }
        public bool DeletionChannelEnabled { get; init; }

        /// <summary>Absent means "leave the stored one alone", as above.</summary>
        public string? DeletionSecret { get; init; }

        /// <summary>
        /// The whole allowlist, replaced wholesale. Absent leaves it as it is;
        /// an empty list clears it, which is a real instruction and not the same
        /// as absent.
        /// </summary>
        public IReadOnlyList<MappingRuleDto>? MappingRules { get; init; }
    }
}

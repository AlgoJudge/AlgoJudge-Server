namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// Removing an account, over the three channels it can be asked for.
    /// </summary>

    /// <summary>
    /// What the account holder sends. Nothing is required: an account with one
    /// link needs no help identifying it.
    /// </summary>
    public record HolderDeletionInputDto
    {
        /// <summary>
        /// The link to remove. **Absent means every one of them** — which is
        /// what "delete my account" means to somebody who signs in through a
        /// provider and holds no password.
        /// </summary>
        public string? ProviderId { get; init; }
    }

    /// <summary>
    /// What a provider sends over the back channel.
    /// <para>
    /// **Generic on purpose.** That Authentik produces this with an event
    /// matcher policy and a webhook transport is known only to
    /// `AlgoJudge-Identity-Authentik`; the Server sees a plain OIDC provider that
    /// may report a deletion, and nothing in this shape could only be filled in
    /// by one product.
    /// <para>
    /// **The other supported deployment cannot send this at all.** Keycloak has
    /// no outbound webhook without a third-party extension, so a Keycloak
    /// provider is registered with its deletion channel off. That costs nothing
    /// here: the switch is per provider precisely because trusting one directory
    /// to say "this person is gone" says nothing about another.
    /// </para>
    /// </para>
    /// </summary>
    public record ProviderDeletionInputDto
    {
        /// <summary>The provider's `sub` for the person it deleted.</summary>
        public required string Subject { get; init; }

        /// <summary>
        /// The provider's own identifier for this request. **Handling is
        /// idempotent on it**: a webhook is retried on any hiccup, and three
        /// deliveries must remove one account once.
        /// </summary>
        public required string RequestId { get; init; }

        /// <summary>
        /// When the provider says it happened. Recorded, and **not** used to
        /// compute the window — a clock this installation does not own must not
        /// be able to shorten an administrator's day to nothing.
        /// </summary>
        public string? RequestedAt { get; init; }
    }

    public record DeletionRequestDto
    {
        public required string Id { get; init; }
        /// <summary>`holder` | `provider`.</summary>
        public required string Channel { get; init; }
        /// <summary>`pending` | `completed` | `halted` | `attention`.</summary>
        public required string State { get; init; }
        public string? ProviderId { get; init; }
        public string? ProviderName { get; init; }
        public string? UserId { get; init; }
        public string? UserLogin { get; init; }
        public required string RequestedAt { get; init; }
        /// <summary>
        /// When it may be carried out. Equal to `requestedAt` on the immediate
        /// channels; a day later on the provider's, which is the window an
        /// administrator has to stop it.
        /// </summary>
        public required string ExecuteAfter { get; init; }
        public string? ResolvedAt { get; init; }
        /// <summary>What happened, in a sentence, for whoever reads the queue.</summary>
        public string? Detail { get; init; }
    }
}

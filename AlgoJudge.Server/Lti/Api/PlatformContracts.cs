namespace AlgoJudge.Server.Lti.Api
{
    /// <summary>
    /// A registered platform, as the panel reads it.
    /// <para>
    /// <b>There is no field here that could carry the tool's private key</b>, and
    /// that is the enforcement rather than the discipline of whoever writes the
    /// projection — the same rule the provider contracts already follow for a
    /// client secret.
    /// </para>
    /// </summary>
    public record PlatformDto
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required string Issuer { get; init; }
        public required string ClientId { get; init; }
        public required string DeploymentId { get; init; }
        public required string KeySetUrl { get; init; }
        public required string AuthTokenUrl { get; init; }
        public required string AuthLoginUrl { get; init; }

        /// <summary>
        /// Whether this platform may assert who somebody is. Shown prominently,
        /// because it is the one setting here that can hand an account to whoever
        /// controls the LMS (§4.5).
        /// </summary>
        public required bool IsIdentityAuthority { get; init; }

        public string? IdentityNamespace { get; init; }
        public required string UsernameClaim { get; init; }
        public required bool Enabled { get; init; }

        /// <summary>
        /// The provider row this platform speaks through, so a manager looking at
        /// a grant sourced by it can find what sourced it.
        /// </summary>
        public required string ProviderId { get; init; }

        public required string CreatedAt { get; init; }
    }

    public record PlatformInputDto
    {
        public required string DisplayName { get; init; }
        public required string Issuer { get; init; }
        public required string ClientId { get; init; }
        public required string DeploymentId { get; init; }
        public required string KeySetUrl { get; init; }
        public required string AuthTokenUrl { get; init; }
        public required string AuthLoginUrl { get; init; }
        public bool IsIdentityAuthority { get; init; }
        public string? IdentityNamespace { get; init; }
        public string? UsernameClaim { get; init; }
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    /// What an operator has to type into the platform's own configuration.
    /// <para>
    /// It exists so that registering a tool is copying five values off a screen
    /// rather than assembling them from documentation and a base URL — which is
    /// where a wrong redirect URI comes from, and a wrong redirect URI fails at
    /// the end of somebody's first launch with an error from Moodle.
    /// </para>
    /// </summary>
    public record ToolRegistrationDto
    {
        public required string ToolUrl { get; init; }
        public required string LoginUrl { get; init; }
        public required string RedirectUri { get; init; }
        public required string KeySetUrl { get; init; }

        /// <summary>
        /// The custom parameters the platform must be configured to send.
        /// <para>
        /// <c>username=$User.username</c> is the one §4.3 rests on, and the one
        /// that is easy to leave out — a launch without it lands on the sign-in
        /// page instead of the activity, which reads like a broken tool.
        /// </para>
        /// </summary>
        public required IReadOnlyList<string> CustomParameters { get; init; }
    }
}

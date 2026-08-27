namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// Setting the administrator's password from the machine itself.
    /// <para>
    /// <b>A body, not a query string.</b> A password in a URL is written into
    /// proxy access logs and shell history, and this one is chosen by a person
    /// under pressure rather than generated — so it is likely to be reused
    /// somewhere else too. The cost is that an operator writing the request by
    /// hand has to count bytes for <c>Content-Length</c>; that is the trade the
    /// product takes, and <c>docs/specs/MAINTENANCE.md</c> shows the shell that
    /// counts them.
    /// </para>
    /// </summary>
    public record AdminPasswordInputDto
    {
        public required string Password { get; init; }
    }

    /// <summary>
    /// Which account was changed. <b>Never the password</b>, which the caller
    /// already knows and nothing else should ever see again.
    /// </summary>
    public record AdminPasswordDto
    {
        public required string Username { get; init; }
    }

    /// <summary>
    /// The key ring as an operator needs to see it: which arrangement is in
    /// force, what is actually stored, and what needs doing about it.
    /// <para>
    /// <b>Never served publicly.</b> It names how many keys exist, when they
    /// expire and whether they are encrypted — an inventory of what protects
    /// every session on the installation.
    /// </para>
    /// </summary>
    public record KeyRingReportDto
    {
        /// <summary><c>database</c> or <c>ephemeral</c>.</summary>
        public required string Kind { get; init; }

        /// <summary>
        /// What Data Protection mixes into every purpose. Two instances that
        /// disagree about it do not share a ring, so it is reported rather than
        /// assumed.
        /// </summary>
        public required string ApplicationName { get; init; }

        public required IReadOnlyList<KeyRingCertificateDto> Certificates { get; init; }
        public required IReadOnlyList<KeyRingKeyDto> Keys { get; init; }

        /// <summary>
        /// What needs an operator's attention, in words that say what to do.
        /// Empty is the ordinary state.
        /// </summary>
        public required IReadOnlyList<string> Problems { get; init; }
    }

    public record KeyRingCertificateDto
    {
        public required string Subject { get; init; }

        /// <summary>The first eight characters, to tell two apart. Not the whole one.</summary>
        public required string Thumbprint { get; init; }

        public required string NotAfter { get; init; }

        /// <summary>
        /// Whether this is the one new keys are encrypted with — the first in
        /// the configured list. Every one listed still decrypts.
        /// </summary>
        public required bool Encrypts { get; init; }
    }

    public record KeyRingKeyDto
    {
        public required string Id { get; init; }
        public required string CreatedAt { get; init; }
        public required string ActivatesAt { get; init; }
        public required string ExpiresAt { get; init; }

        /// <summary>Whether this is the key new cookies are being minted under.</summary>
        public required bool IsActive { get; init; }

        public required bool IsRevoked { get; init; }

        /// <summary>
        /// <c>encrypted</c>, <c>plaintext</c> or <c>unknown</c> — how the key
        /// material sits in the store, which is the question a database backup
        /// raises.
        /// </summary>
        public required string Storage { get; init; }

        /// <summary>
        /// Whether this Server can still read it. <c>false</c> means a
        /// certificate that encrypted it is no longer configured, and every
        /// session minted under it is gone.
        /// </summary>
        public required bool Readable { get; init; }
    }

    /// <summary>What a revoke did, in the only unit that matters: how many.</summary>
    public record KeyRingRevokeDto
    {
        public required int Revoked { get; init; }
        public required string Reason { get; init; }
    }
}

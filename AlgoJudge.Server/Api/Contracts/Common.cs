using System.Text.Json.Serialization;

namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// The wire contract, mirroring `AlgoJudge-Client/src/api/*Api.ts`.
    /// <para>
    /// These are the Client's TypeScript types written in C#. Where a name or a
    /// shape here differs from the Client's, one of the two is wrong — and it is
    /// almost always this one, because the Client's has been exercised by
    /// screens and by a working fake since before the Server had endpoints.
    /// </para>
    /// <para>
    /// Records rather than classes: a response is a value, and nothing should be
    /// able to mutate one after a service has decided what it says. Every
    /// identifier is a <b>string</b> holding a UUID, never a <c>Guid</c> — the
    /// Client's models say `id: string`, and letting the serialiser decide the
    /// casing of a GUID is how "018f2c00-..." becomes "018F2C00-...".
    /// </para>
    /// </summary>
    public static class Wire
    {
        /// <summary>Renders an id the one way the API renders ids.</summary>
        public static string Id(Guid id) => id.ToString("D").ToLowerInvariant();

        /// <summary>
        /// An instant, as the Client parses it: ISO 8601 in UTC with a `Z`.
        /// <para>
        /// `DateTime.Parse` on the Client is `Date.parse`, which reads an offset
        /// but treats a bare local-looking string as local time. Every instant
        /// leaves here with its zone stated.
        /// </para>
        /// </summary>
        public static string? At(DateTime? value) =>
            value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("O");

        public static string At(DateTime value) =>
            DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O");
    }

    /// <summary>One page of a collection. Paging and filtering happen on the Server.</summary>
    public record PageDto<T>
    {
        public required IReadOnlyList<T> Items { get; init; }
        public required int Total { get; init; }
        public required int Page { get; init; }
        public required int PageSize { get; init; }
    }

    // ── Instance ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Where one instance document lives, and what a screen needs before it has
    /// the text. The document itself is fetched from the file API.
    /// </summary>
    public record InstanceDocumentRefDto
    {
        /// <summary>`terms`, `privacy`, `cookies`, `accessibility`, `welcome`, `home`.</summary>
        public required string Kind { get; init; }
        /// <summary>BCP-47 subtag. Absent on the document the operator wrote first.</summary>
        public string? Language { get; init; }
        /// <summary>Absent on the front pages: their heading is inside the document.</summary>
        public string? Title { get; init; }
        /// <summary>When this revision came into force.</summary>
        public string? ValidFrom { get; init; }
        /// <summary>
        /// True while the operator is still using what shipped with the software.
        /// A template names the wrong controller, so the screen says so out loud.
        /// </summary>
        public required bool IsTemplate { get; init; }
        public required string FileId { get; init; }
        public required string Sha256 { get; init; }
        public required long SizeBytes { get; init; }
    }

    public record InstanceLogoDto
    {
        public required string Url { get; init; }
        public required string MimeType { get; init; }
        public required long SizeBytes { get; init; }
        public required string Sha256 { get; init; }
    }

    public record LocalisedLogoDto
    {
        public required string Language { get; init; }
        public required InstanceLogoDto Logo { get; init; }
    }

    /// <summary>What a signed-out screen may know about the installation.</summary>
    public record InstanceInfoDto
    {
        /// <summary>
        /// Absent is a real state, not a missing field: an installation that has
        /// not been named shows the product's name alone rather than a made-up
        /// one.
        /// </summary>
        public string? Name { get; init; }
        public required bool LocalRegistrationEnabled { get; init; }
        public required bool RequireEmail { get; init; }
        public required bool RequireConfirmedEmail { get; init; }
        /// <summary>
        /// Whether this installation may send submissions to a service it does
        /// not run. Shipped off.
        /// <para>
        /// Public, like every other instance setting, and that is a choice worth
        /// stating: it is the fact a privacy notice is written from, so an
        /// installation that forwards somebody's work should not be the only
        /// party that knows. It names no service and no address — only whether
        /// the door is open.
        /// </para>
        /// </summary>
        public required bool ExternalJudgingEnabled { get; init; }
        public required IReadOnlyList<InstanceDocumentRefDto> Documents { get; init; }
        public InstanceLogoDto? Logo { get; init; }
        public IReadOnlyList<LocalisedLogoDto>? LogoTranslations { get; init; }
        public required bool ShowLogo { get; init; }

        /// <summary>
        /// Whether the sign-in screen offers the login-and-password form.
        /// <b>Presentation only</b> — the endpoint behind it stays open, because
        /// administrators and temporary accounts still sign in that way.
        /// </summary>
        public required bool ShowLocalSignIn { get; init; }

        /// <summary>
        /// The identity providers this installation offers, for the buttons on
        /// the sign-in screen.
        /// <para>
        /// It travels here because it has to be readable <b>before anybody has
        /// signed in</b> — which is the whole point of a sign-in button — and
        /// this is the one answer a signed-out screen already fetches.
        /// </para>
        /// <para>
        /// <b>A name and a slug, and nothing else.</b> Everything else on a
        /// provider registration is an operator's business: the issuer, the
        /// client id, the claim path and the mapping are read behind
        /// `provider:manage`, not by whoever loads the login page.
        /// </para>
        /// </summary>
        public required IReadOnlyList<PublicProviderDto> Providers { get; init; }

        /// <summary>Whether a person may remove their own account from here.</summary>
        public required bool AccountDeletionEnabled { get; init; }
    }

    // ── Session ───────────────────────────────────────────────────────────────

    public record SessionDto
    {
        public required string UserId { get; init; }
        public required string Username { get; init; }
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        public string? Email { get; init; }
        public required bool EmailConfirmed { get; init; }
        /// <summary>
        /// False for an account owned by an identity provider. An SSO account may
        /// not change its own name, login, address or password here.
        /// </summary>
        public required bool IsLocal { get; init; }
    }

    /// <summary>
    /// One way the signed-in person can sign in, as their own account screen
    /// needs to describe it.
    /// <para>
    /// <b>Read by the person about themselves</b>, so it carries no `sub`, no
    /// issuer and no client id: an opaque subject displayed on a profile page is
    /// something somebody will eventually paste into a support ticket, and none
    /// of it helps them decide anything.
    /// </para>
    /// </summary>
    public record AccountLinkDto
    {
        public required string ProviderSlug { get; init; }
        public required string DisplayName { get; init; }

        /// <summary>Where they edit their details, if the provider has such a page.</summary>
        public string? AccountUrl { get; init; }

        /// <summary>
        /// Where they delete the account <b>at the provider</b>. Absent means
        /// this installation knows of no such page, and the screen then offers
        /// only what it can do itself rather than inventing a link.
        /// </summary>
        public string? DeletionUrl { get; init; }

        public required string LinkedAt { get; init; }
    }

    public record ProfileInputDto
    {
        public string? FirstName { get; init; }
        public string? LastName { get; init; }
        /// <summary>Changing it is a rename: the login is what other people see.</summary>
        public string? Username { get; init; }
        public string? Email { get; init; }
    }

    public record ChangePasswordInputDto
    {
        public required string CurrentPassword { get; init; }
        public required string NewPassword { get; init; }
    }

    /// <summary>
    /// Deleting an account needs the password, which is why it is a POST with a
    /// body rather than a DELETE — and why it is not really a deletion.
    /// </summary>
    public record DeleteAccountInputDto
    {
        public required string Password { get; init; }
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// An address to fetch into a file.
    /// <para>
    /// Just the address. Neither a name nor a media type is accepted from the
    /// caller: both come from what actually arrived, and taking them here would
    /// let somebody label bytes as something they are not.
    /// </para>
    /// </summary>
    public record FetchFileInputDto
    {
        public required string Url { get; init; }
    }

    public record UploadedFileDto
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string MimeType { get; init; }
        public required long SizeBytes { get; init; }
        public required string Sha256 { get; init; }
        public required string CreatedAt { get; init; }
    }

    /// <summary>
    /// A stored statement, named and pointed at rather than carried. One shape
    /// for both sides of the fence: a participant reading a problem and a manager
    /// editing one ask for the same bytes.
    /// </summary>
    public record StatementRefDto
    {
        /// <summary>`content.md`, `content-en.md`, `content.pdf`. The renderer keys on it.</summary>
        public required string Name { get; init; }
        public string? Language { get; init; }
        public required string FileId { get; init; }
        public required string Sha256 { get; init; }
        public required long SizeBytes { get; init; }
    }

    /// <summary>
    /// One file hanging off a submission or one of its attempts.
    /// <para>
    /// Two names: <see cref="Name"/> is the name within the owner and is what the
    /// activity's visibility table keys on — `source`, `log`, `details`.
    /// <see cref="FileName"/> is what was uploaded and what a person reads.
    /// </para>
    /// </summary>
    public record SubmissionFileDto
    {
        public required string Name { get; init; }
        public required string FileName { get; init; }
        /// <summary>Set on a source file, for the editor's highlighting.</summary>
        public string? Language { get; init; }
        public required string FileId { get; init; }
        public required string Sha256 { get; init; }
        public required long SizeBytes { get; init; }
    }

    /// <summary>The names a Runner attaches by convention. Anything else is a new name.</summary>
    public static class AttachmentNames
    {
        public const string Source = "source";
        public const string Log = "log";
        public const string Details = "details";
    }

    // ── Errors ────────────────────────────────────────────────────────────────

    /// <summary>
    /// RFC 9457, as the Client reads it. Declared so it appears in the OpenAPI
    /// document — a contract whose failures are undocumented is half a contract.
    /// </summary>
    public record ProblemDto
    {
        public string? Title { get; init; }
        public string? Detail { get; init; }
        public int? Status { get; init; }
        /// <summary>Stable across releases. The Client switches on this, not on the status.</summary>
        public string? Code { get; init; }
        [JsonPropertyName("errors")]
        public IDictionary<string, string[]>? Errors { get; init; }
    }
}

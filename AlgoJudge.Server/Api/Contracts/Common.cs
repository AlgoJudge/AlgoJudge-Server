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
    /// Client's models say `id: string`, and letting the serializer decide the
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

    /// <summary>
    /// A filter value on the way in, and <see cref="Wire"/>'s twin: that one
    /// decides how a value leaves, this one how a narrowing arrives.
    /// <para>
    /// Every multi-valued key binds as <c>string[]?</c>, which ASP.NET Core
    /// fills from repeated keys (<c>?state=queued&amp;state=running</c>) and from
    /// a single one alike. The closed vocabularies are additionally split on a
    /// comma, so the form <c>ActivitiesController</c> already served and every
    /// address a manager has pasted into a message keep working unchanged.
    /// </para>
    /// <para>
    /// <b>Null is every, empty is nothing.</b> Somebody who sent no words is
    /// asking an unnarrowed question. Somebody who sent words this product has
    /// no name for is asking a question whose answer is empty — and a filter the
    /// Server cannot honor must never <i>widen</i> what it answers with. That
    /// distinction is the whole of this class: three places used to disagree
    /// about it, and one of them returned an entire print queue to a request
    /// that had asked for one row of it.
    /// </para>
    /// </summary>
    public static class Filter
    {
        /// <summary>
        /// Repeated keys and comma-separated values, flattened. A blank is not a
        /// word, and a value made only of blanks is nobody having narrowed
        /// anything — which is what a cleared control sends.
        /// </summary>
        public static IReadOnlyList<string>? Words(string[]? raw)
        {
            if (raw is null) return null;

            // `?state=` binds as an array holding **one null**, not as an empty
            // one — and the element type is not nullable, so nothing warns. It
            // threw a NullReferenceException and the endpoint answered 500,
            // which is what a cleared control on a screen sends.
            var words = raw
                .SelectMany(value => (value ?? string.Empty).Split(
                    ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();

            return words.Count == 0 ? null : words;
        }

        /// <summary>
        /// The same, <b>without splitting on a comma</b>.
        /// <para>
        /// For a value whose vocabulary the Server does not own. A verdict is a
        /// string this Server promises never to parse, so `Wrong answer, test 3`
        /// is one verdict and not two — and choosing a separator for it would be
        /// parsing it. See <c>Result.Verdict</c>.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string>? Exact(string[]? raw)
        {
            if (raw is null) return null;

            var values = raw
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToList();

            return values.Count == 0 ? null : values;
        }

        /// <summary>
        /// Identifiers. One that is not a UUID is a word nobody named, and goes
        /// the way every unnamed word goes: dropped, leaving the rest to narrow.
        /// </summary>
        public static IReadOnlyList<Guid>? Ids(string[]? raw) =>
            Words(raw) is not { } words
                ? null
                : words
                    .Select(word => Guid.TryParse(word, out var id) ? (Guid?)id : null)
                    .Where(id => id is not null)
                    .Select(id => id!.Value)
                    .ToList();

        /// <summary>
        /// A closed vocabulary. Words with no member behind them are dropped, so
        /// asking for one state this Server knows and one it does not answers
        /// with the first — and asking only for words it does not know answers
        /// with nothing.
        /// </summary>
        public static IReadOnlyList<T>? Of<T>(string[]? raw, Func<string, T?> parse) where T : struct =>
            Words(raw) is not { } words
                ? null
                : words
                    .Select(parse)
                    .Where(value => value is not null)
                    .Select(value => value!.Value)
                    .ToList();
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
        /// <summary>The reference. An address is the caller's to build.</summary>
        public required string FileId { get; init; }
        public required string MimeType { get; init; }
        public required long SizeBytes { get; init; }
        public required string Sha256 { get; init; }
    }

    public record LocalizedLogoDto
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

        /// <summary>
        /// Whether series restrictions are in force at all. Public like the rest
        /// of these — a participant told "this is only available from the
        /// laboratory" is entitled to know the rule is switched on.
        /// </summary>
        public required bool SeriesRestrictionsEnabled { get; init; }
        public required IReadOnlyList<InstanceDocumentRefDto> Documents { get; init; }
        public InstanceLogoDto? Logo { get; init; }
        public IReadOnlyList<LocalizedLogoDto>? LogoTranslations { get; init; }
        public required bool ShowLogo { get; init; }

        /// <summary>
        /// Whether the sign-in screen offers the login-and-password form.
        /// <b>Presentation only</b> — the endpoint behind it stays open, because
        /// administrators and temporary accounts still sign in that way.
        /// </summary>
        public required bool ShowLocalSignIn { get; init; }

        /// <summary>
        /// Whether the home page opens with the product's own introduction, for
        /// a visitor who is not signed in. <b>The switch travels and the content
        /// does not</b> — the words and the picture are the Client's.
        /// </summary>
        public required bool ShowHero { get; init; }

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

        /// <summary>
        /// The provider the sign-in screen sends the browser straight to,
        /// instead of drawing itself. Absent means it draws itself, which is
        /// what an installation that never touched this has.
        /// <para>
        /// <b>It is always a member of <see cref="Providers"/>, or it is
        /// absent.</b> The projection filters it against that very list, so a
        /// screen may build a challenge address out of it without first asking
        /// whether this Server would answer at the other end.
        /// </para>
        /// <para>
        /// <b>What it discloses is one page load's worth.</b> The slug is
        /// already on this answer beside its display name; the only new fact is
        /// <i>that</i> this installation redirects, which anybody learns by
        /// opening the sign-in screen once. Public for the same reason the
        /// buttons are.
        /// </para>
        /// </summary>
        public string? SignInRedirectProvider { get; init; }

        /// <summary>The same, for the registration screen.</summary>
        public string? RegisterRedirectProvider { get; init; }

        /// <summary>Whether a person may remove their own account from here.</summary>
        public required bool AccountDeletionEnabled { get; init; }

        /// <summary>
        /// The operator's colors and typeface. Absent means the installation has
        /// set none and the Client draws the theme it ships with.
        /// <para>
        /// <b>The values travel here rather than as a file reference</b>, unlike
        /// every document beside them. A privacy policy is tens of kilobytes and
        /// is fetched by whoever is about to read it; a theme is under two, and
        /// the shell needs it before the first paint — a second round trip would
        /// guarantee a flash of the wrong colors on every arrival.
        /// </para>
        /// <para>
        /// Public, like every other instance setting, and it has to be: the
        /// sign-in screen is branded before anybody has signed in.
        /// </para>
        /// </summary>
        public InstanceThemeDto? Theme { get; init; }
    }

    /// <summary>
    /// What an installation looks like. <b>Every color optional, and absent
    /// means the product's default</b> — never black and never empty.
    /// </summary>
    public record InstanceThemeDto
    {
        public ThemeColorsDto? Light { get; init; }
        public ThemeColorsDto? Dark { get; init; }
        public string? FontFamily { get; init; }
        public string? FontFamilyHeadings { get; init; }
        /// <summary>The faces to draw with, resolved to addresses.</summary>
        public required IReadOnlyList<InstanceFontDto> Fonts { get; init; }
        /// <summary>
        /// The file this was published from, so the panel can offer it back.
        /// </summary>
        public required string FileId { get; init; }
        public required string Sha256 { get; init; }
    }

    /// <summary>
    /// One color scheme. Both are stated in full: a dark scheme derived from a
    /// light one fails a contrast floor unpredictably, and the browser checks
    /// assert one.
    /// </summary>
    public record ThemeColorsDto
    {
        /* Brand. One hex each; the Client generates the shades from it. */
        public string? Primary { get; init; }
        public string? Secondary { get; init; }
        public string? Accent { get; init; }
        /// <summary>Its own key: in an identity system a link is usually a
        /// different hue rather than a lighter brand color.</summary>
        public string? Link { get; init; }

        /* Surface and text. */
        public string? Body { get; init; }
        public string? Surface { get; init; }
        public string? Text { get; init; }
        public string? Dimmed { get; init; }
        public string? Border { get; init; }

        /* The shell. Hover and muted are mixed from these by the Client. */
        public string? NavBackground { get; init; }
        public string? NavText { get; init; }
        public string? NavActiveBackground { get; init; }
        public string? NavActiveText { get; init; }
        public string? HeaderBackground { get; init; }
        public string? HeaderText { get; init; }
    }

    /// <summary>
    /// One font face, as the Client needs it to write an <c>@font-face</c>.
    /// <para>
    /// <b>The address is built here, from a stored file.</b> An operator names a
    /// face they uploaded and never writes a URL — which is what keeps a value
    /// somebody typed from becoming a request somebody else's browser makes.
    /// </para>
    /// </summary>
    public record InstanceFontDto
    {
        public required string Name { get; init; }
        public required string Family { get; init; }
        public required int Weight { get; init; }
        /// <summary><c>normal</c> or <c>italic</c>.</summary>
        public required string Style { get; init; }
        /// <summary>The reference. An address is the caller's to build.</summary>
        public required string FileId { get; init; }
        public required string Sha256 { get; init; }
        public required long SizeBytes { get; init; }
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

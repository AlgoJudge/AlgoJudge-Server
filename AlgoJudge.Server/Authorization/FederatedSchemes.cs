using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// One authentication scheme per registered provider, resolved at run time.
    /// <para>
    /// Providers live in the database and an operator adds one from the panel, so
    /// the set of schemes is not known when the application starts and
    /// <c>AddOpenIdConnect("name", …)</c> at startup cannot express it. What this
    /// does instead is answer <c>GetSchemeAsync("oidc:slug")</c> from the current
    /// registrations, and build that scheme's options from the row.
    /// </para>
    /// <para>
    /// <b>The protocol itself is not reimplemented here.</b> The scheme is handled
    /// by <see cref="OpenIdConnectHandler"/> — the framework's own — so state,
    /// nonce, PKCE, the code exchange and <c>id_token</c> validation are the
    /// reviewed implementation rather than one written for this product. Identity
    /// phase 2 is a launch gate; this is precisely where writing it ourselves
    /// would be the wrong economy.
    /// </para>
    /// </summary>
    public static class FederatedSchemes
    {
        /// <summary>`oidc:university`, for a provider whose slug is `university`.</summary>
        public const string Prefix = "oidc:";

        public static string For(string slug) => Prefix + slug;

        public static string? SlugOf(string scheme) =>
            scheme.StartsWith(Prefix, StringComparison.Ordinal) ? scheme[Prefix.Length..] : null;

        /// <summary>
        /// Where a provider sends the browser back to, below the API path base.
        /// <para>
        /// One definition, used by the handler options and by what the panel
        /// shows an operator to paste into the provider. Two would be two strings
        /// that have to match exactly and eventually would not.
        /// </para>
        /// </summary>
        public static string CallbackPath(string slug) => $"/identity/providers/{slug}/callback";
    }

    /// <summary>
    /// What the options builder needs about a provider, without holding a
    /// database context open.
    /// </summary>
    public record ProviderRegistration(
        Guid Id, string Slug, string DisplayName, string Issuer,
        string ClientId, string ClientSecret, string Scopes);

    /// <summary>
    /// The registered providers, as of a moment.
    /// <para>
    /// A singleton cache because <c>IOptionsMonitor.Get</c> is synchronous and
    /// runs inside the authentication pipeline, where opening a scoped database
    /// context is not available. It is refreshed on a write and on a short
    /// interval — so a provider registered in the panel is usable without a
    /// restart, and one that was disabled stops being offered.
    /// </para>
    /// </summary>
    public interface IProviderRegistry
    {
        IReadOnlyList<ProviderRegistration> Enabled { get; }
        ProviderRegistration? Find(string slug);
        /// <summary>Called after any write, so the panel's change is immediate.</summary>
        void Invalidate();
    }

    public class ProviderRegistry(IServiceScopeFactory scopes, TimeProvider clock) : IProviderRegistry
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

        // A plain object: System.Threading.Lock arrived in .NET 9 and this
        // targets 8.
        private readonly object gate = new();
        private IReadOnlyList<ProviderRegistration> snapshot = [];
        private DateTimeOffset taken = DateTimeOffset.MinValue;

        public IReadOnlyList<ProviderRegistration> Enabled
        {
            get
            {
                Refresh();
                return snapshot;
            }
        }

        public ProviderRegistration? Find(string slug) =>
            Enabled.FirstOrDefault(p => string.Equals(p.Slug, slug, StringComparison.Ordinal));

        public void Invalidate()
        {
            lock (gate) taken = DateTimeOffset.MinValue;
        }

        private void Refresh()
        {
            lock (gate)
            {
                if (clock.GetUtcNow() - taken < MaxAge) return;

                using var scope = scopes.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                snapshot = [.. context.IdentityProviders
                    .AsNoTracking()
                    .Where(p => p.Enabled)
                    .OrderBy(p => p.DisplayName)
                    .Select(p => new ProviderRegistration(
                        p.Id, p.Slug, p.DisplayName, p.Issuer, p.ClientId, p.ClientSecret, p.Scopes))];

                taken = clock.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// Answers for the dynamic schemes, and defers everything else to the
    /// framework's own provider.
    /// </summary>
    public class FederatedSchemeProvider(
        IOptions<AuthenticationOptions> options,
        IProviderRegistry registry
    ) : AuthenticationSchemeProvider(options)
    {
        public override async Task<AuthenticationScheme?> GetSchemeAsync(string name)
        {
            var known = await base.GetSchemeAsync(name);
            if (known is not null) return known;

            if (FederatedSchemes.SlugOf(name) is not { } slug) return null;
            if (registry.Find(slug) is not { } provider) return null;

            return Scheme(provider);
        }

        /// <summary>
        /// **The other half of a dynamic scheme provider, and the half that is
        /// easy to leave out.**
        /// <para>
        /// The authentication middleware asks this — not
        /// <see cref="GetSchemeAsync"/> — which schemes want to look at a request
        /// before routing. `OpenIdConnectHandler` handles its own callback path
        /// that way, so a provider that answers only `GetSchemeAsync` will
        /// redirect a person to the identity provider perfectly well and then
        /// answer **404** when they come back: the callback falls through to
        /// MVC, which has no such route.
        /// </para>
        /// <para>
        /// Nothing in the framework warns about it, and every test that stops at
        /// the redirect passes.
        /// </para>
        /// </summary>
        public override async Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync()
        {
            var known = await base.GetRequestHandlerSchemesAsync();
            return known.Concat(registry.Enabled.Select(Scheme));
        }

        private static AuthenticationScheme Scheme(ProviderRegistration provider) =>
            new(FederatedSchemes.For(provider.Slug), provider.DisplayName, typeof(OpenIdConnectHandler));
    }

    /// <summary>
    /// Fills in one provider's OIDC options from its registration.
    /// <para>
    /// <b>A named <i>configure</i>, not an options monitor, and the difference
    /// is the whole reason the first version did not work.</b> An earlier
    /// attempt built the options with <c>IOptionsFactory.Create</c> and then
    /// assigned <c>Authority</c>. But <c>Create</c> is what runs
    /// <c>OpenIdConnectPostConfigureOptions</c> — the thing that turns an
    /// authority into a configuration manager, a nonce cookie and a state
    /// format — so every value assigned afterwards arrived too late to be seen.
    /// The handler then failed at the challenge with "the configuration may be
    /// missing or invalid", which names neither the scheme nor the cause.
    /// </para>
    /// <para>
    /// Registered as <c>IConfigureOptions&lt;OpenIdConnectOptions&gt;</c>, it is
    /// applied <b>before</b> post-configuration, for whichever scheme name is
    /// being built. The framework's own monitor then needs no replacing.
    /// </para>
    /// <para>
    /// Nothing here is per-installation configuration beyond the row: the
    /// issuer's discovery document supplies the endpoints and the signing keys,
    /// which is the point of registering an issuer rather than four URLs.
    /// </para>
    /// </summary>
    public class FederatedOidcConfigure(IProviderRegistry registry)
        : IConfigureNamedOptions<OpenIdConnectOptions>
    {
        public void Configure(OpenIdConnectOptions options) { }

        public void Configure(string? name, OpenIdConnectOptions options)
        {
            if (name is null) return;
            if (FederatedSchemes.SlugOf(name) is not { } slug) return;
            if (registry.Find(slug) is not { } provider) return;

            options.Authority = provider.Issuer;
            options.ClientId = provider.ClientId;
            options.ClientSecret = provider.ClientSecret;

            // **The same rule the registration enforces**, stated twice because
            // two different pieces of code apply it: an issuer is https, except
            // on loopback, where a development Authentik has no certificate
            // anybody would trust.
            //
            // The default is `true`, and against an http authority it fails at
            // the challenge with a message that mentions neither TLS nor the
            // provider — so a development stack cannot sign anybody in and says
            // nothing about why.
            options.RequireHttpsMetadata =
                !(Uri.TryCreate(provider.Issuer, UriKind.Absolute, out var issuer) && issuer.IsLoopback);

            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;

            // The path the provider is told to send the browser back to. It
            // carries the slug, so two providers cannot be confused for each
            // other by a callback that arrived on a shared address.
            options.CallbackPath = FederatedSchemes.CallbackPath(provider.Slug);

            // The cookie the handler signs into while the ticket is being turned
            // into an account. Not the application cookie: what the browser ends
            // up holding is issued by `SignInManager` after the mapping has run
            // and decided whether to admit at all.
            options.SignInScheme = IdentityConstants.ExternalScheme;

            options.Scope.Clear();
            foreach (var scope in provider.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                options.Scope.Add(scope);
            }
            if (!options.Scope.Contains("openid")) options.Scope.Add("openid");

            // Kept as the provider sent them. The mapping walks a dotted path
            // through the token, and Microsoft's default map renames several
            // claims into WS-Federation URIs on the way in — which would make an
            // operator's `groups` path fail to find `groups`.
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.RoleClaimType = "roles";
        }
    }
}

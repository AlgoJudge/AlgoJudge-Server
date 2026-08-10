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

            return new AuthenticationScheme(name, provider.DisplayName, typeof(OpenIdConnectHandler));
        }
    }

    /// <summary>
    /// Builds one provider's OIDC options from its registration.
    /// <para>
    /// Nothing here is per-installation configuration: the issuer's discovery
    /// document supplies the endpoints and the signing keys, which is the point
    /// of registering an issuer rather than four URLs.
    /// </para>
    /// </summary>
    public class FederatedOidcOptions(
        IProviderRegistry registry,
        IOptionsFactory<OpenIdConnectOptions> factory
    ) : IOptionsMonitor<OpenIdConnectOptions>
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OpenIdConnectOptions> built = new();

        public OpenIdConnectOptions CurrentValue => Get(Microsoft.Extensions.Options.Options.DefaultName);

        public OpenIdConnectOptions Get(string? name)
        {
            name ??= Microsoft.Extensions.Options.Options.DefaultName;

            if (FederatedSchemes.SlugOf(name) is not { } slug || registry.Find(slug) is not { } provider)
            {
                return factory.Create(name);
            }

            return built.GetOrAdd(name, _ =>
            {
                var options = factory.Create(name);

                options.Authority = provider.Issuer;
                options.ClientId = provider.ClientId;
                options.ClientSecret = provider.ClientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;

                // The path the provider is told to send the browser back to. It
                // carries the slug, so two providers cannot be confused for each
                // other by a callback that arrived on a shared address.
                options.CallbackPath = FederatedSchemes.CallbackPath(provider.Slug);

                // The cookie the handler signs into while the ticket is being
                // turned into an account. Not the application cookie: what the
                // browser ends up holding is issued by `SignInManager` after the
                // mapping has run and decided whether to admit at all.
                options.SignInScheme = IdentityConstants.ExternalScheme;

                options.Scope.Clear();
                foreach (var scope in provider.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    options.Scope.Add(scope);
                }
                if (!options.Scope.Contains("openid")) options.Scope.Add("openid");

                // Kept as the provider sent them. The mapping walks a dotted path
                // through the token, and Microsoft's default map renames several
                // claims into WS-Federation URIs on the way in — which would make
                // an operator's `groups` path fail to find `groups`.
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                options.TokenValidationParameters.RoleClaimType = "roles";

                return options;
            });
        }

        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using AlgoJudge.Server.Lti.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>An access token for talking to a platform's services.</summary>
    public interface IPlatformTokens
    {
        Task<string> GetAsync(Platform platform, IReadOnlyList<string> scopes, CancellationToken ct);
    }

    /// <summary>
    /// The client-credentials grant, authenticated with a JWT this tool signs.
    /// <para>
    /// <b>There is no client secret anywhere in this.</b> LTI authenticates a
    /// tool by a signed assertion — the platform fetched our public key at
    /// registration and checks the signature — which is why the platform's
    /// provider row carries an empty secret and why that is honest rather than a
    /// gap.
    /// </para>
    /// <para>
    /// <b>Not reachable from a browser session</b> (§9). Grade posting is the
    /// tool acting as itself, and the only things that call this are the
    /// reconciling worker and the verifier.
    /// </para>
    /// <para>
    /// <b>A singleton holding a scope factory rather than the key service.</b>
    /// The cache is the whole point — a token per platform, not per request — but
    /// signing needs <c>IToolKeyService</c>, which is scoped because it reads the
    /// module's database. Injecting it here is a captive dependency, and the
    /// framework refuses to build the container at all: caught by
    /// <c>dotnet ef</c>, which validates the graph, rather than by a request in
    /// production.
    /// </para>
    /// </summary>
    public class PlatformTokens(
        IHttpClientFactory clients,
        IServiceScopeFactory scopes,
        TimeProvider clock
    ) : IPlatformTokens
    {
        /// <summary>
        /// Taken off the token's own lifetime, so a token is replaced before it
        /// expires rather than after a request has already been refused with it.
        /// </summary>
        private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

        private sealed record Cached(string Token, DateTimeOffset Until);

        private readonly ConcurrentDictionary<string, Cached> cache = new();

        public async Task<string> GetAsync(
            Platform platform, IReadOnlyList<string> scopes, CancellationToken ct)
        {
            var wanted = string.Join(' ', scopes.OrderBy(s => s, StringComparer.Ordinal));
            var key = platform.Id + "\n" + wanted;
            var now = clock.GetUtcNow();

            if (cache.TryGetValue(key, out var cached) && cached.Until > now)
            {
                return cached.Token;
            }

            var assertion = await AssertionAsync(platform, ct);

            var http = clients.CreateClient(nameof(PlatformTokens));
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] =
                    "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
                ["scope"] = wanted,
            });

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync(platform.AuthTokenUrl, form, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new LtiLaunchException(LtiLaunchException.PlatformUnreachable,
                    $"The platform's token endpoint at {platform.AuthTokenUrl} could not be reached");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // The body is carried because a platform refusing a grant says
                // why — wrong scope, unknown client, clock skew — and none of
                // that is guessable from a status code.
                throw new LtiLaunchException(LtiLaunchException.PlatformUnreachable,
                    $"The platform refused an access token ({(int)response.StatusCode}): {Trim(body)}");
            }

            using var document = JsonDocument.Parse(body);
            var token = document.RootElement.TryGetProperty("access_token", out var value)
                ? value.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new LtiLaunchException(LtiLaunchException.PlatformUnreachable,
                    "The platform's token response carried no access_token");
            }

            var seconds = document.RootElement.TryGetProperty("expires_in", out var expiry)
                && expiry.TryGetInt32(out var parsed) ? parsed : 3600;

            cache[key] = new Cached(token, now.AddSeconds(seconds) - Margin);
            return token;
        }

        /// <summary>
        /// The assertion that stands in for a secret: signed with the tool's key,
        /// addressed to the platform's token endpoint, and good once.
        /// </summary>
        private async Task<string> AssertionAsync(Platform platform, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var descriptor = new SecurityTokenDescriptor
            {
                // Both, and both are the client id. That is what RFC 7523 asks of
                // a client authenticating as itself, and platforms check it.
                Issuer = platform.ClientId,
                Audience = platform.AuthTokenUrl,
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = platform.ClientId,
                    // Against replay at the platform's end. Ours is single-use by
                    // being short-lived; the jti is what lets the platform make it
                    // single-use too.
                    ["jti"] = Guid.NewGuid().ToString("N"),
                },
                IssuedAt = now,
                NotBefore = now.AddMinutes(-1),
                Expires = now.AddMinutes(5),
                SigningCredentials = await CredentialsAsync(ct),
            };

            return new JsonWebTokenHandler().CreateToken(descriptor);
        }

        private async Task<SigningCredentials> CredentialsAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<IToolKeyService>()
                .CredentialsAsync(ct);
        }

        private static string Trim(string body) =>
            body.Length <= 300 ? body : body[..300] + "…";
    }

    /// <summary>The AGS scopes this tool asks for, and nothing beyond them.</summary>
    public static class AgsScopes
    {
        public const string LineItem = "https://purl.imsglobal.org/spec/lti-ags/scope/lineitem";
        public const string Score = "https://purl.imsglobal.org/spec/lti-ags/scope/score";
        public const string ResultReadOnly =
            "https://purl.imsglobal.org/spec/lti-ags/scope/result.readonly";

        /// <summary>
        /// What the module needs to do its job: manage its own columns, post
        /// scores, and read back what the platform holds so drift is detectable.
        /// </summary>
        public static readonly string[] All = [LineItem, Score, ResultReadOnly];
    }
}

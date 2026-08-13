using System.Collections.Concurrent;
using AlgoJudge.Server.Lti.Data;
using Microsoft.IdentityModel.Tokens;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>The signing keys a platform publishes, cached.</summary>
    public interface IPlatformKeys
    {
        /// <summary>
        /// The keys to validate this platform's tokens against.
        /// </summary>
        /// <param name="kid">
        /// The key id from the token's header, if it named one. A cached set that
        /// does not contain it is refetched rather than used — a platform that
        /// rotated its key must not take an outage until a cache expires.
        /// </param>
        Task<IReadOnlyCollection<SecurityKey>> GetAsync(
            Platform platform, string? kid, CancellationToken ct);
    }

    /// <summary>
    /// Fetches and caches each platform's JWKS.
    /// <para>
    /// <b>Cached because it is fetched on every launch</b>, and a launch is a
    /// person waiting through a redirect chain. Refetched on an unknown key id
    /// because the alternative is an outage the length of the cache every time a
    /// platform rotates.
    /// </para>
    /// <para>
    /// <b>And rate-limited on that path</b>, because "refetch when the kid is
    /// unknown" is an unauthenticated request that makes this Server call out:
    /// anybody who can reach the launch endpoint could otherwise aim a stream of
    /// forged tokens with random key ids at somebody else's key set. One refetch
    /// per platform per interval, and a token whose key is still unknown is
    /// refused.
    /// </para>
    /// </summary>
    public class PlatformKeys(IHttpClientFactory clients, TimeProvider clock) : IPlatformKeys
    {
        private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MinimumBetweenRefetches = TimeSpan.FromMinutes(1);

        private sealed record Entry(
            IReadOnlyCollection<SecurityKey> Keys, DateTimeOffset Taken, DateTimeOffset LastRefetch);

        private readonly ConcurrentDictionary<Guid, Entry> cache = new();

        public async Task<IReadOnlyCollection<SecurityKey>> GetAsync(
            Platform platform, string? kid, CancellationToken ct)
        {
            var now = clock.GetUtcNow();

            if (cache.TryGetValue(platform.Id, out var entry))
            {
                var fresh = now - entry.Taken < Freshness;
                var known = kid is null || entry.Keys.Any(k => k.KeyId == kid);

                if (fresh && known)
                {
                    return entry.Keys;
                }

                // Unknown key on a fresh set: a rotation, or a forged token. Both
                // look identical from here, so the refetch is allowed but bounded.
                if (fresh && now - entry.LastRefetch < MinimumBetweenRefetches)
                {
                    return entry.Keys;
                }
            }

            var keys = await FetchAsync(platform, ct);
            cache[platform.Id] = new Entry(keys, now, now);
            return keys;
        }

        private async Task<IReadOnlyCollection<SecurityKey>> FetchAsync(
            Platform platform, CancellationToken ct)
        {
            var http = clients.CreateClient(nameof(PlatformKeys));

            string body;
            try
            {
                var response = await http.GetAsync(platform.KeySetUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    throw new LtiLaunchException(LtiLaunchException.PlatformUnreachable,
                        $"The platform's key set answered {(int)response.StatusCode}");
                }
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // Named rather than swallowed: "the launch failed" with no reason
                // sends an operator looking at their Moodle configuration, which
                // is the one place the fault is not.
                throw new LtiLaunchException(LtiLaunchException.PlatformUnreachable,
                    $"The platform's key set at {platform.KeySetUrl} could not be reached");
            }

            try
            {
                return new JsonWebKeySet(body).GetSigningKeys().ToList();
            }
            catch (Exception)
            {
                throw new LtiLaunchException(LtiLaunchException.PlatformUnreachable,
                    "The platform's key set was not a readable JWKS");
            }
        }
    }
}

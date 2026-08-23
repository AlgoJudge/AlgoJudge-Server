using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// Which hops may be believed when they say who the visitor is.
    /// <para>
    /// <b>This used to trust everybody.</b> The options were configured with
    /// <c>KnownNetworks.Clear()</c> and <c>KnownProxies.Clear()</c>, which does
    /// not mean "no proxies" — it means the middleware stops checking and takes
    /// <c>X-Forwarded-For</c> from whoever sent it. A comment said an
    /// installation could pin them in configuration; nothing read configuration,
    /// so it could not.
    /// </para>
    /// <para>
    /// That was survivable while the address was only ever a log line. It stops
    /// being survivable the moment a judge is shown it and asked whether a
    /// solution came from the examination room: a participant who can reach this
    /// Server past the proxy sets their own address, and the audit then
    /// <b>exonerates</b> them. A wrong answer that reads as an alibi is worse
    /// than no answer.
    /// </para>
    /// <para>
    /// So the hops are named, and an installation that names none does not start
    /// — the same rule, for the same reason, as the one storage answers to
    /// (<c>BlobStoreRegistry</c>). Defaulting instead would mean either trusting
    /// everyone, which is where this came from, or trusting only loopback, which
    /// behind a container network silently records the proxy and looks like it is
    /// working.
    /// </para>
    /// </summary>
    public static class TrustedProxies
    {
        public const string ProxiesSetting = "Forwarded:KnownProxies";
        public const string NetworksSetting = "Forwarded:KnownNetworks";

        /// <summary>
        /// Said instead of naming a proxy, when there is not one.
        /// <para>
        /// <b>A Server reached directly is a real deployment</b> — the
        /// development stack is one — and requiring it to name a proxy it does
        /// not have would be a rule people satisfy with a lie. What the rule
        /// actually asks for is a decision, not a proxy: this is the other
        /// answer, and it is as loud as the first.
        /// </para>
        /// <para>
        /// It switches the middleware off entirely rather than leaving it
        /// running with nothing to trust. The two behave the same today; only
        /// one of them says so.
        /// </para>
        /// </summary>
        public const string NoProxy = "none";

        /// <summary>
        /// Reads both settings and applies them, or throws.
        /// <para>
        /// Each accepts a comma-separated string or a configuration array, so
        /// <c>AJ_Forwarded__KnownProxies=172.20.0.1,172.18.0.1</c> works from an
        /// environment file without the <c>__0</c>, <c>__1</c> spelling that
        /// nobody remembers.
        /// </para>
        /// </summary>
        public static void Apply(ForwardedHeadersOptions options, IConfiguration configuration)
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // **`ForwardLimit` stays at its default of one**, and that is the
            // setting that makes the rest work. nginx's usual
            // `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for`
            // *appends* the peer to whatever arrived, so a header a participant
            // wrote themselves ends up earlier in the chain and the address
            // nginx observed ends up last. Taking one hop takes the last one.
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();

            var proxies = Listed(configuration, ProxiesSetting);
            var networks = Listed(configuration, NetworksSetting);

            if (proxies.Count == 1
                && networks.Count == 0
                && proxies[0].Equals(NoProxy, StringComparison.OrdinalIgnoreCase))
            {
                // Nothing is forwarded and nothing is read: the peer on the
                // socket is the visitor, which is the truth when nothing sits
                // in front.
                options.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }

            if (proxies.Count == 0 && networks.Count == 0)
            {
                throw new InvalidOperationException(
                    "Nothing says whose word to take for a visitor's address, so this Server "
                    + "cannot tell one from a claim about one. Set Forwarded__KnownProxies to "
                    + "the address your reverse proxy reaches this Server from (several may be "
                    + $"comma-separated), or Forwarded__KnownNetworks to its network in CIDR "
                    + $"form — or to '{NoProxy}' if this Server is reached directly. With "
                    + "nginx, the proxy must send "
                    + "`proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;`. "
                    + "See docs/specs/ORIGIN_METADATA.md.");
            }

            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(
                    IPAddress.TryParse(proxy, out var address)
                        ? address
                        : throw new InvalidOperationException(
                            $"{ProxiesSetting} contains '{proxy}', which is not an IP address"));
            }

            foreach (var network in networks) options.KnownNetworks.Add(Network(network));
        }

        /// <summary>
        /// One CIDR block. Both families, because a room reachable over IPv6 and
        /// described only in IPv4 reads as somewhere else entirely.
        /// <para>
        /// <b>Qualified, because there are two types of that name.</b>
        /// <c>System.Net.IPNetwork</c> arrived in .NET 8 and is the one anybody
        /// would reach for; <c>KnownNetworks</c> on this target takes the older
        /// <c>Microsoft.AspNetCore.HttpOverrides</c> one, and an unqualified name
        /// does not compile.
        /// </para>
        /// </summary>
        private static Microsoft.AspNetCore.HttpOverrides.IPNetwork Network(string declared)
        {
            var slash = declared.LastIndexOf('/');
            if (slash > 0
                && IPAddress.TryParse(declared[..slash], out var prefix)
                && int.TryParse(declared[(slash + 1)..], out var length))
            {
                return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length);
            }

            throw new InvalidOperationException(
                $"{NetworksSetting} contains '{declared}', which is not a network in CIDR form "
                + "such as 172.20.0.0/16 or 2001:db8::/32");
        }

        /// <summary>
        /// A configuration array, or one string with commas in it.
        /// </summary>
        private static IReadOnlyList<string> Listed(IConfiguration configuration, string key)
        {
            var section = configuration.GetSection(key);

            var children = section.GetChildren()
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .ToList();
            if (children.Count > 0) return children;

            return section.Value is { Length: > 0 } scalar
                ? scalar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }
    }
}

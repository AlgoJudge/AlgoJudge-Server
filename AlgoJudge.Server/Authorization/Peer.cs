using System.Net;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// Who is really on the other end of the socket.
    /// <para>
    /// **This exists because `UseForwardedHeaders` runs first** (`Program.cs`),
    /// and it rewrites <c>Connection.RemoteIpAddress</c> from
    /// <c>X-Forwarded-For</c> so that logs and rate limits see the visitor
    /// rather than the proxy. That is right for everything else and wrong for
    /// exactly one question: *did this request come from inside the container?*
    /// </para>
    /// <para>
    /// Left to the rewritten value, a request arriving from the far side of the
    /// internet with <c>X-Forwarded-For: 127.0.0.1</c> would look local, and the
    /// maintenance switch would be a switch anybody could throw. So the real
    /// address is captured **before** the rewrite and read from there.
    /// </para>
    /// </summary>
    public static class Peer
    {
        private const string Key = "aj.peer";

        /// <summary>
        /// Records the address the socket actually came from. **Must be
        /// registered before <c>UseForwardedHeaders</c>**; anywhere later and it
        /// records the proxy's claim instead of the truth.
        /// </summary>
        public static IApplicationBuilder UseTruePeerAddress(this IApplicationBuilder app) =>
            app.Use(async (context, next) =>
            {
                context.Items[Key] = context.Connection.RemoteIpAddress;
                await next();
            });

        /// <summary>
        /// Whether the caller is on this machine.
        /// <para>
        /// **Absent means no**, deliberately. If the stamping middleware were
        /// removed or reordered, this would otherwise fall back to trusting a
        /// header — the failure would open the switch rather than close it, and
        /// that is the wrong direction for the one thing here that takes the
        /// Server off the air.
        /// </para>
        /// </summary>
        public static bool IsLoopback(HttpContext context) =>
            context.Items.TryGetValue(Key, out var stamped)
            && stamped is IPAddress address
            && IPAddress.IsLoopback(address);
    }
}

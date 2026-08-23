using System.Security.Claims;
using AlgoJudge.Server.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Realtime
{
    /// <summary>
    /// Records that somebody is using the installation, so the users screen has
    /// something to read.
    /// <para>
    /// A session is neither a person nor a browser tab. This writes the durable
    /// half — when it started, what it last asked for, from where — while the
    /// number of open sockets is <b>counted live</b> from the connection
    /// registry: a count written to a row survives a crash and then tells the
    /// screen somebody is present who left hours ago.
    /// </para>
    /// <para>
    /// One write per request would double the database traffic of a screen that
    /// polls, for a field measured in minutes. It is throttled: a session is
    /// touched at most once a minute, and the cost of that is a `lastRequestAt`
    /// up to a minute stale — which is what "last seen" means anyway.
    /// </para>
    /// </summary>
    public class SessionTrackingMiddleware(RequestDelegate next)
    {
        private static readonly TimeSpan Throttle = TimeSpan.FromMinutes(1);

        /// <summary>
        /// When each session was last written. In memory on purpose: losing it
        /// on a restart costs one extra write per session.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> Touched = new();

        /// <summary>
        /// Carries the session's id, so one is minted per browser and kept while
        /// it is alive. Two browsers are two sessions; two tabs are one.
        /// <para>
        /// Public because <see cref="Services.RequestOrigin"/> reads it, and the
        /// half that writes a cookie should be the half that names it. It said
        /// a session was minted "per (user, user agent, address)" until
        /// 2026-08-23, which no code here has ever done: the cookie is the whole
        /// key.
        /// </para>
        /// </summary>
        public const string SessionCookie = "aj_session";

        public async Task InvokeAsync(
            HttpContext http, ApplicationDbContext context, TimeProvider clock,
            Services.IRequestOrigin origin, IConfiguration configuration)
        {
            await next(http);

            var userId = http.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return;
            // Only successful requests: a 401 storm from an expired cookie is
            // not somebody using the product.
            if (http.Response.StatusCode >= 400) return;

            try
            {
                await TouchAsync(http, context, clock, origin, configuration, userId);
            }
            catch (Exception)
            {
                // Bookkeeping. A failure here must never turn a request that
                // already succeeded into one that did not — the response has
                // been written by this point anyway.
            }
        }

        private static async Task TouchAsync(
            HttpContext http, ApplicationDbContext context, TimeProvider clock,
            Services.IRequestOrigin origin, IConfiguration configuration, string userId)
        {
            var now = clock.GetUtcNow();

            var sessionId = origin.SessionId;

            if (sessionId is { } known
                && Touched.TryGetValue(known, out var last)
                && now - last < Throttle)
            {
                return;
            }

            var session = sessionId is { } id
                ? await context.UserSessions.FirstOrDefaultAsync(s => s.Id == id && s.EndedAt == null)
                : null;

            if (session is null)
            {
                session = new Database.Models.UserSession
                {
                    UserId = userId,
                    StartedAt = now.UtcDateTime,
                    // Through `IRequestOrigin`, which un-maps an IPv4-mapped
                    // IPv6 address. Straight off the connection it arrives mapped
                    // and stops matching any IPv4 network anybody writes down.
                    IpAddress = origin.Address,
                    UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 512),
                };
                context.UserSessions.Add(session);

                var cookie = new CookieOptions
                {
                    HttpOnly = true,
                    // Same site as the cookie that authenticates the request, so
                    // this adds no cross-site surface of its own.
                    SameSite = SameSiteMode.Lax,
                    Secure = http.Request.IsHttps,
                    IsEssential = true,
                    Expires = now.AddDays(30),
                };

                // **Which is why it has to follow that cookie into a frame too.**
                // This one is written on a later request than the sign-in, so
                // the answer is read back off the authentication ticket rather
                // than remembered — and a session left on `Lax` here would be
                // dropped by the browser while the identity cookie survived,
                // leaving somebody signed in and untracked.
                if (await Authorization.EmbeddedSessions.IsEmbeddedAsync(
                        http, IdentityConstants.ApplicationScheme))
                {
                    Authorization.EmbeddedSessions.Widen(cookie);
                }

                http.Response.Cookies.Append(SessionCookie, session.Id.ToString(), cookie);
            }

            session.LastRequestAt = now.UtcDateTime;
            // **Pushed out on every touch, not fixed at creation.** The window
            // is "thirty days since this browser was last here", which is what
            // the cookie means, so a session in daily use never expires and one
            // abandoned in June is swept in July. `AddressSweeper` is what acts
            // on it; the column existed and was never set until 2026-08-23.
            session.ExpiresAt = now.UtcDateTime.AddDays(
                configuration.GetValue("Retention:SessionOriginDays", 30));
            // The path, not the screen: the Server does not know what somebody
            // was looking at, and guessing would be inventing.
            session.LastRequestPath = Truncate(http.Request.Path.Value, 256);

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user is not null) user.LastSeenAt = now.UtcDateTime;

            await context.SaveChangesAsync();
            Touched[session.Id] = now;
        }

        private static string? Truncate(string? value, int limit) =>
            string.IsNullOrEmpty(value) ? null : value.Length <= limit ? value : value[..limit];
    }
}

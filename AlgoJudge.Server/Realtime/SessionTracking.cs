using System.Security.Claims;
using AlgoJudge.Server.Database;
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
        /// The cookie carries no session id of its own, so one is minted per
        /// (user, user agent, address) and kept while it is alive. Two browsers
        /// are two sessions; two tabs are one.
        /// </summary>
        private const string SessionCookie = "aj_session";

        public async Task InvokeAsync(
            HttpContext http, ApplicationDbContext context, TimeProvider clock)
        {
            await next(http);

            var userId = http.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return;
            // Only successful requests: a 401 storm from an expired cookie is
            // not somebody using the product.
            if (http.Response.StatusCode >= 400) return;

            try
            {
                await TouchAsync(http, context, clock, userId);
            }
            catch (Exception)
            {
                // Bookkeeping. A failure here must never turn a request that
                // already succeeded into one that did not — the response has
                // been written by this point anyway.
            }
        }

        private static async Task TouchAsync(
            HttpContext http, ApplicationDbContext context, TimeProvider clock, string userId)
        {
            var now = clock.GetUtcNow();

            Guid? sessionId = http.Request.Cookies.TryGetValue(SessionCookie, out var raw)
                && Guid.TryParse(raw, out var parsed) ? parsed : null;

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
                    IpAddress = http.Connection.RemoteIpAddress?.ToString(),
                    UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 512),
                };
                context.UserSessions.Add(session);

                http.Response.Cookies.Append(SessionCookie, session.Id.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    // Same site as the cookie that authenticates the request, so
                    // this adds no cross-site surface of its own.
                    SameSite = SameSiteMode.Lax,
                    Secure = http.Request.IsHttps,
                    IsEssential = true,
                    Expires = now.AddDays(30),
                });
            }

            session.LastRequestAt = now.UtcDateTime;
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

using Microsoft.AspNetCore.Authentication;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// A session established while this application was displayed inside somebody
    /// else's page.
    ///
    /// <para>
    /// <b>Why the core knows about this at all.</b> A cookie set with
    /// <c>SameSite=Lax</c> — which is what every ordinary sign-in here uses, and
    /// should keep using — is not stored at all when the response comes from a
    /// frame on another site. The sign-in then appears to succeed and every
    /// request after it is anonymous. Measured in Chrome 141 on 2026-08-14: both
    /// cookies refused, in the browser's own words, as <c>SameSiteLax</c>.
    /// </para>
    ///
    /// <para>
    /// <b>It names no integration, and that is deliberate.</b> Being embedded is
    /// a property of how a session was established, not of who embedded us; an
    /// LMS is only the first caller. Nothing here mentions one, so nothing here
    /// has to be removed when one goes away.
    /// </para>
    ///
    /// <para>
    /// <b>The ordinary sign-in is untouched.</b> Widening every session would
    /// give up the protection <c>Lax</c> buys against a cross-site request
    /// riding somebody's cookie, everywhere, to fix a case that only arises in a
    /// frame. This applies to the sessions that ask for it and no others.
    /// </para>
    /// </summary>
    public static class EmbeddedSessions
    {
        /// <summary>
        /// The mark on the authentication ticket. It travels in the cookie, so a
        /// later request can still tell what kind of session it is holding —
        /// which is what the session cookie below needs to know, and it is
        /// written on a different request from the sign-in.
        /// </summary>
        public const string Item = "aj:embedded";

        /// <summary>Properties for a sign-in that will live inside a frame.</summary>
        public static AuthenticationProperties Properties(bool embedded, bool isPersistent)
        {
            var properties = new AuthenticationProperties { IsPersistent = isPersistent };
            if (embedded) properties.Items[Item] = "true";
            return properties;
        }

        public static bool IsEmbedded(AuthenticationProperties? properties) =>
            properties?.Items.TryGetValue(Item, out var value) == true && value == "true";

        /// <summary>
        /// Remembers it for the rest of <b>this</b> request.
        ///
        /// <para>
        /// The ticket answers the question on every later request, but not on the
        /// one that signs somebody in: it is read from the request's own cookie,
        /// and on that request there is not one yet. Anything else written in the
        /// same response — the session cookie below is the case that exists —
        /// would be widened one request too late, and a browser refusing it means
        /// a fresh session row for every request that follows.
        /// </para>
        /// </summary>
        public static void Mark(HttpContext http) => http.Items[Item] = true;

        /// <summary>
        /// Whether this request belongs to an embedded session — asked of the
        /// sign-in happening now, then of the ticket the browser presented.
        /// </summary>
        public static async Task<bool> IsEmbeddedAsync(HttpContext http, string scheme)
        {
            if (http.Items.TryGetValue(Item, out var marked) && marked is true) return true;
            var ticket = await http.AuthenticateAsync(scheme);
            return IsEmbedded(ticket.Properties);
        }

        /// <summary>
        /// Widens one cookie so a browser will keep it in a third-party context.
        ///
        /// <para>
        /// <c>Partitioned</c> is the point rather than an extra: it keys the
        /// cookie to the site that did the embedding, so a session opened inside
        /// one course is not the same session as a visit to this application
        /// directly, and two courses do not share one. Without it the browser is
        /// asked to keep an ordinary third-party cookie, which is the thing
        /// browsers are in the middle of refusing outright.
        /// </para>
        ///
        /// <para>
        /// <c>Secure</c> is not optional: <c>SameSite=None</c> without it is
        /// rejected by every current browser, so an installation that serves
        /// plain HTTP cannot have embedded sessions at all. That is a property
        /// of the web, not a limitation to work around.
        /// </para>
        /// </summary>
        public static void Widen(CookieOptions cookie)
        {
            cookie.SameSite = SameSiteMode.None;
            cookie.Secure = true;
            // .NET 8 has no `Partitioned` property; the attribute is appended
            // verbatim. Guarded because this runs for every cookie of an
            // embedded session and appending twice would emit it twice.
            if (!cookie.Extensions.Contains("Partitioned"))
            {
                cookie.Extensions.Add("Partitioned");
            }
        }
    }
}

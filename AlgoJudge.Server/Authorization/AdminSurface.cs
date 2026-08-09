using System.Security.Cryptography;
using System.Text;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// Who may reach <c>/admin/**</c> at all.
    /// <para>
    /// <b>Two things, and both are required.</b> The caller must be on this
    /// machine's loopback interface, and must present the configured token. The
    /// first alone was the whole authorization of the maintenance switch, and it
    /// is not enough on its own: anything that gets a foothold inside the
    /// container — including the Server's own process — is already on loopback.
    /// The token is what a stolen foothold does not come with.
    /// </para>
    /// <para>
    /// <b>Not a permission</b>, for the reason the switch was never one: this is
    /// an operator's act rather than a role somebody can be granted, and the
    /// moment it is a permission it is also something a stolen administrator
    /// session can do.
    /// </para>
    /// <para>
    /// <b>Middleware, not a filter</b>, as with <see cref="IdentitySurface"/>: a
    /// filter runs after the body has been bound, and a request that may not be
    /// here should be turned away in front of the endpoint rather than after the
    /// Server has read it.
    /// </para>
    /// </summary>
    public static class AdminSurface
    {
        private const string Group = "/admin";

        /// <summary>
        /// A header rather than a query parameter, deliberately.
        /// <para>
        /// This is a long-lived secret. A query string is written into proxy
        /// access logs, into shell history and into whatever an operator pastes
        /// into a ticket; a header is written into none of those by default. The
        /// flags on <c>/admin/maintenance</c> stay in the query because they are
        /// not secret and the shipped image has no HTTP client to send a body
        /// with.
        /// </para>
        /// </summary>
        public const string TokenHeader = "X-AlgoJudge-Admin-Token";

        /// <summary><c>AJ_Admin__Token</c>, by the prefix the Server already reads.</summary>
        public const string TokenSetting = "Admin:Token";

        /// <summary>
        /// The token a development stack ships with.
        /// <para>
        /// Well known on purpose, and named for what it is, exactly as
        /// <c>admin-development-only</c> beside it: a development stack that
        /// cannot take itself off the air teaches nobody anything, and CI would
        /// otherwise need a secret to run one test. The Server says so at
        /// <b>Warning</b> on every start where this value is in force outside
        /// Development.
        /// </para>
        /// </summary>
        public const string DevelopmentToken = "admin-token-development-only";

        public static IApplicationBuilder UseAdminSurfaceRules(this IApplicationBuilder app) =>
            app.Use(async (context, next) =>
            {
                // Seen **after** `UsePathBase`, so `/api/v1` is already gone.
                var path = context.Request.Path.Value ?? "";

                if (!Is(path, Group))
                {
                    await next();
                    return;
                }

                // **One answer for every refusal**, and it is 404.
                //
                // Wrong machine, no token configured, no header, wrong value —
                // all the same. A 403 would confirm that the endpoint is there
                // and that the caller got one of the two halves right, which is
                // exactly the feedback somebody probing for it wants. To
                // anything that is not entitled to be here, this surface does
                // not exist.
                if (!Peer.IsLoopback(context)) throw new NotFoundException("Endpoint");

                var configured = context.RequestServices
                    .GetRequiredService<IConfiguration>()[TokenSetting];

                // Absent, empty or whitespace closes the whole group. There is
                // no "no token means no check" reading of this — the failure has
                // to shut the door rather than open it.
                if (string.IsNullOrWhiteSpace(configured)) throw new NotFoundException("Endpoint");

                var presented = context.Request.Headers[TokenHeader].ToString();
                if (!Matches(presented, configured)) throw new NotFoundException("Endpoint");

                await next();
            });

        /// <summary>
        /// Constant time, so the comparison itself says nothing.
        /// <para>
        /// The length is compared first and in the open, which leaks only how
        /// long the token is — and a caller on loopback probing byte by byte is
        /// already on the machine. There is no lockout and no rate limit here
        /// for that reason, and because a lockout on this endpoint would be a
        /// way to close the last door on the operator.
        /// </para>
        /// </summary>
        private static bool Matches(string presented, string configured)
        {
            var a = Encoding.UTF8.GetBytes(presented);
            var b = Encoding.UTF8.GetBytes(configured);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static bool Is(string path, string prefix) =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }
}

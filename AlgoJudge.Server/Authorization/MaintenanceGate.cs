using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// Refuses what the current maintenance level refuses.
    /// <para>
    /// <b>Middleware, not a filter</b>, for the same reason as
    /// <see cref="IdentitySurface"/>: a filter runs after the body has been
    /// bound, and a request that arrives during a window should be turned away
    /// in front of the endpoint rather than after the Server has read a hundred
    /// megabytes of it.
    /// </para>
    /// <para>
    /// It reads the row on every guarded request. That is a round trip, and it
    /// is the point: an operator throwing the switch expects the next request to
    /// feel it, and a cache would mean "the next request, or the one after,
    /// depending".
    /// </para>
    /// </summary>
    public static class MaintenanceGate
    {
        /// <summary>
        /// The one path that answers at every level.
        /// <para>
        /// Not a courtesy. It is what the Client and the Runner poll to learn
        /// they may come back, so closing it would make the window
        /// unescapable — and the container's own healthcheck reads it, so a
        /// Server that failed it during a planned window would be killed by the
        /// thing meant to keep it alive.
        /// </para>
        /// </summary>
        private const string Health = "/health";

        /// <summary>
        /// The operator's own surface, which has to be reachable to be thrown
        /// off again.
        /// <para>
        /// <b>The whole group, not only the switch.</b> Resetting the
        /// administrator's password is exactly the thing somebody needs while
        /// the Server is closed — a window they cannot escape and an account
        /// they cannot get into are the same lockout. What guards this is
        /// <see cref="AdminSurface"/>, which runs before this and needs
        /// loopback and a token.
        /// </para>
        /// </summary>
        private const string Switch = "/admin";

        /// <summary>
        /// What a Runner may still do while the Server drains.
        /// <para>
        /// Everything here is about **finishing work already claimed**: renewing
        /// the lease that proves it is still alive, uploading the log and the
        /// per-test table, and reporting the verdict. Reading a file is on the
        /// list because a job claimed a second before the switch was thrown has
        /// not downloaded its package yet.
        /// </para>
        /// <para>
        /// Claiming is deliberately absent, and is not refused here either: the
        /// two claim endpoints answer <b>204</b> instead, which every Runner
        /// already treats as an ordinary empty queue. A 503 there would be
        /// correct and noisier for no gain.
        /// </para>
        /// </summary>
        private static readonly string[] DrainingAllows =
        [
            "/runner/jobs/",
            "/runner/trials/",
            "/runner/files",
            "/runner/heartbeat",
            "/runner/auth/",
        ];

        public static IApplicationBuilder UseMaintenanceGate(this IApplicationBuilder app) =>
            app.Use(async (context, next) =>
            {
                // Paths here are seen **after** `UsePathBase`, so `/api/v1` is
                // already gone. Matching against the full prefix would match
                // nothing and open the gate.
                var path = context.Request.Path.Value ?? "";

                if (Is(path, Health) || Is(path, Switch))
                {
                    await next();
                    return;
                }

                var maintenance = context.RequestServices.GetRequiredService<IMaintenanceService>();
                var state = await maintenance.StateAsync(context.RequestAborted);

                if (state.Level == MaintenanceLevel.Open)
                {
                    await next();
                    return;
                }

                if (state.Level == MaintenanceLevel.Draining && Allowed(path))
                {
                    await next();
                    return;
                }

                throw new ServiceUnavailableException(
                    state.Reason is { Length: > 0 } reason
                        ? $"The Server is under maintenance: {reason}"
                        : "The Server is under maintenance");
            });

        private static bool Is(string path, string prefix) =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

        private static bool Allowed(string path) =>
            DrainingAllows.Any(allowed => path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
    }
}

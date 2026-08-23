using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Workers
{
    /// <summary>
    /// Takes the addresses back out again.
    /// <para>
    /// <b>There was an index waiting for this worker and no worker.</b>
    /// <c>UserSession.ExpiresAt</c> has existed since the schema was written, is
    /// indexed, and carried a comment saying a reaper closed expired sessions —
    /// nothing ever set the column and nothing ever swept it. So every address
    /// anybody had ever connected from was kept for ever, and
    /// <c>SessionsAsync</c> answers only for sessions that have not ended, which
    /// left the rest with no reader at all: cost and risk, no use.
    /// </para>
    /// <para>
    /// <b>The row survives and the personal data does not.</b> Deleting it
    /// outright would take "when did this person sign in, and how often" with
    /// it, which is a fair question to ask of an account under dispute. What
    /// goes is the address and the user agent — the two fields that describe a
    /// person rather than an event.
    /// </para>
    /// <para>
    /// Thirty days by default, from last activity, because that is exactly how
    /// long the <c>aj_session</c> cookie lives: the data lasts as long as the
    /// credential that produced it and not a day longer.
    /// </para>
    /// </summary>
    public class AddressSweeper(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IConfiguration configuration,
        ILogger<AddressSweeper> logger
    ) : BackgroundService
    {
        /// <summary>
        /// Hourly. The window is measured in days, so the cost of checking is one
        /// indexed query against rows that are nearly always in date, and the
        /// cost of a longer interval is a day's data outliving its window by an
        /// arbitrary fraction.
        /// </summary>
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            using var timer = new PeriodicTimer(Interval, clock);

            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await SweepSessionsAsync(stopping);
                    await SweepSubmissionsAsync(stopping);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A failed sweep is not a failed process. The next one runs
                    // in an hour, and a worker that took the host down with it
                    // would turn a database blip into an outage.
                    logger.LogError(e, "Address sweep failed");
                }

                try
                {
                    await timer.WaitForNextTickAsync(stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Clears what describes a person from sessions past their window, and
        /// closes them if nobody has.
        /// </summary>
        internal async Task<int> SweepSessionsAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var now = clock.GetUtcNow().UtcDateTime;

            // **`IpAddress != null || UserAgent != null` and not the expiry
            // alone**, so a session swept once is not read again on every pass
            // for the rest of its life. The row stays for ever; the work does
            // not repeat.
            var stale = await context.UserSessions
                .Where(s => s.ExpiresAt != null
                    && s.ExpiresAt <= now
                    && (s.IpAddress != null || s.UserAgent != null))
                .ToListAsync(ct);

            if (stale.Count == 0) return 0;

            foreach (var session in stale)
            {
                session.IpAddress = null;
                session.UserAgent = null;
                // Ended if nobody ended it: a session past its expiry is not
                // open, whatever the row says, and the users screen counts open
                // ones.
                session.EndedAt ??= session.ExpiresAt;
            }

            await context.SaveChangesAsync(ct);
            logger.LogInformation(
                "Cleared the origin of {Count} sessions past their window", stale.Count);
            return stale.Count;
        }

        /// <summary>
        /// The same, for submissions, on a window of its own.
        /// <para>
        /// <b>A year, and independent of the activity's life.</b> A submission's
        /// address is evidence in a contest rather than an operational record,
        /// so it outlives the session that produced it — a complaint about a
        /// result can arrive long after the activity is archived. It does not
        /// outlive it indefinitely, which is what the window is for.
        /// </para>
        /// <para>
        /// <c>SessionId</c> stays. Once the session's own fields have been swept
        /// it names nothing about a person; what it still answers is "these
        /// submissions came from one browser session", which is the shape of the
        /// question a judge asks and costs nothing to keep.
        /// </para>
        /// </summary>
        internal async Task<int> SweepSubmissionsAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var days = configuration.GetValue("Retention:SubmissionOriginDays", 365);
            var before = clock.GetUtcNow().UtcDateTime.AddDays(-days);

            var stale = await context.Submissions
                .Where(s => s.CreatedDate <= before
                    && (s.IpAddress != null || s.DeviceId != null))
                .ToListAsync(ct);

            if (stale.Count == 0) return 0;

            foreach (var submission in stale)
            {
                submission.IpAddress = null;
                submission.DeviceId = null;
            }

            await context.SaveChangesAsync(ct);
            logger.LogInformation(
                "Cleared the origin of {Count} submissions past their window", stale.Count);
            return stale.Count;
        }
    }
}

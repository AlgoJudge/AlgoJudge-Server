using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Workers
{
    /// <summary>
    /// Moves the gradebook towards what AlgoJudge holds, and keeps moving.
    /// <para>
    /// <b>A reconciler, not a sender</b> (§6.4). At least six things detach a
    /// gradebook from the truth — a released freeze, a platform that was down, a
    /// rejudge, a changed maximum, an account linked late, a roster member with
    /// no account — and every one of them becomes the same act here: the row says
    /// what should be true, and this makes it true. Six code paths that each have
    /// to remember to post is the arrangement this replaces.
    /// </para>
    /// <para>
    /// It is the shape the evaluation lease already uses in this Server, which is
    /// why it should look familiar rather than clever.
    /// </para>
    /// </summary>
    public class GradeSyncWorker(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<GradeSyncWorker> logger
    ) : BackgroundService
    {
        /// <summary>
        /// How often the sweep runs. A gradebook does not notice a minute, and
        /// the alternative — waking every few seconds to find nothing — is a
        /// database query per instance per tick, for ever.
        /// </summary>
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long a grade may keep failing before it is reported as failed
        /// rather than as still coming.
        /// <para>
        /// A day, reached by doubling. §13 #5 left the ceiling open; this is the
        /// answer, and the reason is what the two states mean to a teacher: a
        /// grade that is "pending" is one they are waiting for, and one that will
        /// never arrive has to stop looking like that. Nothing is lost by
        /// failing — the row keeps its intent, and any change to it, or a manual
        /// resync, starts the attempts again.
        /// </para>
        /// </summary>
        private const int MaximumAttempts = 12;

        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stopping);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A sweep that throws must not take the worker down with it:
                    // the next one may well succeed, and a dead worker is a
                    // gradebook that silently stops moving.
                    logger.LogError(e, "The grade sweep failed");
                }

                try
                {
                    await Task.Delay(Interval, clock, stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// One sweep. <c>internal</c> so a test can run it against a clock
        /// somebody turns rather than wait a minute for it.
        /// </summary>
        internal async Task<int> RunOnceAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();
            var sync = scope.ServiceProvider.GetRequiredService<IGradeSyncService>();
            var ags = scope.ServiceProvider.GetRequiredService<IAgsClient>();
            var core = scope.ServiceProvider.GetRequiredService<Database.ApplicationDbContext>();

            var links = await db.ResourceLinks.AsNoTracking().ToListAsync(ct);
            foreach (var link in links)
            {
                await sync.RefreshAsync(link, ct);
            }

            var now = clock.GetUtcNow().UtcDateTime;

            var due = await db.GradeSyncStates
                .Where(s => s.State == GradeSyncStatus.Pending
                    && (s.NextAttemptAt == null || s.NextAttemptAt <= now))
                .OrderBy(s => s.UpdatedAt)
                // Bounded, so one course with a thousand people cannot hold the
                // sweep for minutes while every other course waits.
                .Take(200)
                .ToListAsync(ct);

            var posted = 0;
            foreach (var state in due)
            {
                if (await PostAsync(db, core, ags, state, ct))
                {
                    posted++;
                }
            }

            await db.SaveChangesAsync(ct);
            return posted;
        }

        private async Task<bool> PostAsync(
            LtiDbContext db, Database.ApplicationDbContext core,
            IAgsClient ags, GradeSyncState state, CancellationToken ct)
        {
            var item = await db.LineItems.FirstOrDefaultAsync(i => i.Id == state.LineItemId, ct);
            var link = item is null
                ? null
                : await db.ResourceLinks.FirstOrDefaultAsync(l => l.Id == item.ResourceLinkId, ct);
            var platform = link is null
                ? null
                : await db.Platforms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == link.PlatformId, ct);
            var subject = await db.ExternalIdentities.AsNoTracking()
                .Where(i => i.PlatformId == (platform == null ? Guid.Empty : platform.Id)
                    && i.UserId == state.UserId)
                .Select(i => i.Subject)
                .FirstOrDefaultAsync(ct);

            if (item is null || link is null || platform is null || subject is null)
            {
                return Failed(state, "the placement, platform or link this grade belongs to is gone");
            }

            if (!platform.Enabled)
            {
                // Not a failure and not an attempt: the operator switched it off,
                // and the grade waits for them to switch it back on.
                return false;
            }

            if (string.IsNullOrWhiteSpace(link.AgsLineItemsUrl))
            {
                return Failed(state,
                    "the platform offered no gradebook for this placement — the tool may be "
                    + "registered without the grade services");
            }

            try
            {
                if (string.IsNullOrWhiteSpace(item.PlatformUrl))
                {
                    item.PlatformUrl = await ags.EnsureLineItemAsync(
                        platform, link.AgsLineItemsUrl, link.PlatformResourceLinkId,
                        item.SeriesProblemId.ToString("D"),
                        await LabelAsync(core, item, ct),
                        item.ScoreMaximum, ct);
                }

                // **Monotonic, per person per column, and this is the whole
                // reason the column exists.** A platform refuses a score whose
                // timestamp is not newer than the one it holds, so a retry that
                // reuses the original result's time achieves nothing.
                //
                // <b>How loudly it refuses depends on the platform.</b> Measured
                // 2026-08-14 against Moodle 4.5.13, 5.2.2 and 5.3dev, all three
                // identical: `scores.php` throws <b>409</b> with "Refusing score
                // with an earlier timestamp", which lands in `LastError` and on
                // the manager's screen. §6.4 of `LMS_INTEGRATION.md` states the
                // refusal as silent — a success that changes nothing — and that
                // is not true of Moodle. It may be true of something else, which
                // is why the rule below holds either way.
                // **Truncated to the precision the database keeps**, before
                // anything is compared or sent. PostgreSQL stores a timestamp to
                // the microsecond, so a value written at .NET's 100-nanosecond
                // resolution comes back a few ticks earlier than it went in —
                // and the comparison below then measures a truncated `last`
                // against an untruncated `now`, decides nothing needs bumping,
                // and posts a timestamp the platform has already seen. Accepted,
                // ignored, and reported as synchronised.
                //
                // Found by a test with the clock stopped. With a real clock the
                // next sweep is a minute later and the fault never shows, which
                // is exactly the kind of thing that surfaces in a lab a year on.
                // **Compared at the receiver's resolution, not at ours.** Moodle
                // reads the timestamp with `strtotime`, which resolves to whole
                // seconds — so two posts inside one second are the same instant
                // to it, however different they look here. Comparing more finely
                // than the platform does is the same mistake as comparing more
                // finely than the database keeps, one scale up.
                var stamp = Microseconds(clock.GetUtcNow().UtcDateTime);
                if (state.LastTimestamp is { } last && Second(stamp) <= Second(last))
                {
                    // **A whole second, not a millisecond**, and that is measured
                    // rather than cautious. Moodle compares with `strtotime`,
                    // which resolves to seconds — so two posts inside one second
                    // carry the same instant as far as it is concerned, and the
                    // second is refused. A millisecond here would produce a 409,
                    // a backoff, and a grade that eventually lands looking as
                    // though something had gone wrong.
                    stamp = Microseconds(last).AddSeconds(1);
                }

                // **The limit of this, stated rather than discovered.** The rule
                // above keeps *our* timestamps rising, which is what a retry
                // needs. It cannot beat a timestamp the *platform* holds that is
                // ahead of our clock — an edit made on a machine running fast, or
                // real skew between two servers. AGS offers no way to read the
                // stored timestamp back: a result carries a score and no time. So
                // the post is accepted, changes nothing, and the verifier keeps
                // reporting drift that a resync appears not to fix.
                //
                // Not worked around here, because every workaround is a lie about
                // time: stamping into the future to win would poison every later
                // post for the same person. If it is ever seen in the field, the
                // fix is on the clocks.

                await ags.PostScoreAsync(
                    platform, item.PlatformUrl, subject,
                    state.DesiredScore, item.ScoreMaximum, stamp, graded: true, ct);

                state.PostedScore = state.DesiredScore;
                state.PostedAt = stamp;
                state.LastTimestamp = stamp;
                state.State = GradeSyncStatus.Synchronised;
                state.Attempts = 0;
                state.NextAttemptAt = null;
                state.LastError = null;
                state.UpdatedAt = stamp;
                return true;
            }
            catch (Exception e) when (e is AgsException or LtiLaunchException)
            {
                return Failed(state, e.Message);
            }
        }

        /// <summary>
        /// Records a failure and decides when to try again — doubling, and giving
        /// up loudly rather than quietly.
        /// </summary>
        private bool Failed(GradeSyncState state, string why)
        {
            state.Attempts++;
            state.LastError = why;
            state.UpdatedAt = clock.GetUtcNow().UtcDateTime;

            if (state.Attempts >= MaximumAttempts)
            {
                state.State = GradeSyncStatus.Failed;
                state.NextAttemptAt = null;
                logger.LogWarning(
                    "Grade for {User} in line item {Item} failed after {Attempts} attempts: {Why}",
                    state.UserId, state.LineItemId, state.Attempts, why);
            }
            else
            {
                var backoff = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(state.Attempts, 10)));
                state.NextAttemptAt = state.UpdatedAt + backoff;
            }

            return false;
        }

        /// <summary>
        /// What the column is called in the teacher's gradebook.
        /// <para>
        /// The assignment's own name, then the problem's, then its slug — because
        /// that is what somebody typed and what they will look for. A column
        /// named after an identifier is a column nobody can find, and a gradebook
        /// full of them is worse than no integration.
        /// </para>
        /// </summary>
        private static async Task<string> LabelAsync(
            Database.ApplicationDbContext core, LineItem item, CancellationToken ct)
        {
            var named = await core.SeriesProblems.AsNoTracking()
                .Where(sp => sp.Id == item.SeriesProblemId)
                .Select(sp => new { sp.Name, sp.Slug, ProblemName = sp.Problem!.Name })
                .FirstOrDefaultAsync(ct);

            if (named is null)
            {
                return "AlgoJudge";
            }

            return Pick(named.Name) ?? Pick(named.ProblemName) ?? named.Slug;
        }

        private static string? Pick(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>
        /// The same instant, at the resolution this Server's database keeps.
        /// Comparing two timestamps only means something when both have been
        /// through the same rounding.
        /// </summary>
        private static DateTime Microseconds(DateTime value) =>
            new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);

        /// <summary>
        /// The same instant, at the resolution a platform compares at. One
        /// second, measured against Moodle rather than assumed.
        /// </summary>
        private static long Second(DateTime value) => value.Ticks / TimeSpan.TicksPerSecond;
    }
}

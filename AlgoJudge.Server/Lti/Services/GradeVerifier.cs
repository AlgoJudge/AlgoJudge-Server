using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>
    /// How one placement's grades stand, as a manager reads it.
    /// </summary>
    public record GradeSummaryDto
    {
        public required int Total { get; init; }
        public required int Synchronised { get; init; }
        public required int Pending { get; init; }

        /// <summary>Held back until a freeze lifts. Not a failure (§6.3).</summary>
        public required int Deferred { get; init; }

        /// <summary>Never posted, because the activity withholds scores.</summary>
        public required int Withheld { get; init; }

        public required int Failed { get; init; }

        /// <summary>
        /// Grades the platform holds that disagree with what was posted, counted
        /// only when the platform was actually asked. Null means it was not.
        /// </summary>
        public int? Drifted { get; init; }

        /// <summary>
        /// The most recent thing a platform said no to, so a count has a reason
        /// beside it instead of sending somebody to a log.
        /// </summary>
        public string? LastError { get; init; }
    }

    public interface IGradeVerifier
    {
        Task<GradeSummaryDto> SummariseAsync(Guid resourceLinkId, bool verify, CancellationToken ct);

        /// <summary>Marks everything postable as stale, so the worker sends it again.</summary>
        Task<int> ResyncAsync(Guid resourceLinkId, CancellationToken ct);
    }

    /// <summary>
    /// The half of §6.4 that AGS gives away: <b>asking the platform what it
    /// actually holds.</b>
    /// <para>
    /// It catches drift this module caused — a score the platform accepted and
    /// dropped for a stale timestamp, which succeeds silently — and drift it did
    /// not: a teacher editing a grade by hand, a course restored from a backup, a
    /// line item somebody deleted. Without it, the first of those is invisible
    /// by construction.
    /// </para>
    /// <para>
    /// <b>It reports and does not repair.</b> A teacher who edited a grade
    /// deliberately should not have it overwritten by a sweep they did not run;
    /// resynchronising is a decision, and it has a button rather than a schedule.
    /// </para>
    /// </summary>
    public class GradeVerifier(
        LtiDbContext db,
        IAgsClient ags,
        IPermissionService permissions,
        TimeProvider clock
    ) : IGradeVerifier
    {
        public async Task<GradeSummaryDto> SummariseAsync(
            Guid resourceLinkId, bool verify, CancellationToken ct)
        {
            var link = await db.ResourceLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == resourceLinkId, ct)
                ?? throw new NotFoundException("Placement");

            await permissions.RequireAsync(Permissions.ResultReadAll, link.ActivityId, ct);

            var states = await db.GradeSyncStates.AsNoTracking()
                .Where(s => s.ResourceLinkId == link.Id)
                .ToListAsync(ct);

            int? drifted = null;
            if (verify)
            {
                drifted = await DriftAsync(link, states, ct);
            }

            return new GradeSummaryDto
            {
                Total = states.Count,
                Synchronised = states.Count(s => s.State == GradeSyncStatus.Synchronised),
                Pending = states.Count(s => s.State == GradeSyncStatus.Pending),
                Deferred = states.Count(s => s.State == GradeSyncStatus.Deferred),
                Withheld = states.Count(s => s.State == GradeSyncStatus.Withheld),
                Failed = states.Count(s => s.State == GradeSyncStatus.Failed),
                Drifted = drifted,
                LastError = states
                    .Where(s => s.LastError is not null)
                    .OrderByDescending(s => s.UpdatedAt)
                    .Select(s => s.LastError)
                    .FirstOrDefault(),
            };
        }

        public async Task<int> ResyncAsync(Guid resourceLinkId, CancellationToken ct)
        {
            var link = await db.ResourceLinks.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == resourceLinkId, ct)
                ?? throw new NotFoundException("Placement");

            // A write against somebody else's gradebook, so it sits behind the
            // permission that governs the activity rather than the one that reads
            // its results.
            await permissions.RequireAsync(Permissions.ActivityUpdate, link.ActivityId, ct);

            var now = clock.GetUtcNow().UtcDateTime;

            // **Withheld and deferred are left alone.** They are not stuck; they
            // are correct, and a resync that posted them would publish through
            // the platform exactly what the activity is withholding.
            return await db.GradeSyncStates
                .Where(s => s.ResourceLinkId == link.Id
                    && (s.State == GradeSyncStatus.Failed
                        || s.State == GradeSyncStatus.Synchronised
                        || s.State == GradeSyncStatus.Pending))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.State, GradeSyncStatus.Pending)
                    .SetProperty(s => s.Attempts, 0)
                    .SetProperty(s => s.NextAttemptAt, (DateTime?)null)
                    .SetProperty(s => s.LastError, (string?)null)
                    .SetProperty(s => s.UpdatedAt, now), ct);
        }

        private async Task<int> DriftAsync(
            ResourceLink link, IReadOnlyList<GradeSyncState> states, CancellationToken ct)
        {
            var platform = await db.Platforms.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == link.PlatformId, ct);
            if (platform is null)
            {
                return 0;
            }

            var subjects = await db.ExternalIdentities.AsNoTracking()
                .Where(i => i.PlatformId == platform.Id)
                .ToDictionaryAsync(i => i.UserId, i => i.Subject, ct);

            var items = await db.LineItems.AsNoTracking()
                .Where(i => i.ResourceLinkId == link.Id && i.PlatformUrl != "")
                .ToListAsync(ct);

            var drifted = 0;
            foreach (var item in items)
            {
                IReadOnlyList<AgsResult> held;
                try
                {
                    held = await ags.ReadResultsAsync(platform, item.PlatformUrl, ct);
                }
                catch (Exception e) when (e is AgsException or LtiLaunchException)
                {
                    // A platform that will not answer is not drift. Counting it
                    // as drift would tell a manager their gradebook is wrong when
                    // what is wrong is the connection.
                    continue;
                }

                var byUser = held.ToDictionary(r => r.UserId, r => r.ResultScore);

                foreach (var state in states.Where(s => s.LineItemId == item.Id))
                {
                    if (state.State != GradeSyncStatus.Synchronised) continue;
                    if (!subjects.TryGetValue(state.UserId, out var subject)) continue;

                    var there = byUser.TryGetValue(subject, out var value) ? value : null;
                    if (there is null || Math.Abs(there.Value - (state.PostedScore ?? 0)) > 0.0001)
                    {
                        drifted++;
                    }
                }
            }

            return drifted;
        }
    }
}

using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Workers
{
    /// <summary>
    /// Opens and closes rounds, and says so.
    /// <para>
    /// Openness is <b>stored</b> (decided 2026-08-08), so this owns every
    /// transition: nothing else may compute whether a series is running, or
    /// there would be two answers to one question.
    /// </para>
    /// <para>
    /// Each transition is one conditional <c>UPDATE … WHERE marker IS NULL</c>,
    /// which is what makes it exactly-once with several Server instances: two
    /// schedulers racing both issue the update, one changes a row and the other
    /// changes none, and only the one that changed a row announces.
    /// </para>
    /// <para>
    /// The markers are kept <b>separate from the state they accompany</b>. A
    /// marker answers "has this been announced", never "is this open" — keeping
    /// them apart is what makes a missed announcement recoverable without
    /// touching the state, and what stops a re-announce.
    /// </para>
    /// </summary>
    public class SeriesScheduler(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<SeriesScheduler> logger
    ) : BackgroundService
    {
        /// <summary>
        /// From the shortest thing a participant would notice — a round opening.
        /// A minute would be visible; an hour would be a complaint.
        /// </summary>
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How late an announcement may be before it is marked as such.
        /// <para>
        /// Beyond this the Server was down, or the round was created with a start
        /// already past. Either way the Client must not draw a countdown from it
        /// as though it had just happened.
        /// </para>
        /// </summary>
        private static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            using var timer = new PeriodicTimer(Interval, clock);

            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stopping);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // One failed pass is not a failed process: the next runs in
                    // fifteen seconds, and every transition is conditional, so
                    // nothing is skipped by having failed once.
                    logger.LogError(e, "Series scheduler pass failed");
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

        internal async Task<int> TickAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var events = scope.ServiceProvider.GetRequiredService<IEventHub>();
            var gate = scope.ServiceProvider.GetRequiredService<ISeriesGate>();

            var now = clock.GetUtcNow().UtcDateTime;
            var announced = 0;

            announced += await OpenAsync(context, events, gate, now, ct);
            announced += await CloseAsync(context, events, gate, now, ct);
            announced += await WindowsAsync(context, events, now, ct);
            announced += await UnfreezeAsync(context, events, now, ct);

            return announced;
        }

        private async Task<int> OpenAsync(
            ApplicationDbContext context, IEventHub events, ISeriesGate gate,
            DateTime now, CancellationToken ct)
        {
            var due = await context.Series
                .Include(s => s.Activity)
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .Where(s => s.StartAnnouncedAt == null
                    && s.StartDate != null && s.StartDate <= now
                    && s.PausedAt == null
                    && (s.EndDate == null || s.EndDate > now))
                .ToListAsync(ct);

            foreach (var round in due)
            {
                round.IsOpen = true;
                round.StartAnnouncedAt = now;
            }
            if (due.Count > 0) await context.SaveChangesAsync(ct);

            foreach (var round in due)
            {
                var late = round.StartDate is { } start && now - start > Slack;
                if (late)
                {
                    logger.LogWarning(
                        "Series {Series} opened {Minutes:F0} minutes late",
                        round.Id, (now - round.StartDate!.Value).TotalMinutes);
                }
                await AnnounceAsync(context, events, gate, round, "opened", late, ct);
            }

            return due.Count;
        }

        private async Task<int> CloseAsync(
            ApplicationDbContext context, IEventHub events, ISeriesGate gate,
            DateTime now, CancellationToken ct)
        {
            var due = await context.Series
                .Include(s => s.Activity)
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .Where(s => s.EndAnnouncedAt == null && s.EndDate != null && s.EndDate <= now)
                .ToListAsync(ct);

            foreach (var round in due)
            {
                round.IsOpen = false;
                round.EndAnnouncedAt = now;
                // A round that ended without ever being opened — one created
                // wholly in the past — is still marked opened, so nothing later
                // tries to open it.
                round.StartAnnouncedAt ??= now;
            }
            if (due.Count > 0) await context.SaveChangesAsync(ct);

            foreach (var round in due)
            {
                var late = round.EndDate is { } end && now - end > Slack;
                await AnnounceAsync(context, events, gate, round, "closed", late, ct);
            }

            return due.Count;
        }

        /// <summary>
        /// A round's ranking window opening. Carries nothing: it means "there is
        /// a board that was not there", and the screen fetches it.
        /// </summary>
        private async Task<int> WindowsAsync(
            ApplicationDbContext context, IEventHub events, DateTime now, CancellationToken ct)
        {
            var due = await context.Series
                .Where(s => s.WindowAnnouncedAt == null
                    && ((s.RankingVisibleFrom != null && s.RankingVisibleFrom <= now)
                        || (s.RankingVisibleFrom == null && s.StartDate != null && s.StartDate <= now)))
                .ToListAsync(ct);

            foreach (var round in due) round.WindowAnnouncedAt = now;
            if (due.Count > 0) await context.SaveChangesAsync(ct);

            foreach (var round in due)
            {
                await RankingAsync(context, events, round, "windowOpened", ct);
            }

            return due.Count;
        }

        /// <summary>
        /// A freeze ending. Also carries nothing — everything it withheld is now
        /// readable, and what the reader holds is incomplete.
        /// </summary>
        private async Task<int> UnfreezeAsync(
            ApplicationDbContext context, IEventHub events, DateTime now, CancellationToken ct)
        {
            var due = await context.Series
                .Where(s => s.UnfrozenAnnouncedAt == null
                    && s.RankingFreezeAt != null
                    && s.RankingRevealAt != null && s.RankingRevealAt <= now)
                .ToListAsync(ct);

            foreach (var round in due) round.UnfrozenAnnouncedAt = now;
            if (due.Count > 0) await context.SaveChangesAsync(ct);

            foreach (var round in due)
            {
                await RankingAsync(context, events, round, "unfrozen", ct);
            }

            return due.Count;
        }

        /// <summary>
        /// Tells the activity's members, and nobody else.
        /// <para>
        /// The same disclosure the endpoint applies: a round that has opened
        /// carries its problems, and one that has not does not — so the event
        /// cannot leak what a fetch would have withheld.
        /// </para>
        /// </summary>
        private static async Task AnnounceAsync(
            ApplicationDbContext context, IEventHub events, ISeriesGate gate,
            Series round, string change, bool late, CancellationToken ct)
        {
            var members = await MembersAsync(context, round.ActivityId, Permissions.ActivityRead, ct);
            if (members.Count == 0) return;

            var open = round.Activity is not null && gate.MayReadProblems(round, round.Activity);

            var payload = new SeriesChangedData
            {
                ActivityId = Wire.Id(round.ActivityId),
                Change = change,
                Late = late ? true : null,
                Series = new SeriesDto
                {
                    Id = Wire.Id(round.Id),
                    Slug = round.Slug,
                    Name = round.Name,
                    StartDate = Wire.At(round.StartDate),
                    EndDate = Wire.At(round.EndDate),
                    IsOpen = round.IsOpen,
                    PausedAt = Wire.At(round.PausedAt),
                    RankingVisibleFrom = Wire.At(round.RankingVisibleFrom),
                    RankingVisibleTo = Wire.At(round.RankingVisibleTo),
                    ProblemCount = open || round.RevealProblemCount
                        ? round.SeriesProblems.Count
                        : null,
                    Problems = open
                        ? round.SeriesProblems
                            .OrderBy(sp => sp.Order).ThenBy(sp => sp.Id)
                            .Select(sp => new ProblemSummaryDto
                            {
                                Id = Wire.Id(sp.Id),
                                Slug = sp.Slug,
                                Name = sp.Name ?? sp.Problem?.Name ?? sp.Slug,
                                // Nobody's own standing: this goes to everybody,
                                // so it carries what is true of the problem and
                                // nothing that is true of one reader.
                                Status = "untouched",
                                Attempts = 0,
                            })
                            .ToList()
                        : null,
                },
            };

            await events.SendToUsersAsync(members, EventTypes.SeriesChanged, payload, ct);
        }

        private static async Task RankingAsync(
            ApplicationDbContext context, IEventHub events,
            Series round, string change, CancellationToken ct)
        {
            var members = await MembersAsync(context, round.ActivityId, Permissions.RankingRead, ct);
            if (members.Count == 0) return;

            await events.SendToUsersAsync(members, EventTypes.RankingChanged, new RankingChangedData
            {
                ActivityId = Wire.Id(round.ActivityId),
                Change = change,
                SeriesId = Wire.Id(round.Id),
            }, ct);
        }

        /// <summary>
        /// Who in this activity holds the permission the event's data is guarded
        /// by. The fan-out runs through the same rule as a fetch would.
        /// </summary>
        private static async Task<List<string>> MembersAsync(
            ApplicationDbContext context, Guid activityId, string permission, CancellationToken ct)
        {
            var grants = await context.Grants
                .AsNoTracking()
                .Where(g => g.ActivityId == activityId && g.State == GrantState.Active)
                .Select(g => new { g.UserId, g.Permissions })
                .ToListAsync(ct);

            return grants
                .Where(g => g.Permissions.Contains(permission, StringComparison.Ordinal)
                    || g.Permissions.Contains(Permissions.SystemAdministrator, StringComparison.Ordinal))
                .Select(g => g.UserId)
                .Distinct()
                .ToList();
        }
    }
}

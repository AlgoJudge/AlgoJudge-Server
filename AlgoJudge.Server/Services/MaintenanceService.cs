using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// How far the Server has withdrawn, and the two ways that changes.
    /// <para>
    /// Read on **every request** that the gate guards, the way
    /// `IdentitySurface` reads the instance row on every registration attempt.
    /// No cache and no `IOptionsMonitor`: a stale answer here means either a
    /// request served during a backup or a Server that stays closed after
    /// somebody opened it, and one round trip to Postgres is cheaper than
    /// either.
    /// </para>
    /// </summary>
    public interface IMaintenanceService
    {
        /// <summary>The level in force, creating the row on first use.</summary>
        Task<MaintenanceState> StateAsync(CancellationToken ct);

        /// <summary>
        /// The level alone, untracked, and creating nothing.
        /// <para>
        /// For the claim path, which asks on every look and needs one enum
        /// rather than a row it will not write. <see cref="StateAsync"/> selects
        /// and tracks the whole singleton, which on a loop that re-enters per
        /// nudge is a round trip and a tracked entity per attempt for a table
        /// with one row in it.
        /// </para>
        /// <para>
        /// **An absent row is <see cref="MaintenanceLevel.Open"/>**, the same
        /// answer <see cref="StateAsync"/> gives by creating one — and the same
        /// reason: the absence means nobody has ever asked for maintenance.
        /// Creating it is left to whoever actually throws the switch.
        /// </para>
        /// </summary>
        Task<MaintenanceLevel> LevelAsync(CancellationToken ct);

        /// <summary>
        /// Throws the switch. **On means <see cref="MaintenanceLevel.Draining"/>,
        /// never straight to closed** — work in flight is given its chance to
        /// finish, and the drainer decides when that is over. Off means open,
        /// immediately.
        /// </summary>
        Task<MaintenanceState> SetAsync(bool on, string? reason, CancellationToken ct);

        /// <summary>
        /// Moves `Draining` to `Closed`. Called by the drainer alone, which is
        /// what decides that nothing is in flight any more.
        /// </summary>
        Task<MaintenanceState> CloseAsync(CancellationToken ct);
    }

    /// <summary>
    /// The level in force, in this process.
    /// <para>
    /// **The row is the backup, not the source.** An operator throws the switch
    /// a handful of times a year; the claim path asks on every look, and since
    /// a claim may be held open and re-entered on every nudge, that was a round
    /// trip per waiting Runner per submission for a table with one row in it.
    /// The answer belongs in memory, and the row exists so that a restart
    /// remembers a drain that was in progress.
    /// </para>
    /// <para>
    /// **Single instance, and that is a real assumption.** A second Server
    /// would not see this one's switch until its own copy expired, and there is
    /// no expiry — so this is correct exactly as far as the 0.1.0 deployment
    /// goes, which is one Compose stack. The decision of 2026-08-27 already
    /// names what replaces it when there are two: PostgreSQL
    /// <c>LISTEN</c>/<c>NOTIFY</c>, the same backplane the queue's nudge will
    /// need, and this is one more caller for it rather than a new problem.
    /// </para>
    /// </summary>
    public sealed class MaintenanceLevelCache
    {
        /// <summary>
        /// Boxed because <c>volatile</c> cannot be applied to a nullable enum,
        /// and a torn read of the level is exactly what this must not allow.
        /// </summary>
        private volatile object? known;

        public MaintenanceLevel? Known => (MaintenanceLevel?)known;

        public void Remember(MaintenanceLevel level) => known = level;
    }

    public class MaintenanceService(
        ApplicationDbContext context,
        IEventHub events,
        MaintenanceLevelCache cache,
        TimeProvider clock
    ) : IMaintenanceService
    {
        public async Task<MaintenanceState> StateAsync(CancellationToken ct)
        {
            var state = await context.Maintenance.FirstOrDefaultAsync(ct);
            if (state is not null) return state;

            // Created on first use rather than by the migration, so a database
            // restored from a dump that predates this table still starts — and
            // starts **open**, which is the only safe default for a row whose
            // absence means "nobody has ever asked for maintenance".
            state = new MaintenanceState { Id = MaintenanceState.SingletonId };
            context.Maintenance.Add(state);
            await context.SaveChangesAsync(ct);
            return state;
        }

        public async Task<MaintenanceLevel> LevelAsync(CancellationToken ct)
        {
            if (cache.Known is { } known) return known;

            // Once per process, or once after a restart while a drain was on.
            var level = await context.Maintenance
                .AsNoTracking()
                .Select(m => (MaintenanceLevel?)m.Level)
                .FirstOrDefaultAsync(ct) ?? MaintenanceLevel.Open;
            cache.Remember(level);
            return level;
        }

        public async Task<MaintenanceState> SetAsync(bool on, string? reason, CancellationToken ct)
        {
            var state = await StateAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;

            if (on)
            {
                // Asking twice is not an error and must not restart the clock:
                // `RequestedAt` is what the forced close is measured against, so
                // a script that calls this in a loop would otherwise hold the
                // Server in `Draining` for ever.
                if (state.Level == MaintenanceLevel.Open)
                {
                    state.Level = MaintenanceLevel.Draining;
                    state.RequestedAt = now;
                    state.Reason = reason;
                    cache.Remember(state.Level);
                }
            }
            else
            {
                state.Level = MaintenanceLevel.Open;
                state.RequestedAt = null;
                state.Reason = null;
                cache.Remember(state.Level);
            }

            await context.SaveChangesAsync(ct);
            await AnnounceAsync(state, ct);
            return state;
        }

        public async Task<MaintenanceState> CloseAsync(CancellationToken ct)
        {
            var state = await StateAsync(ct);
            // Only from draining. Closing an open Server would be a withdrawal
            // nobody asked for, and this is called from a timer.
            if (state.Level == MaintenanceLevel.Draining)
            {
                state.Level = MaintenanceLevel.Closed;
                cache.Remember(state.Level);
                await context.SaveChangesAsync(ct);
                await AnnounceAsync(state, ct);
            }
            return state;
        }

        /// <summary>
        /// Tells every connected session, whichever way the level moved.
        /// <para>
        /// <b>One path for all three transitions.</b> Going away is the one that
        /// does the work: it reaches a tab that is asking for nothing, which
        /// would otherwise find out at the next thing somebody clicked.
        /// </para>
        /// <para>
        /// Coming back is announced too, and it is <b>not</b> what brings a
        /// Client back — a Client that has stood aside has closed its socket,
        /// and at <c>Closed</c> the handshake is refused anyway. Polling
        /// <c>/health</c> is what ends a window on the other side. This is sent
        /// for the readers that never blocked, and because a switch that
        /// announced one direction and not the other is a trap for whoever
        /// writes the next consumer.
        /// </para>
        /// <para>
        /// Everybody, because a window is not scoped by any permission: it
        /// applies to whoever is looking. This mirrors how an instance change is
        /// announced.
        /// </para>
        /// </summary>
        private async Task AnnounceAsync(MaintenanceState state, CancellationToken ct)
        {
            var everybody = await context.Users.AsNoTracking()
                .Where(u => !u.Anonymized)
                .Select(u => u.Id)
                .ToListAsync(ct);

            await events.SendToUsersAsync(
                everybody,
                EventTypes.MaintenanceChanged,
                new { maintenance = MaintenanceWire.Dto(state) },
                ct);
        }
    }

    /// <summary>What `/health` and the switch both answer with.</summary>
    public static class MaintenanceWire
    {
        /// <summary>`open` | `draining` | `closed`.</summary>
        public static string Word(MaintenanceLevel level) => level switch
        {
            MaintenanceLevel.Draining => "draining",
            MaintenanceLevel.Closed => "closed",
            _ => "open",
        };

        public static MaintenanceDto Dto(MaintenanceState state) => new()
        {
            Level = Word(state.Level),
            Since = state.RequestedAt is { } at ? Wire.At(at) : null,
            Reason = state.Reason,
        };
    }
}

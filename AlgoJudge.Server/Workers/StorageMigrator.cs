using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Workers
{
    /// <summary>
    /// Moves files to the store that takes new writes, one file at a time.
    /// <para>
    /// <b>A different worker from <see cref="FileCollector"/> (A79).</b> One
    /// copies and one deletes, and keeping them apart is what makes the ordering
    /// legible: nothing here removes a source copy until a grace period after the
    /// target is committed, and the collector never sees a file mid-move because
    /// a file mid-move is still referenced.
    /// </para>
    /// <para>
    /// <b>Started deliberately (A83).</b> Changing <c>Storage__Default</c> moves
    /// nothing — it only says where the next upload goes. Somebody asks for this,
    /// with <c>aj-admin storage migrate</c>, usually right after a backup.
    /// </para>
    /// <para>
    /// <b>Resumable because it keeps no state of its own (A85).</b> Which files
    /// have moved is on the files. A process killed halfway leaves either a file
    /// that moved or a file that did not, plus at most one orphan blob at the
    /// target that the collector takes — there is nothing to reconcile and no
    /// recovery path to get wrong.
    /// </para>
    /// </summary>
    public class StorageMigrator(
        IServiceScopeFactory scopes,
        IBlobStoreRegistry stores,
        TimeProvider clock,
        IConfiguration configuration,
        ILogger<StorageMigrator> logger
    ) : BackgroundService
    {
        /// <summary>
        /// The hour a migration may begin in, UTC. <b>Negative means any hour</b>
        /// — for an operator who has already stopped the installation and is not
        /// waiting until two in the morning to move their own files.
        /// </summary>
        public const string StartHourSetting = "Storage:Migration:StartHourUtc";
        public const string BudgetMinutesSetting = "Storage:Migration:BudgetMinutes";
        public const string GraceMinutesSetting = "Storage:Migration:GraceMinutes";

        /// <summary>
        /// How often it looks while something is in flight: a migration exists,
        /// or stale copies are waiting out their grace period. Fast enough to
        /// lose none of a thirty-minute budget to the window opening late, and to
        /// notice the evaluation queue emptying.
        /// </summary>
        private static readonly TimeSpan Busy = TimeSpan.FromSeconds(30);

        /// <summary>
        /// And how often when nothing is: no migration, nothing to sweep.
        /// <para>
        /// <b>Most installations are here for ever.</b> A migration is a rare,
        /// deliberate act, so a thirty-second tick would be two indexed queries
        /// every thirty seconds, in perpetuity, to learn nothing — nearly three
        /// thousand round trips a day for the answer "no". Five minutes costs a
        /// migration nothing that matters: the window is an hour wide, the grace
        /// period an hour long, and the operator asking for one is told it starts
        /// in its window rather than at once.
        /// </para>
        /// </summary>
        private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Its own key, distinct from the collector's.
        /// <para>
        /// Two Servers copying the same file would each write it to the target and
        /// each commit a row, which is survivable, and would then each delete a
        /// source copy the other still points at, which is not.
        /// </para>
        /// </summary>
        private const long LockKey = 0x41_4A_4D_47; // "AJMG"

        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            var next = Idle;

            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(next, clock, stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    // The pace follows the work: a run in progress is watched
                    // closely, and an installation that has never migrated is
                    // barely watched at all.
                    next = await RunOnceAsync(stopping) ? Busy : Idle;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "The storage migration failed");
                    next = Busy;
                }
            }
        }

        /// <summary>
        /// One tick: move what may be moved, then sweep what may be swept.
        /// <para>
        /// Answers whether anything is in flight, which is what decides how soon
        /// the next tick comes.
        /// </para>
        /// </summary>
        internal async Task<bool> RunOnceAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // The stale copies come first, and unconditionally: they are the tail
            // of a migration that may already have finished, and holding them
            // hostage to the window would keep somebody's old volume full.
            var pending = await SweepPreviousCopiesAsync(context, ct);

            var migration = await context.StorageMigrations
                .FirstOrDefaultAsync(
                    m => m.State == StorageMigrationState.Requested
                        || m.State == StorageMigrationState.Running, ct);
            if (migration is null) return pending;

            if (await WaitingForAsync(context, migration, ct) is { } waiting)
            {
                // Recorded rather than logged only, because "why is it not
                // moving" is the question an operator will actually have.
                if (migration.Detail != waiting)
                {
                    migration.Detail = waiting;
                    await context.SaveChangesAsync(ct);
                }
                return true;
            }

            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(ct);

            try
            {
                await using var take = connection.CreateCommand();
                take.CommandText = $"SELECT pg_try_advisory_lock({LockKey})";
                if ((bool?)await take.ExecuteScalarAsync(ct) is not true)
                {
                    logger.LogInformation("Another instance is migrating; skipping this tick");
                    return true;
                }

                try
                {
                    await MoveAsync(context, migration, ct);
                }
                finally
                {
                    await using var release = connection.CreateCommand();
                    release.CommandText = $"SELECT pg_advisory_unlock({LockKey})";
                    await release.ExecuteScalarAsync(ct);
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }

            return true;
        }

        /// <summary>
        /// What this migration is waiting for, or null when it may go.
        /// <para>
        /// <b>Nothing moves while work is in flight (A81).</b> A Runner holding a
        /// job is about to read a package, and a series that is open is somebody
        /// sitting an exam. Copying is not dangerous to either — a read follows
        /// its own row for the whole move — but the load is, and a contest is the
        /// worst hour of the year to add any.
        /// </para>
        /// </summary>
        private async Task<string?> WaitingForAsync(
            ApplicationDbContext context, StorageMigration migration, CancellationToken ct)
        {
            if (stores.Find(migration.TargetStoreId) is null)
            {
                return "the target store is not configured on this Server";
            }

            var hour = configuration.GetValue(StartHourSetting, 2);
            var budget = TimeSpan.FromMinutes(configuration.GetValue(BudgetMinutesSetting, 30));
            var now = clock.GetUtcNow().UtcDateTime;

            // A negative hour means no window at all, which is what an operator
            // who has already taken the installation down for maintenance wants:
            // they did not stop everything in order to wait until two in the
            // morning. The gates below still apply.
            if (hour is >= 0 and <= 23)
            {
                // **The window bounds the whole run, not only its first minute.**
                // This checked `State == Requested` until 2026-08-13, so once a
                // migration had begun the window stopped applying entirely — and
                // the budget was recomputed on every thirty-second tick, so it
                // bounded nothing either. A migration that started at 02:00 was
                // still copying at nine in the morning, under a working day,
                // which is the one thing the window exists to prevent. Found by
                // being asked when a migration actually runs.
                if (now.Hour != hour) return $"waiting for the window at {hour:00}:00 UTC";

                // And within the window, for at most the budget. `StartedAt` is
                // when this window's stretch began, so a run that has spent its
                // budget waits for the next window rather than starting over on
                // the next tick.
                if (InThisWindow(migration.StartedAt, now) && now - migration.StartedAt >= budget)
                {
                    return "the time budget for this window is spent; it continues in the next one";
                }
            }

            if (await context.EvaluationJobs.AnyAsync(
                    j => j.State == EvaluationJobState.Queued || j.State == EvaluationJobState.Running, ct))
            {
                return "waiting for the evaluation queue to empty";
            }

            if (await context.Series.AnyAsync(s => s.IsOpen, ct))
            {
                return "waiting for every series to close";
            }

            return null;
        }

        private async Task MoveAsync(
            ApplicationDbContext context, StorageMigration migration, CancellationToken ct)
        {
            var target = stores.Find(migration.TargetStoreId)!;

            var now = clock.GetUtcNow().UtcDateTime;

            if (migration.State == StorageMigrationState.Requested)
            {
                // **Before the first byte moves (A84).** A target that cannot
                // write and read back its own probe is a target that would take
                // files and lose them, and the source copy goes after a grace
                // period whether or not anybody was watching.
                var health = await target.CheckHealthAsync(ct);
                if (!health.Ok)
                {
                    migration.State = StorageMigrationState.Refused;
                    migration.FinishedAt = clock.GetUtcNow().UtcDateTime;
                    migration.Detail = "the target store did not pass its smoke test";
                    await context.SaveChangesAsync(ct);
                    logger.LogError(
                        "Refusing to migrate to {Store}: {Detail}", target.Id, health.Detail);
                    return;
                }

                migration.State = StorageMigrationState.Running;
                migration.Detail = null;
            }

            // A fresh stretch: either this run has just begun, or a new window
            // has opened since the last one. Recorded before any file moves, so
            // the budget is measured from when work actually started.
            if (!InThisWindow(migration.StartedAt, now))
            {
                migration.StartedAt = now;
            }

            await context.SaveChangesAsync(ct);

            var budget = TimeSpan.FromMinutes(configuration.GetValue(BudgetMinutesSetting, 30));
            var grace = TimeSpan.FromMinutes(configuration.GetValue(GraceMinutesSetting, 60));

            // Measured from when this window's stretch began, not from now:
            // this method runs once per tick, so `now + budget` would hand every
            // tick a fresh half hour and the budget would bound nothing.
            var until = (migration.StartedAt ?? now) + budget;

            // **What this run has already failed on.** Without it a single file
            // the target refuses is picked again on the very next pass — the
            // query would keep answering with it — and the run spends its whole
            // budget copying one broken thing to no effect. Found by a test that
            // hung for ten minutes rather than failing.
            //
            // Per run, deliberately: the next one tries again, because whatever
            // was wrong may have been the network rather than the file.
            var failed = new HashSet<Guid>();

            while (!ct.IsCancellationRequested)
            {
                // **The budget holds back the next file and never interrupts one
                // in flight (A82).** A 128 MiB copy cut in half is a blob nobody
                // asked for and a row that still points at the source; letting it
                // finish costs a minute and leaves nothing behind.
                if (clock.GetUtcNow().UtcDateTime >= until)
                {
                    migration.Detail = "the time budget ended this run";
                    await context.SaveChangesAsync(ct);
                    return;
                }

                var file = await context.Files
                    .Where(f => f.StorageId != migration.TargetStoreId
                        && f.PreviousStorageId == null
                        && !failed.Contains(f.Id))
                    .OrderBy(f => f.CreatedAt)
                    .FirstOrDefaultAsync(ct);

                if (file is null && failed.Count > 0)
                {
                    // Everything that could move has. Saying "finished" here
                    // would be a claim that every file is on the target, which
                    // is exactly what the skipped ones are not.
                    migration.Detail =
                        $"{failed.Count} file(s) could not be moved; the next run tries again";
                    await context.SaveChangesAsync(ct);
                    return;
                }

                if (file is null)
                {
                    migration.State = StorageMigrationState.Finished;
                    migration.FinishedAt = clock.GetUtcNow().UtcDateTime;
                    migration.Detail = null;
                    await context.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "Migration to {Store} finished: {Files} files, {Bytes} bytes",
                        target.Id, migration.FilesMoved, migration.BytesMoved);
                    return;
                }

                if (!await MoveOneAsync(context, file, target, grace, ct))
                {
                    failed.Add(file.Id);
                    continue;
                }

                migration.FilesMoved++;
                migration.BytesMoved += file.SizeBytes;
                await context.SaveChangesAsync(ct);
            }
        }

        /// <summary>
        /// Whether a moment belongs to the window that is open now.
        /// <para>
        /// Same day and same hour: a stretch that began in yesterday's window is
        /// not this one's, so today's budget starts fresh.
        /// </para>
        /// </summary>
        private static bool InThisWindow(DateTime? began, DateTime now) =>
            began is { } value && value.Date == now.Date && value.Hour == now.Hour;

        /// <summary>
        /// One file: read it where it is, check it is itself, write it to the
        /// target, and only then say so in one transaction.
        /// </summary>
        private async Task<bool> MoveOneAsync(
            ApplicationDbContext context,
            Database.Models.File file,
            IBlobStore target,
            TimeSpan grace,
            CancellationToken ct)
        {
            if (stores.Find(file.StorageId) is not { } source)
            {
                // Nothing can be done about it here and it must not stop the run:
                // the health surface already names it, and leaving it alone is
                // better than a migration that stalls for ever on one row.
                logger.LogWarning(
                    "Not migrating {File}: the store it names is not configured", file.Id);
                file.PreviousStorageId = null;
                return false;
            }

            var key = new BlobKey(file.Id, file.Sha256);

            try
            {
                BlobWriteResult written;
                await using (var reading = await source.OpenReadAsync(key, ct))
                {
                    written = await target.WriteAsync(file.Id, reading, ct);
                }

                // **Verified, not assumed (A77).** This is the one moment the
                // product's own copy of somebody's submission passes through a
                // network it did not before; a silent corruption here would be
                // discovered by a Runner refusing to judge, weeks later.
                if (!string.Equals(written.Sha256, file.Sha256, StringComparison.Ordinal))
                {
                    await target.DeleteAsync(key, ct);
                    logger.LogError(
                        "Not migrating {File}: it did not arrive at {Store} as itself",
                        file.Id, target.Id);
                    return false;
                }

                // One transaction, and the order inside it is the whole design:
                // the row points at the target, remembers where the stale copy
                // is, and says when that copy may go. A reader that resolved this
                // row a moment ago is still reading the source, which is why the
                // copy outlives the switch.
                file.PreviousStorageId = file.StorageId;
                file.StorageId = target.Id;
                file.PreviousCopyDeleteAfter = clock.GetUtcNow().UtcDateTime + grace;
                await context.SaveChangesAsync(ct);

                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Could not migrate {File}", file.Id);

                // Whatever reached the target is unreferenced, and the collector
                // takes it. The row is untouched, so the next run tries again.
                try { await target.DeleteAsync(key, ct); } catch { /* best effort */ }
                return false;
            }
        }

        /// <summary>
        /// Removes source copies whose grace period has passed.
        /// <para>
        /// <b>Never before the target is committed (A78)</b>, which is why this
        /// reads the row rather than remembering anything: a copy is removable
        /// exactly when its file already says it lives somewhere else.
        /// </para>
        /// </summary>
        /// <returns>Whether any stale copy is still waiting to be removed.</returns>
        private async Task<bool> SweepPreviousCopiesAsync(
            ApplicationDbContext context, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var due = await context.Files
                .Where(f => f.PreviousCopyDeleteAfter != null && f.PreviousCopyDeleteAfter <= now)
                .Take(200)
                .ToListAsync(ct);

            foreach (var file in due)
            {
                if (file.PreviousStorageId is { } previous && stores.Find(previous) is { } store)
                {
                    try
                    {
                        await store.DeleteAsync(new BlobKey(file.Id, file.Sha256), ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Left for the next sweep. An undeleted stale copy costs
                        // space; a cleared column would lose it for ever.
                        logger.LogWarning(e, "Could not remove the stale copy of {File}", file.Id);
                        continue;
                    }
                }

                file.PreviousStorageId = null;
                file.PreviousCopyDeleteAfter = null;
            }

            if (due.Count > 0) await context.SaveChangesAsync(ct);

            // Anything left with a copy to remove — whether its grace period has
            // passed or not — is a reason to come back soon.
            return await context.Files.AnyAsync(f => f.PreviousCopyDeleteAfter != null, ct);
        }
    }
}

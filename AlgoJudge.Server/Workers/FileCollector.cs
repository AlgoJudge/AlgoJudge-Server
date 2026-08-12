using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Workers
{
    public record CollectorReport
    {
        public required int Candidates { get; init; }
        public required int Deleted { get; init; }
        public required long BytesReclaimed { get; init; }
        public required bool DryRun { get; init; }
    }

    /// <summary>
    /// Removes files nothing points at any more.
    /// <para>
    /// <b>Retention is a property of what references a file, not of the file.</b>
    /// The sweep asks two questions in order: is anything still pointing at this,
    /// and if everything pointing at it has been superseded, for how long does
    /// that kind of owner keep its old revisions.
    /// </para>
    /// <list type="table">
    /// <item><term>Referenced by nothing</term><description>24 hours from upload</description></item>
    /// <item><term>Superseded Runner attachment</term><description>30 days, or past 20 revisions per name</description></item>
    /// <item><term>Superseded instance document</term><description>for ever — it is the history</description></item>
    /// <item><term>Referenced by a problem version</term><description>as long as the version exists</description></item>
    /// </list>
    /// <para>
    /// A superseded reference is <b>marked, not deleted</b>: deleting it would
    /// leave its file with no reference at all, and an unreferenced file goes in
    /// twenty-four hours — which would make the thirty-day and forever rules
    /// unreachable.
    /// </para>
    /// </summary>
    public class FileCollector(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IConfiguration configuration,
        Storage.IBlobStoreRegistry stores,
        ILogger<FileCollector> logger
    ) : BackgroundService
    {
        /// <summary>
        /// Generous on purpose: it has to outlast a slow upload followed by a
        /// long think. A manager who uploads a 128 MB package and then closes
        /// the tab is an ordinary Tuesday.
        /// </summary>
        private static readonly TimeSpan MinimumOrphanAge = TimeSpan.FromHours(24);

        private static readonly TimeSpan RunnerAttachmentAge = TimeSpan.FromDays(30);

        /// <summary>
        /// The count is what makes the age bounded. Thirty days alone bounds
        /// nothing if a Runner re-sends its log every minute — that is forty
        /// thousand revisions of one file in a month.
        /// </summary>
        private const int RunnerAttachmentRevisions = 20;

        /// <summary>
        /// A ceiling per run, so a first sweep over a neglected installation is a
        /// long series of small transactions rather than one enormous one.
        /// </summary>
        private const int MaxDeletionsPerRun = 500;

        protected override async Task ExecuteAsync(CancellationToken stopping)
        {
            while (!stopping.IsCancellationRequested)
            {
                var wait = UntilNextRun();
                try
                {
                    await Task.Delay(wait, clock, stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    var report = await CollectAsync(dryRun: false, stopping);
                    logger.LogInformation(
                        "File sweep: {Candidates} candidates, {Deleted} deleted, {Bytes} bytes reclaimed",
                        report.Candidates, report.Deleted, report.BytesReclaimed);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "File sweep failed");
                }
            }
        }

        /// <summary>
        /// Time until the next scheduled hour. Six in the morning by default:
        /// after the night's contests and before anybody is working.
        /// </summary>
        private TimeSpan UntilNextRun()
        {
            var hour = configuration.GetValue("Files:CollectAtHourUtc", 6);
            var now = clock.GetUtcNow().UtcDateTime;
            var next = now.Date.AddHours(hour);
            if (next <= now) next = next.AddDays(1);
            return next - now;
        }

        /// <summary>
        /// One sweep.
        /// <para>
        /// Guarded by a PostgreSQL advisory lock, so several Server instances do
        /// not sweep at once — two collectors racing would each see the other's
        /// half-finished work and could delete a file the other had just decided
        /// to keep.
        /// </para>
        /// </summary>
        internal async Task<CollectorReport> CollectAsync(bool dryRun, CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // A session-scoped advisory lock: taken without blocking, released
            // when this connection goes. Whoever does not get it simply skips
            // this run, which is correct — the sweep is idempotent and the next
            // one is a day away.
            const long LockKey = 0x41_4A_46_43; // "AJFC"
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(ct);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT pg_try_advisory_lock({LockKey})";
                var acquired = (bool?)await command.ExecuteScalarAsync(ct) ?? false;

                if (!acquired)
                {
                    logger.LogInformation("Another instance is sweeping; skipping this run");
                    return new CollectorReport
                    {
                        Candidates = 0, Deleted = 0, BytesReclaimed = 0, DryRun = dryRun,
                    };
                }

                try
                {
                    return await SweepAsync(context, dryRun, ct);
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
        }

        private async Task<CollectorReport> SweepAsync(
            ApplicationDbContext context, bool dryRun, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var orphanCutoff = now - MinimumOrphanAge;

            await DropExpiredRunnerRevisionsAsync(context, now, dryRun, ct);

            // Everything nothing points at, old enough to be sure it was
            // abandoned rather than in flight.
            var orphans = await context.Files
                .Where(f => f.CreatedAt <= orphanCutoff && !context.FileReferences.Any(r => r.FileId == f.Id))
                .OrderBy(f => f.CreatedAt)
                .Take(MaxDeletionsPerRun)
                .ToListAsync(ct);

            var bytes = orphans.Sum(f => f.SizeBytes);

            if (!dryRun && orphans.Count > 0)
            {
                var collected = new List<Database.Models.File>(orphans.Count);

                foreach (var file in orphans)
                {
                    logger.LogDebug(
                        "Collecting {File} ({Name}, {Bytes} bytes, uploaded {Uploaded})",
                        file.Id, file.Name, file.SizeBytes, file.CreatedAt);

                    // **The bytes first, and the row only if they went.** The row
                    // is the only record of where the bytes are; dropping it while
                    // they remain leaks them permanently, because nothing left
                    // knows the key. A store that is configured away is skipped
                    // rather than forced — its files are still somebody's, and
                    // this is the sweep that would quietly forget them.
                    if (stores.Find(file.StorageId) is not { } store)
                    {
                        logger.LogWarning(
                            "Not collecting {File}: the store it names is not configured", file.Id);
                        continue;
                    }

                    try
                    {
                        await store.DeleteAsync(new Storage.BlobKey(file.Id, file.Sha256), ct);
                        collected.Add(file);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Leave the row. The next run tries again, and until then
                        // the file is merely uncollected rather than unreachable.
                        logger.LogWarning(e, "Could not remove the bytes of {File}", file.Id);
                    }
                }

                bytes = collected.Sum(f => f.SizeBytes);
                context.Files.RemoveRange(collected);
                await context.SaveChangesAsync(ct);

                return new CollectorReport
                {
                    Candidates = orphans.Count,
                    Deleted = collected.Count,
                    BytesReclaimed = bytes,
                    DryRun = false,
                };
            }

            return new CollectorReport
            {
                Candidates = orphans.Count,
                Deleted = dryRun ? 0 : orphans.Count,
                BytesReclaimed = dryRun ? 0 : bytes,
                DryRun = dryRun,
            };
        }

        /// <summary>
        /// Drops the references a Runner attachment has outgrown — by age, or by
        /// count, whichever says so first. Their files then become orphans and go
        /// on this run or the next.
        /// <para>
        /// Only Runner attachments. A superseded instance document is kept for
        /// ever because it is the history, and a problem version's files are
        /// never superseded at all.
        /// </para>
        /// </summary>
        private async Task DropExpiredRunnerRevisionsAsync(
            ApplicationDbContext context, DateTime now, bool dryRun, CancellationToken ct)
        {
            var superseded = await context.FileReferences
                .Where(r => r.OwnerKind == FileOwnerKind.Runner && r.SupersededAt != null)
                .ToListAsync(ct);

            if (superseded.Count == 0) return;

            var tooOld = superseded
                .Where(r => now - r.SupersededAt!.Value > RunnerAttachmentAge)
                .ToList();

            var tooMany = superseded
                .GroupBy(r => (r.RunnerId, r.Name))
                .SelectMany(group => group
                    .OrderByDescending(r => r.SupersededAt)
                    .Skip(RunnerAttachmentRevisions))
                .ToList();

            var dropping = tooOld.Concat(tooMany).DistinctBy(r => r.Id).ToList();
            if (dropping.Count == 0 || dryRun) return;

            context.FileReferences.RemoveRange(dropping);
            await context.SaveChangesAsync(ct);

            logger.LogInformation("Dropped {Count} superseded Runner attachments", dropping.Count);
        }
    }
}

using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Storage
{
    /// <summary>Asking for a migration, and asking how it is going.</summary>
    public interface IStorageMigrations
    {
        /// <summary>
        /// Asks for one, towards whichever store takes new writes.
        /// <para>
        /// <b>Refuses a second while one is live.</b> Two migrations towards two
        /// targets would each move files the other had just moved, and the loser
        /// would delete a copy the winner's rows point at.
        /// </para>
        /// </summary>
        Task<StorageMigration> RequestAsync(CancellationToken ct);

        /// <summary>The live one, or the last one to finish. Null on an installation that has never run one.</summary>
        Task<StorageMigration?> LatestAsync(CancellationToken ct);

        /// <summary>Calls off a live one. What has already moved stays moved.</summary>
        Task<StorageMigration?> CancelAsync(CancellationToken ct);
    }

    public sealed class StorageMigrations(
        ApplicationDbContext context,
        IBlobStoreRegistry stores,
        TimeProvider clock
    ) : IStorageMigrations
    {
        public async Task<StorageMigration> RequestAsync(CancellationToken ct)
        {
            var live = await LiveAsync(ct);
            if (live is not null)
            {
                throw new ConflictException(
                    "A storage migration is already under way", "storage.migration.running");
            }

            var migration = new StorageMigration
            {
                TargetStoreId = stores.Default.Id,
                RequestedAt = clock.GetUtcNow().UtcDateTime,
            };

            context.StorageMigrations.Add(migration);
            await context.SaveChangesAsync(ct);
            return migration;
        }

        public Task<StorageMigration?> LatestAsync(CancellationToken ct) =>
            context.StorageMigrations
                .AsNoTracking()
                .OrderByDescending(m => m.RequestedAt)
                .FirstOrDefaultAsync(ct);

        public async Task<StorageMigration?> CancelAsync(CancellationToken ct)
        {
            var live = await LiveAsync(ct);
            if (live is null) return null;

            live.State = StorageMigrationState.Cancelled;
            live.FinishedAt = clock.GetUtcNow().UtcDateTime;
            live.Detail = "called off by an operator";

            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException conflict)
            {
                // The worker moved it between the read and the write. If it
                // reached an end state there is nothing left to call off, which
                // is the same answer as finding none — the operator asked for a
                // migration to stop and it has.
                foreach (var entry in conflict.Entries) await entry.ReloadAsync(ct);

                if (live.State is not (StorageMigrationState.Requested or StorageMigrationState.Running))
                {
                    return null;
                }

                throw new ConflictException(
                    "The migration changed while it was being called off. Ask again.",
                    "storage.migration.conflict");
            }

            return live;
        }

        /// <summary>
        /// The migration under way, oldest first.
        /// <para>
        /// <b>The order is not decoration.</b> `RequestAsync` guards against a
        /// second live migration by reading and then inserting, which two
        /// requests at once can walk through — so there can be two. Unordered,
        /// this and the worker's identical query could return <i>different</i>
        /// rows, and `storage cancel` would report cancelling a migration while
        /// the one actually moving files carried on. `LatestAsync` already
        /// orders, the other way, because "what happened last" is a different
        /// question from "what is running".
        /// </para>
        /// </summary>
        private Task<StorageMigration?> LiveAsync(CancellationToken ct) =>
            context.StorageMigrations
                .OrderBy(m => m.RequestedAt)
                .FirstOrDefaultAsync(
                    m => m.State == StorageMigrationState.Requested
                        || m.State == StorageMigrationState.Running, ct);
    }
}

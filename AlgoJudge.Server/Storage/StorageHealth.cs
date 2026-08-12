using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Storage
{
    /// <summary>Whether storage is working, in the two amounts of detail it may be said in.</summary>
    public interface IStorageHealth
    {
        /// <summary>
        /// One word, for anybody. <c>ok</c> or <c>degraded</c>, and nothing else
        /// — no store, no backend, no bucket, no path (A65c).
        /// </summary>
        Task<string> SummaryAsync(CancellationToken ct);

        /// <summary>Per store, for an operator who has proved they are one (A65b).</summary>
        Task<StorageReport> ReportAsync(CancellationToken ct);

        /// <summary>
        /// The startup check of §11: every <c>StorageId</c> in <c>Files</c>
        /// resolves to a configured store. Logs and records; never throws.
        /// </summary>
        Task ValidateAsync(CancellationToken ct);
    }

    public record StorageReport
    {
        public required IReadOnlyList<StoreReport> Stores { get; init; }

        /// <summary>
        /// Store ids that rows name and the configuration does not have.
        /// <para>
        /// The one thing that cannot be fixed by restarting: those files are
        /// unreachable until the store comes back under the same id. Empty is the
        /// ordinary state.
        /// </para>
        /// </summary>
        public required IReadOnlyList<string> Unconfigured { get; init; }
    }

    public record StoreReport
    {
        public required string Id { get; init; }
        public required bool Reachable { get; init; }
        public required bool SmokeTestPassed { get; init; }
        public string? Detail { get; init; }

        /// <summary>How many files name this store, and how many bytes they are.</summary>
        public required long Files { get; init; }
        public required long SizeBytes { get; init; }

        /// <summary>Whether new writes go here.</summary>
        public required bool IsDefault { get; init; }
    }

    /// <summary>
    /// Runs the probes, and does not run them too often.
    /// <para>
    /// <b>The smoke test writes a blob</b>, and the container's own healthcheck
    /// polls every ten seconds. Probing on every poll would mean a write, a read
    /// and a delete six times a minute for ever, which is a strange thing for a
    /// health check to do to a bucket somebody pays for. So the answer is cached
    /// for <see cref="Freshness"/> and the store is asked again only when it has
    /// gone stale — §11 says health reports the smoke test, not that it performs
    /// one per request.
    /// </para>
    /// </summary>
    public sealed class StorageHealth(
        IBlobStoreRegistry stores,
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<StorageHealth> logger
    ) : IStorageHealth
    {
        private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(60);

        private readonly SemaphoreSlim gate = new(1, 1);
        private IReadOnlyList<StoreHealth> probed = [];
        private DateTimeOffset probedAt = DateTimeOffset.MinValue;

        public async Task ValidateAsync(CancellationToken ct)
        {
            var unconfigured = await UnconfiguredAsync(ct);
            if (unconfigured.Count == 0) return;

            // **Loud, and not fatal.** Refusing to start would take the whole
            // installation down over files that may be a small and long-forgotten
            // corner of it — everything else still works, and every request for
            // one of these answers 503 with a code that says which problem it is.
            // What must not happen is silence (§11 step 2, A64).
            logger.LogError(
                "Storage is degraded: {Count} store id(s) named by files are not configured: {Ids}",
                unconfigured.Count, string.Join(", ", unconfigured));
        }

        public async Task<string> SummaryAsync(CancellationToken ct)
        {
            if ((await UnconfiguredAsync(ct)).Count > 0) return "degraded";

            var health = await ProbeAsync(ct);
            return health.All(store => store.Ok) ? "ok" : "degraded";
        }

        /// <summary>
        /// Which store ids rows name that the configuration does not have.
        /// <para>
        /// **Asked again rather than remembered from startup.** An operator who
        /// takes a store out of the configuration of a running Server, or who
        /// restores a database from somewhere else, would otherwise be told
        /// everything was fine by a value captured before either happened. One
        /// grouped read, and the answer is what is true now.
        /// </para>
        /// </summary>
        private async Task<IReadOnlyList<string>> UnconfiguredAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var named = await context.Files
                .Select(f => f.StorageId)
                .Distinct()
                .ToListAsync(ct);

            return named.Where(id => stores.Find(id) is null).OrderBy(id => id).ToList();
        }

        public async Task<StorageReport> ReportAsync(CancellationToken ct)
        {
            var health = await ProbeAsync(ct);

            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var counted = await context.Files
                .GroupBy(f => f.StorageId)
                .Select(group => new
                {
                    StorageId = group.Key,
                    Files = group.LongCount(),
                    SizeBytes = group.Sum(f => f.SizeBytes),
                })
                .ToListAsync(ct);

            var byStore = counted.ToDictionary(row => row.StorageId, row => row);

            return new StorageReport
            {
                Unconfigured = await UnconfiguredAsync(ct),
                Stores = health.Select(store => new StoreReport
                {
                    Id = store.StoreId,
                    Reachable = store.Reachable,
                    SmokeTestPassed = store.SmokeTestPassed,
                    Detail = store.Detail,
                    // A count of zero is the signal that a store is safe to
                    // switch off, so the row has to exist even when nothing
                    // names it (§11).
                    Files = byStore.TryGetValue(store.StoreId, out var row) ? row.Files : 0,
                    SizeBytes = row is null ? 0 : row.SizeBytes,
                    IsDefault = store.StoreId == stores.Default.Id,
                }).ToList(),
            };
        }

        private async Task<IReadOnlyList<StoreHealth>> ProbeAsync(CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            if (now - probedAt < Freshness) return probed;

            await gate.WaitAsync(ct);
            try
            {
                // Checked again inside: several pollers arriving together should
                // produce one round of probes, not one each.
                if (clock.GetUtcNow() - probedAt < Freshness) return probed;

                var results = new List<StoreHealth>(stores.All.Count);
                foreach (var store in stores.All) results.Add(await store.CheckHealthAsync(ct));

                probed = results;
                probedAt = clock.GetUtcNow();
                return probed;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

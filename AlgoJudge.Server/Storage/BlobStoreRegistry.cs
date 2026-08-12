using System.Security.Cryptography;
using System.Text;

namespace AlgoJudge.Server.Storage
{
    /// <summary>
    /// The write–read–compare every store answers its health with.
    /// <para>
    /// Shared rather than written three times, so that "the smoke test passed"
    /// means the same thing about a bucket as it does about a volume. A store
    /// that only implemented <c>WriteAsync</c> convincingly would pass a test it
    /// wrote for itself.
    /// </para>
    /// </summary>
    public static class StoreProbe
    {
        private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("algojudge storage probe");

        /// <summary>The checksum of <see cref="Bytes"/>, which is also what gives the probe its path.</summary>
        public static readonly string Sha256 =
            Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

        /// <summary>
        /// Writes a small known blob, reads it back, compares, and removes it.
        /// Cleans up even when the comparison fails — a store that is merely
        /// misconfigured should not also accumulate probes.
        /// </summary>
        public static async Task<bool> RunAsync(IBlobStore store, BlobKey key, CancellationToken ct)
        {
            try
            {
                var written = await store.WriteAsync(key, new MemoryStream(Bytes), ct);
                if (written.Sha256 != Sha256 || written.SizeBytes != Bytes.Length) return false;

                await using var read = await store.OpenReadAsync(key, ct);
                using var buffer = new MemoryStream();
                await read.CopyToAsync(buffer, ct);

                return buffer.ToArray().AsSpan().SequenceEqual(Bytes);
            }
            finally
            {
                try { await store.DeleteAsync(key, ct); } catch { /* the health answer is the point */ }
            }
        }
    }

    /// <summary>
    /// Reads the configured stores out of the environment and hands them out.
    /// <para>
    /// <b>Configuration only, and never the database.</b> A store's credentials
    /// are its whole security boundary, and an API that could set them would make
    /// every administrator's session a way to redirect the product's files
    /// somewhere else. So there is no endpoint, no table, and nothing to migrate
    /// (§3, A20, A21).
    /// </para>
    /// <para>
    /// <b>An unconfigured installation still works.</b> With no
    /// <c>Storage__*</c> at all, this synthesizes exactly what every installation
    /// had before storage became a choice: one <c>postgres</c> store called
    /// <c>pg</c>, which is what the schema migration backfilled every existing
    /// row to. An upgrade that changes nothing changes nothing.
    /// </para>
    /// </summary>
    public sealed class BlobStoreRegistry : IBlobStoreRegistry
    {
        public const string Section = "Storage";
        public const string DefaultSetting = "Storage:Default";
        public const string SpoolPathSetting = "Storage:SpoolPath";

        private readonly Dictionary<string, IBlobStore> stores;

        public BlobStoreRegistry(IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("DbConnectionString") ?? "";
            var spoolPath = configuration[SpoolPathSetting] is { Length: > 0 } configured
                ? configured
                : Path.Combine(Path.GetTempPath(), "algojudge-spool");

            stores = new Dictionary<string, IBlobStore>(StringComparer.Ordinal);

            foreach (var declared in configuration.GetSection($"{Section}:Stores").GetChildren())
            {
                var storeId = declared.Key;
                var kind = declared["Kind"];

                stores[storeId] = kind switch
                {
                    "postgres" => new PostgresBlobStore(storeId, connectionString, spoolPath),
                    null or "" => throw new InvalidOperationException(
                        $"Storage store '{storeId}' does not say what kind it is"),
                    _ => throw new InvalidOperationException(
                        $"Storage store '{storeId}' names a kind this Server does not implement"),
                };
            }

            if (stores.Count == 0)
            {
                stores[Database.Models.File.InitialStorageId] = new PostgresBlobStore(
                    Database.Models.File.InitialStorageId, connectionString, spoolPath);
            }

            var defaultId = configuration[DefaultSetting] is { Length: > 0 } named
                ? named
                : stores.Keys.First();

            // Loudly, and at startup. A default naming a store nobody configured
            // means every upload fails — better a container that refuses to start
            // than one that starts and cannot accept a file.
            Default = stores.TryGetValue(defaultId, out var chosen)
                ? chosen
                : throw new InvalidOperationException(
                    $"{DefaultSetting} names '{defaultId}', which is not a configured store");
        }

        public IBlobStore Default { get; }

        public IBlobStore? Find(string storageId) =>
            stores.TryGetValue(storageId, out var store) ? store : null;

        public IReadOnlyList<IBlobStore> All => stores.Values.ToList();
    }
}

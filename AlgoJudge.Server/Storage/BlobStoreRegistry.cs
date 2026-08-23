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
                var written = await store.WriteAsync(key.FileId, new MemoryStream(Bytes), ct);
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
    /// <b>An unconfigured installation does not start</b> (decided 2026-08-12).
    /// It used to synthesize a <c>postgres</c> store and carry on, which meant a
    /// deployment could take a hundred gigabytes of submissions into its database
    /// without anybody choosing that. Where the files go is now something an
    /// operator says out loud, and the failure to say it is loud in return.
    /// </para>
    /// </summary>
    public sealed class BlobStoreRegistry : IBlobStoreRegistry
    {
        public const string Section = "Storage";
        public const string DefaultSetting = "Storage:Default";
        public const string SpoolPathSetting = "Storage:SpoolPath";

        /// <summary>
        /// What <c>Storage__Default</c> means when nobody said.
        /// <para>
        /// <b>An object store</b> (decided 2026-08-12). An installation that
        /// configures one under another name simply names it; an installation
        /// that configures nothing at all does not start, which is the point.
        /// The other two kinds stay fully supported — <c>postgres</c> is still
        /// the configuration with no dependencies (§10.1) — they are just no
        /// longer what a deployment gets by accident.
        /// </para>
        /// </summary>
        public const string DefaultStoreId = "objects";

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
                    "filesystem" => new FilesystemBlobStore(storeId, Required(declared, "Path", storeId)),
                    "s3" => new S3BlobStore(storeId, new S3StoreOptions
                    {
                        Endpoint = Required(declared, "Endpoint", storeId),
                        Bucket = Required(declared, "Bucket", storeId),
                        AccessKey = Required(declared, "AccessKey", storeId),
                        SecretKey = Required(declared, "SecretKey", storeId),
                        Region = declared["Region"] is { Length: > 0 } region ? region : "us-east-1",
                        CreateBucket = declared.GetValue("CreateBucket", false),
                        // Seconds rather than a `TimeSpan`, so a deployment says
                        // `TimeoutSeconds: 600` instead of a format nobody
                        // remembers the shape of.
                        Timeout = TimeSpan.FromSeconds(declared.GetValue("TimeoutSeconds", 600)),
                        MaxErrorRetry = declared.GetValue("MaxErrorRetry", 2),
                    }, spoolPath),
                    null or "" => throw new InvalidOperationException(
                        $"Storage store '{storeId}' does not say what kind it is"),
                    _ => throw new InvalidOperationException(
                        $"Storage store '{storeId}' names a kind this Server does not implement"),
                };
            }

            // **An installation that says nothing does not start** (decided
            // 2026-08-12). Where the files of a product go is not a thing to
            // inherit from a default nobody read: the earlier behaviour — one
            // synthesized `postgres` store — meant a deployment could accept a
            // hundred gigabytes of submissions into its database without anyone
            // ever choosing that.
            if (stores.Count == 0)
            {
                throw new InvalidOperationException(
                    "No storage is configured. Set Storage__Stores__<id>__Kind and "
                    + $"{DefaultSetting}; see docs/specs/FILE_STORAGE.md §3.");
            }

            var defaultId = configuration[DefaultSetting] is { Length: > 0 } named
                ? named
                : DefaultStoreId;

            // Loudly, and at startup. A default naming a store nobody configured
            // means every upload fails — better a container that refuses to start
            // than one that starts and cannot accept a file.
            Default = stores.TryGetValue(defaultId, out var chosen)
                ? chosen
                : throw new InvalidOperationException(
                    $"{DefaultSetting} names '{defaultId}', which is not a configured store");
        }

        /// <summary>
        /// A setting a store cannot work without.
        /// <para>
        /// Refused at startup rather than discovered at the first upload: a
        /// <c>filesystem</c> store with no <c>Path</c> would otherwise write
        /// somewhere relative to the working directory, which is a place nobody
        /// chose and no backup covers.
        /// </para>
        /// </summary>
        private static string Required(IConfigurationSection store, string key, string storeId) =>
            store[key] is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException(
                    $"Storage store '{storeId}' needs a {key}");

        public IBlobStore Default { get; }

        public IBlobStore? Find(string storageId) =>
            stores.TryGetValue(storageId, out var store) ? store : null;

        public IReadOnlyList<IBlobStore> All => stores.Values.ToList();
    }
}

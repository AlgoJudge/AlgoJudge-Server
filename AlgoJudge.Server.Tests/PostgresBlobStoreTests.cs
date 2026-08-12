using AlgoJudge.Server.Database;
using AlgoJudge.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The contract, against bytes in the database.
/// <para>
/// Its own container and its own schema, because the contract writes blobs with
/// no <c>File</c> row behind them — which is legal (§7 writes the bytes first)
/// but would leave the shared suite's database full of things its other tests
/// would have to learn to ignore.
/// </para>
/// </summary>
[Collection("storage")]
public sealed class PostgresBlobStoreTests : BlobStoreContract, IAsyncLifetime
{
    private PostgreSqlContainer container = null!;
    private PostgresBlobStore store = null!;

    protected override IBlobStore Store => store;

    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder()
            .WithImage("postgres:18")
            .WithDatabase("algojudge")
            .WithUsername("algojudge")
            .WithPassword("test")
            .Build();

        await container.StartAsync();

        // The real schema from the real migrations. A hand-written CREATE TABLE
        // here would be a second definition of the one thing this store depends
        // on, and the two would drift.
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(container.GetConnectionString()).Options);
        await context.Database.MigrateAsync();

        store = new PostgresBlobStore(
            "pg", container.GetConnectionString(), Path.Combine(Path.GetTempPath(), "algojudge-test-spool"));
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    /// <summary>
    /// The spool is a means, not a place things live.
    /// <para>
    /// This backend has to stage the bytes on disk to learn their length before
    /// Npgsql will stream them. A spool file left behind would be a 128 MiB leak
    /// per upload on the busiest day of the year — and one nothing would attribute
    /// to the database backend.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Nothing_is_left_in_the_spool_directory()
    {
        var spool = Path.Combine(Path.GetTempPath(), "algojudge-spool-isolated");
        if (Directory.Exists(spool)) Directory.Delete(spool, recursive: true);

        var isolated = new PostgresBlobStore("pg", container.GetConnectionString(), spool);
        var bytes = new byte[100_000];
        Random.Shared.NextBytes(bytes);
        var sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        await isolated.WriteAsync(
            Guid.NewGuid(), new MemoryStream(bytes), CancellationToken.None);

        Assert.Empty(Directory.GetFiles(spool));
    }
}

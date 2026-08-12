using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The migrations that move where a file's bytes live, run against rows that
/// already existed.
/// <para>
/// <b>Its own database, deliberately.</b> Every other test in this suite starts
/// from a schema that is already fully migrated, which is exactly the state that
/// cannot answer "what happened to the rows that were there before". These tests
/// migrate <b>up to the migration before</b>, put bytes in the old column, and
/// then migrate the rest of the way — so what is asserted is the real migration
/// against real data, not a copy of its SQL.
/// </para>
/// <para>
/// It costs one extra container per run. That is worth paying for once: this is
/// the migration that moves everybody's bytes, and it gets exactly one chance to
/// be right on an installation that has some.
/// </para>
/// </summary>
public sealed class FileStorageMigrationTests : IAsyncLifetime
{
    /// <summary>The last migration before storage became a choice.</summary>
    private const string Before = "20260811204856_InstanceShowLocalSignIn";

    private PostgreSqlContainer container = null!;
    private string connectionString = "";

    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder()
            .WithImage("postgres:18")
            .WithDatabase("algojudge")
            .WithUsername("algojudge")
            .WithPassword("test")
            .Build();

        await container.StartAsync();
        connectionString = container.GetConnectionString();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString).Options);

    [Fact]
    public async Task Bytes_that_were_already_stored_are_copied_and_keep_their_store()
    {
        var fileId = Guid.NewGuid();
        var bytes = new byte[] { 0x41, 0x4C, 0x47, 0x4F, 0x00, 0xFF };

        await MigrateToAsync(Before);
        await InsertOldStyleFileAsync(fileId, bytes);
        await MigrateToLatestAsync();

        // The bytes arrived in the new table, unchanged. `bytea` compares by
        // value, so this is the whole assertion about the copy.
        var copied = await ScalarAsync<byte[]>(
            @"SELECT ""Content"" FROM ""FileContents"" WHERE ""FileId"" = @id", ("id", fileId));
        Assert.Equal(bytes, copied);

        // And the row says where they are. An empty string here — which is what
        // EF scaffolds for a non-nullable string — would name a store that does
        // not exist, and the startup check would refuse to bring the Server up.
        var storageId = await ScalarAsync<string>(
            @"SELECT ""StorageId"" FROM ""Files"" WHERE ""Id"" = @id", ("id", fileId));
        Assert.Equal("pg", storageId);

        // Outside a migration both of these are empty. A row that arrived
        // claiming to be mid-move would be read from the wrong place.
        Assert.True(await ScalarAsync<bool>(
            @"SELECT ""PreviousStorageId"" IS NULL AND ""PreviousCopyDeleteAfter"" IS NULL
              FROM ""Files"" WHERE ""Id"" = @id", ("id", fileId)));
    }

    [Fact]
    public async Task A_later_insert_has_to_say_where_it_put_the_bytes()
    {
        await MigrateToLatestAsync();

        // The default exists only to backfill, and is dropped immediately after.
        // Left in place it would let a writer that has never heard of stores
        // insert a row claiming `pg`, and the bytes would not be there.
        var hasDefault = await ScalarAsync<bool>(
            @"SELECT column_default IS NOT NULL FROM information_schema.columns
              WHERE table_name = 'Files' AND column_name = 'StorageId'");
        Assert.False(hasDefault);
    }

    [Fact]
    public async Task The_bytes_are_stored_out_of_line_and_uncompressed()
    {
        await MigrateToLatestAsync();

        // 'e' is EXTERNAL: out of line, **not compressed**. Uncompressed is the
        // half that matters — a ranged read is `substring(Content from X for Y)`,
        // and PostgreSQL can only seek into a TOASTed value it did not compress.
        // Under the default 'x' (EXTENDED), serving `Range:` on a 128 MiB package
        // would decompress from the beginning on every request.
        var storage = await ScalarAsync<char>(
            @"SELECT a.attstorage FROM pg_attribute a
              JOIN pg_class c ON c.oid = a.attrelid
              WHERE c.relname = 'FileContents' AND a.attname = 'Content'");
        Assert.Equal('e', storage);
    }

    [Fact]
    public async Task Bytes_can_be_written_before_the_row_that_names_them()
    {
        await MigrateToLatestAsync();

        // The upload path writes the bytes and only then commits the `File` row,
        // so that a crash leaves bytes nobody points at rather than a row
        // pointing at nothing (§2, invariant 3). A foreign key here — which §6 of
        // the spec draws, with ON DELETE CASCADE — would make that write fail
        // against a row that does not exist yet.
        //
        // Asserted as schema rather than as behaviour because re-adding the key
        // looks like tightening and would break every upload on this backend.
        var keys = await ScalarAsync<long>(
            @"SELECT count(*) FROM information_schema.table_constraints
              WHERE table_name = 'FileContents' AND constraint_type = 'FOREIGN KEY'");
        Assert.Equal(0, keys);

        // Proving it rather than reasoning about it: an id no `Files` row has.
        await ExecuteAsync(
            @"INSERT INTO ""FileContents"" (""FileId"", ""Content"") VALUES (@id, @content)",
            ("id", Guid.NewGuid()), ("content", new byte[] { 0x01 }));
    }

    // ── Getting the database into the state a real installation is in ────────

    private async Task MigrateToAsync(string migration)
    {
        await using var context = NewContext();
        await context.Database.GetService<IMigrator>().MigrateAsync(migration);
    }

    private async Task MigrateToLatestAsync()
    {
        await using var context = NewContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// A file as it was written before storage became a choice: bytes in
    /// <c>Files.Content</c>, and no other column knowing anything about them.
    /// Raw SQL because the entity no longer describes that shape — which is the
    /// point of the test.
    /// </summary>
    private Task InsertOldStyleFileAsync(Guid fileId, byte[] bytes) =>
        ExecuteAsync(
            @"INSERT INTO ""Files"" (""Id"", ""Name"", ""MimeType"", ""Content"", ""SizeBytes"", ""Sha256"", ""CreatedAt"")
              VALUES (@id, 'main.cpp', 'text/plain', @content, @size, @sha, now() at time zone 'utc')",
            ("id", fileId),
            ("content", bytes),
            ("size", (long)bytes.Length),
            ("sha", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()));

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

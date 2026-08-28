using Npgsql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Three properties of the file-storage schema that no entity states, asserted
/// against the database the migration actually built.
/// <para>
/// <b>This is the guard on hand-written SQL.</b> <c>FileContents</c> is not an
/// EF entity, so it is created by a <c>migrationBuilder.Sql</c> block at the end
/// of <c>InitialCreate</c> — the one part of that file the model does not
/// generate, and therefore the one part a regeneration would silently drop.
/// Everything below fails if it goes.
/// </para>
/// <para>
/// <b>Schema rather than behaviour</b>, deliberately: re-adding the foreign key
/// or leaving the default in place looks like tightening, and the upload path
/// would only say so at run time, on the backend fewest deployments use.
/// </para>
/// <para>
/// It had its own container until the migrations were squashed on 2026-08-28,
/// because two of its tests migrated to a named migration and back. Those two
/// tested a transition that no longer exists; what is left needs a migrated
/// database and nothing else, so it takes the shared one.
/// </para>
/// </summary>
[Collection("server-2")]
public class FileStorageSchemaTests(ServerFixture server)
{
    /// <summary>Starts the shared host, so the schema being read exists.</summary>
    private async Task ReadyAsync()
    {
        using var warm = server.CreateClient();
        (await warm.GetAsync("/api/v1/health")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task An_insert_has_to_say_where_it_put_the_bytes()
    {
        await ReadyAsync();

        // A default here would let a writer that has never heard of stores
        // insert a row claiming `pg`, with the bytes somewhere else entirely.
        var hasDefault = await ScalarAsync<bool>(
            """
            SELECT column_default IS NOT NULL FROM information_schema.columns
            WHERE table_name = 'Files' AND column_name = 'StorageId'
            """);
        Assert.False(hasDefault);
    }

    [Fact]
    public async Task The_bytes_are_stored_out_of_line_and_uncompressed()
    {
        await ReadyAsync();

        // 'e' is EXTERNAL: out of line, **not compressed**. Uncompressed is the
        // half that matters — a ranged read is `substring(Content from X for Y)`,
        // and PostgreSQL can only seek into a TOASTed value it did not compress.
        // Under the default 'x' (EXTENDED), serving `Range:` on a 128 MiB package
        // would decompress from the beginning on every request.
        var storage = await ScalarAsync<char>(
            """
            SELECT a.attstorage FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.relname = 'FileContents' AND a.attname = 'Content'
            """);
        Assert.Equal('e', storage);
    }

    [Fact]
    public async Task Bytes_can_be_written_before_the_row_that_names_them()
    {
        await ReadyAsync();

        // The upload path writes the bytes and only then commits the `File` row,
        // so a crash leaves bytes nobody points at rather than a row pointing at
        // nothing (FILE_STORAGE.md §2, invariant 3). A foreign key here — which
        // §6 of the spec draws, with ON DELETE CASCADE — would make that write
        // fail against a row that does not exist yet.
        var keys = await ScalarAsync<long>(
            """
            SELECT count(*) FROM information_schema.table_constraints
            WHERE table_name = 'FileContents' AND constraint_type = 'FOREIGN KEY'
            """);
        Assert.Equal(0, keys);

        // Proving it rather than reasoning about it: an id no `Files` row has.
        await ExecuteAsync(
            """INSERT INTO "FileContents" ("FileId", "Content") VALUES (@id, @content)""",
            ("id", Guid.NewGuid()), ("content", new byte[] { 0x01 }));
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(server.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(server.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

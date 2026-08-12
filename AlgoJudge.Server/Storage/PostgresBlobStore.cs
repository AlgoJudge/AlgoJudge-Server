using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace AlgoJudge.Server.Storage
{
    /// <summary>
    /// Bytes in the database, in <c>FileContents.Content</c>.
    /// <para>
    /// <b>The configuration with no dependencies</b>, and that is what it is for:
    /// one container, and <c>pg_dump</c> alone is a complete backup. It is the
    /// only backend where "did the backup include the files" cannot be answered
    /// wrongly. It stays supported rather than becoming the thing that still
    /// compiles (§10.1).
    /// </para>
    /// <para>
    /// <b>No EF Core anywhere below this line.</b> Two independent reasons, both
    /// fatal: EF cannot stream a <c>byte[]</c>, and change tracking would take a
    /// snapshot of every file it touched. A store built on it would break the one
    /// invariant the whole design exists to keep.
    /// </para>
    /// </summary>
    public sealed class PostgresBlobStore(
        string id, string connectionString, string spoolPath) : IBlobStore
    {
        public string Id => id;

        /// <summary>
        /// Writes the bytes, hashing them on the way past.
        /// <para>
        /// <b>Spooled to a file first, and not for want of trying otherwise.</b>
        /// Npgsql will take a <c>Stream</c> as a <c>bytea</c> parameter, but only
        /// one it can seek or whose <c>Size</c> it is told up front — and a
        /// multipart section arriving from a socket is neither. The bytes have to
        /// rest somewhere while their length becomes known, and the only place
        /// that is not memory is disk.
        /// </para>
        /// <para>
        /// So the temporary name of §5.2 is a file here, and the move into place
        /// is the <c>INSERT</c>: nothing is visible under the final key until the
        /// whole blob is on disk and its length is known.
        /// </para>
        /// </summary>
        public async Task<BlobWriteResult> WriteAsync(
            Guid fileId, Stream content, CancellationToken ct)
        {
            Directory.CreateDirectory(spoolPath);
            var spool = Path.Combine(spoolPath, $"{Guid.NewGuid():N}.blob");

            try
            {
                string sha256;
                long size;

                await using (var hashing = new HashingStream(content))
                await using (var temp = new FileStream(
                    spool, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await hashing.CopyToAsync(temp, ct);
                    sha256 = hashing.Sha256;
                    size = hashing.BytesRead;
                }

                await using (var stored = new FileStream(
                    spool, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await using var connection = await OpenAsync(ct);
                    await using var command = new NpgsqlCommand(
                        """
                        INSERT INTO "FileContents" ("FileId", "Content")
                        VALUES (@id, @content)
                        ON CONFLICT ("FileId") DO UPDATE SET "Content" = EXCLUDED."Content"
                        """, connection);

                    command.Parameters.AddWithValue("id", fileId);
                    // The stream is seekable now, which is the whole point of the
                    // spool: Npgsql reads its length and streams it to the server
                    // without ever holding it.
                    command.Parameters.Add(new NpgsqlParameter("content", NpgsqlDbType.Bytea)
                    {
                        Value = stored,
                    });

                    await command.ExecuteNonQueryAsync(ct);
                }

                return new BlobWriteResult { Sha256 = sha256, SizeBytes = size };
            }
            finally
            {
                // The spool file is ours and nothing else knows its name, so
                // losing one to a crash costs disk rather than correctness.
                if (File.Exists(spool)) File.Delete(spool);
            }
        }

        public Task<Stream> OpenReadAsync(BlobKey key, CancellationToken ct) =>
            OpenReadAsync(key, 0, null, ct);

        /// <summary>
        /// A window onto the bytes, read as a stream rather than as a value.
        /// <para>
        /// <c>SequentialAccess</c> plus <c>GetStream()</c> is what keeps a 128 MiB
        /// package from arriving as a 128 MiB array: without it Npgsql
        /// materializes the whole column before the first byte reaches the caller.
        /// </para>
        /// <para>
        /// <c>substring</c> is <b>1-based</b> and the offset here is not, which is
        /// the kind of difference that produces a file short by one byte at the
        /// front and a checksum that never matches.
        /// </para>
        /// </summary>
        public async Task<Stream> OpenReadAsync(
            BlobKey key, long offset, long? length, CancellationToken ct)
        {
            var connection = await OpenAsync(ct);
            NpgsqlDataReader? reader = null;

            try
            {
                // **The casts are not decoration.** `substring` on a `bytea` is
                // declared over `integer`, and a .NET `long` parameter arrives as
                // `bigint` — which PostgreSQL will not narrow on its own, so
                // without these every read fails with "function substring(bytea,
                // bigint) does not exist". `int` is enough by construction: a
                // `bytea` cannot exceed 1 GB, and the largest thing this product
                // stores is 128 MiB.
                var sql = length is null
                    ? """SELECT substring("Content" from @from::int) FROM "FileContents" WHERE "FileId" = @id"""
                    : """SELECT substring("Content" from @from::int for @take::int) FROM "FileContents" WHERE "FileId" = @id""";

                var command = new NpgsqlCommand(sql, connection);
                command.Parameters.AddWithValue("id", key.FileId);
                command.Parameters.AddWithValue("from", offset + 1);
                if (length is { } take) command.Parameters.AddWithValue("take", take);

                reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

                if (!await reader.ReadAsync(ct)) throw new BlobMissingException(key);

                // The reader and the connection have to outlive this method: the
                // stream is still reading through them. They are handed to the
                // wrapper, which closes all three together.
                return new ReaderOwnedStream(reader.GetStream(0), reader, connection);
            }
            catch
            {
                if (reader is not null) await reader.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task<bool> ExistsAsync(BlobKey key, CancellationToken ct)
        {
            await using var connection = await OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                """SELECT 1 FROM "FileContents" WHERE "FileId" = @id""", connection);
            command.Parameters.AddWithValue("id", key.FileId);
            return await command.ExecuteScalarAsync(ct) is not null;
        }

        /// <summary>Idempotent: deleting bytes that are already gone is a success.</summary>
        public async Task DeleteAsync(BlobKey key, CancellationToken ct)
        {
            await using var connection = await OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                """DELETE FROM "FileContents" WHERE "FileId" = @id""", connection);
            command.Parameters.AddWithValue("id", key.FileId);
            await command.ExecuteNonQueryAsync(ct);
        }

        public Task<BlobDelivery> PrepareDeliveryAsync(BlobKey key, CancellationToken ct) =>
            Task.FromResult(new BlobDelivery { Kind = BlobDeliveryKind.StreamFromServer });

        public async Task<StoreHealth> CheckHealthAsync(CancellationToken ct)
        {
            var probe = new BlobKey(Guid.NewGuid(), StoreProbe.Sha256);

            try
            {
                await using var connection = await OpenAsync(ct);
                // Reachability first and separately, so "the database is down"
                // and "the write path is broken" are not the same answer.
                await using (var ping = new NpgsqlCommand("SELECT 1", connection))
                {
                    await ping.ExecuteScalarAsync(ct);
                }

                var passed = await StoreProbe.RunAsync(this, probe, ct);
                return new StoreHealth
                {
                    StoreId = Id, Reachable = true, SmokeTestPassed = passed,
                    Detail = passed ? null : "the write, read and compare did not agree",
                };
            }
            catch (Exception e)
            {
                return new StoreHealth
                {
                    StoreId = Id, Reachable = false, SmokeTestPassed = false, Detail = e.Message,
                };
            }
        }

        private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection;
        }

        /// <summary>
        /// A blob's bytes, plus the reader and connection they are still arriving
        /// through. Closing the stream closes all three, in that order.
        /// </summary>
        private sealed class ReaderOwnedStream(
            Stream inner, NpgsqlDataReader reader, NpgsqlConnection connection) : Stream
        {
            public override int Read(byte[] buffer, int offset, int count) =>
                inner.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                inner.ReadAsync(buffer, ct);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                inner.ReadAsync(buffer, offset, count, ct);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    reader.Dispose();
                    connection.Dispose();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                await reader.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The row said the bytes were here and they are not.
    /// <para>
    /// Distinct from "this store is not configured", which is a <c>503</c> about
    /// the installation. This one is a fault: something deleted a blob a row
    /// still points at, which is the inconsistency the ordering in §7 exists to
    /// make unreachable.
    /// </para>
    /// </summary>
    public class BlobMissingException(BlobKey key)
        : Exception($"The bytes of file {key.FileId} are not in the store that claims them");
}

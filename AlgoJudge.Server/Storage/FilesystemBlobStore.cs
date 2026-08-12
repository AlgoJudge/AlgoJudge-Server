namespace AlgoJudge.Server.Storage
{
    /// <summary>
    /// Bytes on a volume, laid out as <c>ab/cd/ef/&lt;fileId&gt;</c>.
    /// <para>
    /// The layout is the Runner's cache layout, so one description in
    /// FILE_INTEGRITY.md covers both, and three levels of fan-out keep every
    /// directory small — a flat directory of a hundred thousand files is slow to
    /// list on every filesystem worth naming.
    /// </para>
    /// <para>
    /// <b>Encryption is the operator's, not this class's.</b> For a self-hosted
    /// deployment the right control is the volume (LUKS/dm-crypt), which covers
    /// the PostgreSQL data directory and any local object store at the same time.
    /// Encrypting here would protect one of the three and complicate all of them
    /// (§10.2, A56).
    /// </para>
    /// </summary>
    public sealed class FilesystemBlobStore(string id, string root) : IBlobStore
    {
        public string Id => id;

        /// <summary>
        /// <b>Under the store's own root, and that is load-bearing.</b>
        /// <c>File.Move</c> is atomic only within a volume; a temporary directory
        /// elsewhere on the machine turns the move into a copy that can be
        /// interrupted, and a half-written blob would then be visible under its
        /// final name — the one thing §5.2 forbids.
        /// </summary>
        private string Spool => Path.Combine(root, "tmp");

        public async Task<BlobWriteResult> WriteAsync(
            Guid fileId, Stream content, CancellationToken ct)
        {
            Directory.CreateDirectory(Spool);
            var temp = Path.Combine(Spool, $"{Guid.NewGuid():N}.blob");

            try
            {
                string sha256;
                long size;

                await using (var hashing = new HashingStream(content))
                await using (var writing = new FileStream(
                    temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await hashing.CopyToAsync(writing, ct);
                    sha256 = hashing.Sha256;
                    size = hashing.BytesRead;
                }

                // Only now is the name it will be found under created. Everything
                // before this point is a file nobody can ask for.
                var destination = PathOf(new BlobKey(fileId, sha256));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(temp, destination, overwrite: true);

                return new BlobWriteResult { Sha256 = sha256, SizeBytes = size };
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        public Task<Stream> OpenReadAsync(BlobKey key, CancellationToken ct) =>
            OpenReadAsync(key, 0, null, ct);

        public Task<Stream> OpenReadAsync(
            BlobKey key, long offset, long? length, CancellationToken ct)
        {
            var path = PathOf(key);
            if (!File.Exists(path)) throw new BlobMissingException(key);

            var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);

            try
            {
                if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
                return Task.FromResult<Stream>(
                    length is { } take ? new BoundedStream(stream, take) : stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        public Task<bool> ExistsAsync(BlobKey key, CancellationToken ct) =>
            Task.FromResult(File.Exists(PathOf(key)));

        /// <summary>
        /// Idempotent, and it leaves the directories.
        /// <para>
        /// Pruning empty fan-out directories would be a second thing to get right
        /// under concurrency — a directory removed between another writer's
        /// <c>CreateDirectory</c> and its <c>Move</c> — to reclaim a few hundred
        /// bytes each. Three levels of two hex characters is at most 16 million
        /// directories and in practice far fewer, because they are only created
        /// where a file landed.
        /// </para>
        /// </summary>
        public Task DeleteAsync(BlobKey key, CancellationToken ct)
        {
            var path = PathOf(key);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }

        public Task<BlobDelivery> PrepareDeliveryAsync(BlobKey key, CancellationToken ct) =>
            Task.FromResult(new BlobDelivery { Kind = BlobDeliveryKind.StreamFromServer });

        public async Task<StoreHealth> CheckHealthAsync(CancellationToken ct)
        {
            try
            {
                // Reachability for a volume is "it is there and I may write to
                // it", which is a different failure from "the write path is
                // broken" — a read-only mount and a bug should not read alike.
                Directory.CreateDirectory(Spool);

                var passed = await StoreProbe.RunAsync(
                    this, new BlobKey(Guid.NewGuid(), StoreProbe.Sha256), ct);

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

        private string PathOf(BlobKey key) =>
            Path.Combine(root, key.Path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The first <c>length</c> bytes of whatever it wraps, and then the end.
    /// <para>
    /// Distinct from <see cref="LimitedStream"/>, which <b>throws</b> past its
    /// ceiling: that one guards an upload against a caller sending more than it
    /// may, this one serves a window somebody asked for. Confusing the two would
    /// turn a perfectly ordinary range request into a <c>413</c>.
    /// </para>
    /// </summary>
    internal sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private long served;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            var room = length - served;
            if (room <= 0) return 0;

            var read = await inner.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, room)], ct);
            served += read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var room = length - served;
            if (room <= 0) return 0;

            var read = inner.Read(buffer, offset, (int)Math.Min(count, room));
            served += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => served;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

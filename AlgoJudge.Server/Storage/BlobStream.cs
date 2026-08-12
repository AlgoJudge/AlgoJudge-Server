namespace AlgoJudge.Server.Storage
{
    /// <summary>
    /// A seekable view over a blob whose backend cannot seek.
    /// <para>
    /// <b>This is the whole reason <c>Range:</c> works.</b> ASP.NET Core serves
    /// <c>206</c> only from a stream whose <c>CanSeek</c> is true — it asks for
    /// the length, seeks to the offset and copies a bounded count. Neither
    /// <c>NpgsqlDataReader.GetStream()</c> nor an S3 <c>GetObject</c> body can do
    /// that, so without this the framework silently falls back to <c>200</c> and
    /// the whole file: no error, no log line, and a resumable download that
    /// quietly is not one.
    /// </para>
    /// <para>
    /// The trick is that seeking costs nothing until somebody reads. A seek
    /// throws away the open reader and remembers a number; the next read opens a
    /// fresh ranged read at that number. So MVC's seek-then-copy becomes exactly
    /// one ranged request to the backend, which is what both backends are good at.
    /// </para>
    /// <para>
    /// The length comes from <c>File.SizeBytes</c>, which is recorded from what
    /// the Server itself counted while storing the bytes — not from anything the
    /// backend is asked at read time, and not from anything a caller declared.
    /// </para>
    /// </summary>
    public sealed class BlobStream(
        Func<long, long?, CancellationToken, Task<Stream>> openRange,
        long length
    ) : Stream
    {
        private Stream? open;
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            if (target < 0) throw new IOException("Cannot seek before the beginning of the blob");

            // Only pay for it if it actually moves. MVC seeks to 0 on a request
            // with no Range header, and reopening the backend for that would
            // double every plain download.
            if (target != position)
            {
                open?.Dispose();
                open = null;
                position = target;
            }

            return position;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            if (position >= length) return 0;

            open ??= await openRange(position, null, ct);

            var read = await open.ReadAsync(buffer, ct);
            position += read;
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        /// <summary>
        /// <para>
        /// Blocking on the asynchronous path, which is safe here and nowhere by
        /// luck: ASP.NET Core has no synchronization context, so there is no
        /// context to deadlock against. It exists because <c>Stream</c> demands
        /// it — everything in this product's own paths reads asynchronously, and
        /// <c>AllowSynchronousIO</c> is off, so in practice this is reached only
        /// by something outside our code that insists on the synchronous API.
        /// </para>
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) open?.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (open is not null) await open.DisposeAsync();
            open = null;
            await base.DisposeAsync();
        }
    }
}

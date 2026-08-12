using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Storage
{
    /// <summary>
    /// Passes bytes through and stops when there have been too many.
    /// <para>
    /// <b>Counted, not declared.</b> A limit enforced from <c>Content-Length</c>
    /// is a limit enforced against a number the caller chose, and a caller
    /// sending more than it announced is the case the limit exists for. This
    /// counts what actually arrives.
    /// </para>
    /// <para>
    /// It throws rather than returning short. A short read looks like the end of
    /// a complete file to everything downstream, so an oversized upload would be
    /// stored as a truncated one — accepted, checksummed against its own
    /// truncation, and wrong.
    /// </para>
    /// </summary>
    public sealed class LimitedStream(Stream inner, long maxBytes) : Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            Counted(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Counted(await inner.ReadAsync(buffer, ct));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Counted(int read)
        {
            if (read <= 0) return read;

            BytesRead += read;
            // Checked after the chunk, so at most one buffer's worth is read past
            // the ceiling — the price of not trusting a declared length.
            if (BytesRead > maxBytes) throw new PayloadTooLargeException(maxBytes);

            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

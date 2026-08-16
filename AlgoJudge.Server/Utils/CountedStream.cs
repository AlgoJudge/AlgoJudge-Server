namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// A stream that stops at a ceiling.
    /// <para>
    /// <b>Counted while reading, never taken from a header.</b>
    /// <c>Content-Length</c> is something the sender says, and a sender that
    /// wanted to fill this disk would say whatever got past the check.
    /// </para>
    /// </summary>
    public sealed class CountedStream(Stream inner, long ceiling) : Stream
    {
        private long read;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            var got = await inner.ReadAsync(buffer, ct);
            Count(got);
            return got;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var got = inner.Read(buffer, offset, count);
            Count(got);
            return got;
        }

        private void Count(int got)
        {
            read += got;
            if (read > ceiling)
            {
                throw new ValidationException(
                    $"That document is larger than {ceiling} bytes", "fetch.tooLarge");
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => read;
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
    }
}

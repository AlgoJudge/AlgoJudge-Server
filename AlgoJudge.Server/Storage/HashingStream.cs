using System.Security.Cryptography;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Storage
{
    /// <summary>
    /// Passes bytes through, hashing and counting them as they go.
    /// <para>
    /// <b>This is what makes "one pass" a property of the interface</b> rather
    /// than something three store implementations each remember to do. A store
    /// reads its source through one of these, and the checksum is a by-product of
    /// having moved the bytes at all — never a second read, and never a buffer
    /// held so that the bytes can be hashed afterwards.
    /// </para>
    /// <para>
    /// The ceiling is here for the same reason: this is the one object that knows
    /// how many bytes have actually gone past. A limit checked from the declared
    /// <c>Content-Length</c> is a limit checked against a number the caller chose.
    /// </para>
    /// </summary>
    public sealed class HashingStream(Stream inner, long? maxBytes = null) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        /// <summary>How many bytes have been read through this so far.</summary>
        public long BytesRead { get; private set; }

        /// <summary>
        /// Lowercase hexadecimal SHA-256 of everything read so far. Safe to ask
        /// more than once — the hash is not reset by looking at it.
        /// </summary>
        public string Sha256 => Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();

        public override int Read(byte[] buffer, int offset, int count) =>
            Absorb(inner.Read(buffer, offset, count), buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            return Absorb(read, buffer);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await inner.ReadAsync(buffer, ct);
            return Absorb(read, buffer.Span);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        /// <summary>
        /// Hash it, count it, and stop if it is more than was allowed.
        /// <para>
        /// The ceiling is checked after each chunk rather than before, so at most
        /// one buffer's worth is read past the limit — which is the price of not
        /// trusting a declared length. Throwing here rather than returning short
        /// is deliberate: a short read looks like a complete file to everything
        /// downstream, and would store a truncated upload as a valid one.
        /// </para>
        /// </summary>
        private int Absorb(int read, ReadOnlySpan<byte> buffer)
        {
            if (read <= 0) return read;

            hash.AppendData(buffer[..read]);
            BytesRead += read;

            if (maxBytes is { } limit && BytesRead > limit) throw new PayloadTooLargeException(limit);

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

        /// <summary>
        /// <b>Does not dispose the source.</b> The source is somebody else's —
        /// a multipart section belonging to the request, or a store's own reader —
        /// and closing it from here would end a request body that the caller is
        /// still walking through.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) hash.Dispose();
            base.Dispose(disposing);
        }
    }
}

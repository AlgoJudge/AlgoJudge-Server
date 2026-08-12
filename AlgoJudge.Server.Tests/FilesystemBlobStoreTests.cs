using System.Security.Cryptography;
using AlgoJudge.Server.Storage;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The same contract, against a volume.
/// <para>
/// It runs the identical assertions the database store runs, which is the whole
/// point: nothing above <c>IBlobStore</c> may behave differently depending on
/// which of them is configured (§2, invariant 5).
/// </para>
/// </summary>
public sealed class FilesystemBlobStoreTests : BlobStoreContract, IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"algojudge-fs-{Guid.NewGuid():N}");

    private readonly FilesystemBlobStore store;

    protected override IBlobStore Store => store;

    public FilesystemBlobStoreTests()
    {
        store = new FilesystemBlobStore("local", root);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// The layout is not an implementation detail: it is the Runner's cache
    /// layout too, so one description covers both. Three levels of fan-out keep
    /// every directory small — a flat one of a hundred thousand files is slow to
    /// list on every filesystem worth naming.
    /// </summary>
    [Fact]
    public async Task A_blob_lands_where_its_checksum_says()
    {
        var bytes = new byte[64];
        Random.Shared.NextBytes(bytes);
        var fileId = Guid.NewGuid();

        var written = await store.WriteAsync(fileId, new MemoryStream(bytes), CancellationToken.None);

        var sha256 = written.Sha256;
        var expected = Path.Combine(
            root, sha256[..2], sha256[2..4], sha256[4..6], fileId.ToString("D"));

        Assert.True(File.Exists(expected), $"expected a blob at {expected}");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(expected));
    }

    /// <summary>
    /// <para>
    /// The staging directory has to be <b>under the store's own root</b>:
    /// <c>File.Move</c> is atomic within a volume and a copy across one, and a
    /// copy can be interrupted — which would leave a half-written blob visible
    /// under its final name, the one thing §5.2 forbids.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Staging_happens_on_the_same_volume_and_leaves_nothing()
    {
        var bytes = new byte[200_000];
        Random.Shared.NextBytes(bytes);

        await store.WriteAsync(Guid.NewGuid(), new MemoryStream(bytes), CancellationToken.None);

        var spool = Path.Combine(root, "tmp");
        Assert.True(Directory.Exists(spool), "the staging directory is under the store's root");
        Assert.Empty(Directory.GetFiles(spool));
    }

    /// <summary>
    /// A blob is only ever visible whole.
    /// <para>
    /// Checked by watching the destination while a slow write is in flight: the
    /// bytes arrive over several reads, and at no point may the final name exist
    /// holding fewer than all of them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_half_written_blob_is_never_visible_under_its_final_name()
    {
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var fileId = Guid.NewGuid();

        var destination = Path.Combine(
            root, sha256[..2], sha256[2..4], sha256[4..6], fileId.ToString("D"));

        var sizes = new List<long>();
        var watching = true;
        var watcher = Task.Run(async () =>
        {
            while (Volatile.Read(ref watching))
            {
                if (File.Exists(destination))
                {
                    try { sizes.Add(new FileInfo(destination).Length); } catch { /* mid-move */ }
                }
                await Task.Delay(1);
            }
        });

        await store.WriteAsync(fileId, new SlowStream(bytes), CancellationToken.None);
        Volatile.Write(ref watching, false);
        await watcher;

        Assert.True(File.Exists(destination));
        Assert.All(sizes, seen => Assert.Equal(bytes.LongLength, seen));
    }

    /// <summary>A source that arrives in pieces, as a socket does.</summary>
    private sealed class SlowStream(byte[] bytes) : Stream
    {
        private int position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (position >= bytes.Length) return 0;
            await Task.Delay(2, ct);

            var take = Math.Min(Math.Min(buffer.Length, 16 * 1024), bytes.Length - position);
            bytes.AsSpan(position, take).CopyTo(buffer.Span);
            position += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

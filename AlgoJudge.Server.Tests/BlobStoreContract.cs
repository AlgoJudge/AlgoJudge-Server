using System.Security.Cryptography;
using AlgoJudge.Server.Storage;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What every store must do, whatever it is made of.
/// <para>
/// <b>Written once and inherited</b>, because the whole design rests on nothing
/// above <c>IBlobStore</c> knowing which backend it is talking to (§2, invariant
/// 5). A backend with its own test file would be a backend free to be subtly
/// different — and the difference would surface as a corrupt download on
/// whichever installation happened to configure it.
/// </para>
/// <para>
/// This is also the skeleton the S3 conformance suite of §13.3 hangs on: the
/// same assertions run against a development endpoint and against the reference
/// implementation, with no test code in between that knows the difference.
/// </para>
/// </summary>
public abstract class BlobStoreContract
{
    protected abstract IBlobStore Store { get; }

    /// <summary>
    /// Deliberately not round: 300 000 bytes crosses PostgreSQL's TOAST
    /// threshold, so the bytes really do leave the main table, and it is large
    /// enough that a single-buffer implementation would show.
    /// </summary>
    private static byte[] Payload(int size = 300_000)
    {
        var bytes = new byte[size];
        // Patterned rather than random, so a failure says *where* it went wrong:
        // an off-by-one at the front looks completely different from a truncation.
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static BlobKey KeyFor(byte[] bytes) => new(Guid.NewGuid(), Sha256Of(bytes));

    [Fact]
    public async Task What_went_in_is_what_comes_out()
    {
        var bytes = Payload();
        var key = KeyFor(bytes);

        var written = await Store.WriteAsync(key, new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(Sha256Of(bytes), written.Sha256);
        Assert.Equal(bytes.LongLength, written.SizeBytes);

        await using var read = await Store.OpenReadAsync(key, CancellationToken.None);
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);

        Assert.Equal(bytes, buffer.ToArray());
    }

    [Fact]
    public async Task The_checksum_is_of_what_arrived_and_not_of_what_was_claimed()
    {
        var bytes = Payload(1024);

        // A key claiming somebody else's checksum. The store must not take the
        // claim as an answer — this is the whole of A12, and the reason a
        // truncated upload is refused rather than stored as a file whose
        // contents are quietly wrong.
        var key = new BlobKey(Guid.NewGuid(), Sha256Of([0x00]));

        var written = await Store.WriteAsync(key, new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(Sha256Of(bytes), written.Sha256);
        Assert.NotEqual(key.Sha256, written.Sha256);
    }

    /// <summary>
    /// The off-by-one that would not show up as an error anywhere.
    /// <para>
    /// A range read that starts one byte late still returns bytes, still fills a
    /// buffer, and still looks like a working download — until somebody resumes
    /// one and the checksum fails. PostgreSQL's <c>substring</c> is 1-based and
    /// an HTTP range is not, which is exactly where it would come from.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 10)]
    [InlineData(255, 1)]
    [InlineData(1000, 4096)]
    [InlineData(299_999, 1)]
    public async Task A_range_begins_exactly_where_it_was_asked_to(int offset, int length)
    {
        var bytes = Payload();
        var key = KeyFor(bytes);
        await Store.WriteAsync(key, new MemoryStream(bytes), CancellationToken.None);

        await using var read = await Store.OpenReadAsync(key, offset, length, CancellationToken.None);
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);

        Assert.Equal(bytes.Skip(offset).Take(length).ToArray(), buffer.ToArray());
    }

    [Fact]
    public async Task A_range_with_no_length_runs_to_the_end()
    {
        var bytes = Payload(5000);
        var key = KeyFor(bytes);
        await Store.WriteAsync(key, new MemoryStream(bytes), CancellationToken.None);

        await using var read = await Store.OpenReadAsync(key, 4990, null, CancellationToken.None);
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);

        Assert.Equal(bytes.Skip(4990).ToArray(), buffer.ToArray());
    }

    [Fact]
    public async Task Deleting_is_idempotent()
    {
        var bytes = Payload(64);
        var key = KeyFor(bytes);
        await Store.WriteAsync(key, new MemoryStream(bytes), CancellationToken.None);

        Assert.True(await Store.ExistsAsync(key, CancellationToken.None));

        await Store.DeleteAsync(key, CancellationToken.None);
        Assert.False(await Store.ExistsAsync(key, CancellationToken.None));

        // The collector may sweep the same file twice — once after a run that
        // failed partway. The second pass must be quiet, not an error.
        await Store.DeleteAsync(key, CancellationToken.None);
    }

    [Fact]
    public async Task A_blob_nobody_wrote_does_not_exist()
    {
        var key = new BlobKey(Guid.NewGuid(), Sha256Of([0x7F]));
        Assert.False(await Store.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task A_working_store_says_so()
    {
        var health = await Store.CheckHealthAsync(CancellationToken.None);

        Assert.True(health.Reachable);
        Assert.True(health.SmokeTestPassed);
        Assert.Equal(Store.Id, health.StoreId);
    }

    /// <summary>
    /// <para>
    /// Driving the probe directly rather than going through
    /// <c>CheckHealthAsync</c>, because the health check invents a fresh id every
    /// time and a test cannot then ask whether <i>that</i> blob is gone. An
    /// earlier version of this asked about a key nothing had ever written, passed
    /// for that reason, and went on passing with the cleanup deleted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_probe_does_not_leave_anything_behind()
    {
        var key = new BlobKey(Guid.NewGuid(), StoreProbe.Sha256);

        Assert.True(await StoreProbe.RunAsync(Store, key, CancellationToken.None));

        // A health endpoint gets polled — by a container, by a load balancer, by
        // whatever the operator wired up. A probe that accumulated would be a
        // slow leak nobody would ever connect to health checks.
        Assert.False(await Store.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task Delivery_is_a_stream_from_the_Server()
    {
        var bytes = Payload(16);
        var key = KeyFor(bytes);
        await Store.WriteAsync(key, new MemoryStream(bytes), CancellationToken.None);

        var delivery = await Store.PrepareDeliveryAsync(key, CancellationToken.None);

        // One case in the base version, and the seam that keeps a later offload
        // from becoming a rewrite of the endpoint (§10.0).
        Assert.Equal(BlobDeliveryKind.StreamFromServer, delivery.Kind);
    }
}

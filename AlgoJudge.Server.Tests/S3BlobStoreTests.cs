using AlgoJudge.Server.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The S3 conformance suite of §13.3.
/// <para>
/// <b>The same assertions as every other backend</b>, inherited from
/// <see cref="BlobStoreContract"/>, plus the few that are about S3 itself. No
/// test here knows which implementation is answering, which is what lets the
/// suite run against the development endpoint and against the reference one
/// without a line changing.
/// </para>
/// <para>
/// By default it starts <b>RustFS</b>, which is the local development endpoint
/// and nothing more (§10.3). <b>SeaweedFS is the reference implementation</b>
/// and is what a release is checked against: start one however you like and
/// point this at it —
/// </para>
/// <code>
/// ALGOJUDGE_S3_ENDPOINT=http://127.0.0.1:8333 \
/// ALGOJUDGE_S3_ACCESS_KEY=… ALGOJUDGE_S3_SECRET_KEY=… \
/// dotnet test --filter S3BlobStoreTests
/// </code>
/// <para>
/// The same shape as <c>ALGOJUDGE_TEST_DB</c> on the database, and for the same
/// reason: an endpoint somebody else started is the only way to check one this
/// suite has no business orchestrating.
/// </para>
/// </summary>
[Collection("storage")]
public sealed class S3BlobStoreTests : BlobStoreContract, IAsyncLifetime
{
    /// <summary>An endpoint somebody else started. Set it and no container is used.</summary>
    public const string EndpointVariable = "ALGOJUDGE_S3_ENDPOINT";

    public const string AccessKeyVariable = "ALGOJUDGE_S3_ACCESS_KEY";
    public const string SecretKeyVariable = "ALGOJUDGE_S3_SECRET_KEY";

    /// <summary>
    /// Pinned, like every other image in this repository. An object store that
    /// moves under a test suite produces a failure nobody can reproduce.
    /// </summary>
    private const string DevelopmentImage = "rustfs/rustfs:1.0.0-rc.1";

    private const string AccessKey = "algojudge-test";
    private const string SecretKey = "algojudge-test-only";

    private IContainer? container;
    private S3BlobStore store = null!;

    protected override IBlobStore Store => store;

    public async Task InitializeAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
        var accessKey = Environment.GetEnvironmentVariable(AccessKeyVariable) ?? AccessKey;
        var secretKey = Environment.GetEnvironmentVariable(SecretKeyVariable) ?? SecretKey;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            container = new ContainerBuilder()
                .WithImage(DevelopmentImage)
                .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
                .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
                // **Why the encryption item can run at all.** RustFS implements
                // SSE-S3 and refuses to enable it without a master key, which is
                // a configuration answer rather than the "not supported" the
                // plan feared. Thirty-two bytes of nothing in particular: this
                // key exists to make one assertion possible and guards no data
                // that outlives the test.
                .WithEnvironment(
                    "RUSTFS_SSE_S3_MASTER_KEY",
                    Convert.ToBase64String(Enumerable.Repeat((byte)0x2A, 32).ToArray()))
                .WithPortBinding(9000, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(9000))
                .Build();

            await container.StartAsync();
            endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(9000)}";
        }

        serviceUrl = endpoint;
        this.accessKey = accessKey;
        this.secretKey = secretKey;
        bucket = $"algojudge-test-{Guid.NewGuid():N}"[..40];

        store = new S3BlobStore(
            "objects",
            new S3StoreOptions
            {
                Endpoint = endpoint,
                Bucket = bucket,
                AccessKey = accessKey,
                SecretKey = secretKey,
                // The suite is not an installation, and this is the one place
                // the flag is meant to be on besides the development stack.
                CreateBucket = true,
            },
            Path.Combine(Path.GetTempPath(), "algojudge-s3-spool"));

        // Makes the bucket and proves the endpoint answers, in one act. A
        // failure here is worth seeing before thirteen contract assertions fail
        // for the same reason.
        var health = await store.CheckHealthAsync(CancellationToken.None);
        Assert.True(health.Reachable, $"the S3 endpoint did not answer: {health.Detail}");
        Assert.True(health.SmokeTestPassed, $"the S3 endpoint failed its smoke test: {health.Detail}");
    }

    public async Task DisposeAsync()
    {
        store.Dispose();
        if (container is not null) await container.DisposeAsync();
    }

    /// <summary>
    /// A 128 MiB object goes in one <c>PutObject</c>.
    /// <para>
    /// <b>The reason is the checksum, not the convenience.</b> A multipart
    /// object's <c>x-amz-checksum-sha256</c> is a checksum of checksums, so
    /// anything resting on that value rests on a different number that looks
    /// like the right kind of thing (§10.3, A59). This is the size the ceiling
    /// actually is, so if a package at the limit needed multipart, it would be
    /// here that it showed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_largest_thing_the_product_accepts_goes_in_one_piece()
    {
        var size = 128 * 1024 * 1024;
        var fileId = Guid.NewGuid();

        var written = await Store.WriteAsync(fileId, new PatternStream(size), CancellationToken.None);
        Assert.Equal(size, written.SizeBytes);

        var key = new BlobKey(fileId, written.Sha256);

        // Read back a window from the far end: an object stored in pieces and
        // reassembled wrongly is identical at the front.
        await using var tail = await Store.OpenReadAsync(key, size - 32, 32, CancellationToken.None);
        using var buffer = new MemoryStream();
        await tail.CopyToAsync(buffer);

        var expected = new byte[32];
        for (var i = 0; i < 32; i++) expected[i] = (byte)((size - 32 + i) % 251);
        Assert.Equal(expected, buffer.ToArray());

        await Store.DeleteAsync(key, CancellationToken.None);
    }

    /// <summary>
    /// §13.3: the implementation refuses an object whose declared checksum does
    /// not match its bytes.
    /// <para>
    /// This is about the <b>endpoint</b>, not about the Server — the Server's own
    /// recomputation is checked elsewhere. What it buys is knowing whether a
    /// truncation on the wire between here and the store would be caught by the
    /// store, which is the one stretch the Server cannot see.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_endpoint_refuses_bytes_that_do_not_match_their_declared_checksum()
    {
        using var client = ClientFor();
        var bytes = System.Text.Encoding.UTF8.GetBytes("what was sent");

        var refused = await Assert.ThrowsAnyAsync<Amazon.S3.AmazonS3Exception>(() =>
            client.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
            {
                BucketName = Bucket,
                Key = $"conformance/{Guid.NewGuid():N}",
                InputStream = new MemoryStream(bytes),
                // The checksum of something else entirely.
                ChecksumSHA256 = Convert.ToBase64String(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("what was claimed"))),
            }));

        Assert.NotNull(refused);
    }

    /// <summary>
    /// §13.3, the last item: where server-side encryption is on, a known string
    /// from an object is not findable in the store's data directory.
    /// <para>
    /// <b>Skipped against the development endpoint, and the reason is measured
    /// rather than assumed.</b> On 2026-08-12 this was run against RustFS in
    /// three shapes — a small object, a megabyte of repeated characters, and a
    /// megabyte of random bytes in a part file of its own — and in none of them
    /// was the plaintext findable by <c>grep -ra</c> over <c>/data</c>
    /// <i>even with encryption switched off</i>. RustFS does not lay object
    /// bytes down in a form this method can read, so a green result here would
    /// have meant nothing at all: the first version of this test passed with
    /// encryption disabled, which is how the problem was found.
    /// </para>
    /// <para>
    /// So it stays, whole and skipped, rather than being deleted or left to pass
    /// vacuously. §13.3 is a <b>release</b> check against SeaweedFS, not a
    /// per-commit one — run it there and un-skip:
    /// </para>
    /// <code>
    /// ALGOJUDGE_S3_ENDPOINT=… ALGOJUDGE_S3_ACCESS_KEY=… ALGOJUDGE_S3_SECRET_KEY=…
    /// </code>
    /// <para>
    /// The control below it is what makes it worth running at all: it looks for
    /// bytes that were never encrypted, where the method <b>must</b> succeed. If
    /// that one cannot find them, the item above proves nothing and both need
    /// looking at rather than one.
    /// </para>
    /// </summary>
    [Fact(Skip = "Needs an endpoint that stores object bytes readably; RustFS does not. Run against SeaweedFS before a release.")]
    public async Task Where_encryption_is_on_the_bytes_are_not_findable_on_disk()
    {
        Assert.NotNull(container);

        using var client = ClientFor();

        // The control first: if a plain object cannot be found, nothing about an
        // encrypted one follows from failing to find it either.
        var plain = $"algojudge-plaintext-{Guid.NewGuid():N}";
        await WriteThroughStoreAsync(plain);
        Assert.True(
            await FindableOnDiskAsync(plain),
            "a grep of the store's data directory could not find bytes that were never encrypted, "
            + "so it cannot show that encrypted ones are absent either");

        await client.PutBucketEncryptionAsync(new Amazon.S3.Model.PutBucketEncryptionRequest
        {
            BucketName = Bucket,
            ServerSideEncryptionConfiguration = new Amazon.S3.Model.ServerSideEncryptionConfiguration
            {
                ServerSideEncryptionRules =
                [
                    new Amazon.S3.Model.ServerSideEncryptionRule
                    {
                        ServerSideEncryptionByDefault = new Amazon.S3.Model.ServerSideEncryptionByDefault
                        {
                            ServerSideEncryptionAlgorithm = Amazon.S3.ServerSideEncryptionMethod.AES256,
                        },
                    },
                ],
            },
        });

        var encrypted = $"algojudge-encrypted-{Guid.NewGuid():N}";
        await WriteThroughStoreAsync(encrypted);

        Assert.False(
            await FindableOnDiskAsync(encrypted),
            "the plaintext of an encrypted object was findable in the store's data directory");
    }

    /// <summary>Writes it, and reads it back, so the store is known to hold it.</summary>
    private async Task WriteThroughStoreAsync(string text)
    {
        // **Incompressible padding**, and not a stylistic choice: a megabyte of
        // one repeated character is exactly the shape a store compresses, and a
        // compressed part file hides a plain string as thoroughly as an
        // encrypted one. Large enough, too, that the object is not inlined into
        // the store's own metadata, where it is encoded either way.
        var head = System.Text.Encoding.UTF8.GetBytes(text);
        var padded = new byte[head.Length + 1024 * 1024];
        head.CopyTo(padded, 0);
        Random.Shared.NextBytes(padded.AsSpan(head.Length));

        var fileId = Guid.NewGuid();
        var written = await Store.WriteAsync(
            fileId, new MemoryStream(padded), CancellationToken.None);

        await using var read = await Store.OpenReadAsync(
            new BlobKey(fileId, written.Sha256), CancellationToken.None);
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer);

        // Encryption at rest is transparent to the product either way; a value
        // that did not come back would make the grep meaningless in the other
        // direction.
        Assert.Equal(padded, buffer.ToArray());
    }

    /// <summary>
    /// <c>-a</c>, because a store keeps objects inside files of its own and grep
    /// treats those as binary and stays quiet without it.
    /// </summary>
    private async Task<bool> FindableOnDiskAsync(string needle)
    {
        var found = await container!.ExecAsync(["grep", "-r", "-a", "-l", needle, "/data"]);
        return found.ExitCode == 0;
    }

    private string Bucket => bucket;
    private string bucket = "";

    private Amazon.S3.AmazonS3Client ClientFor() =>
        new(new Amazon.Runtime.BasicAWSCredentials(accessKey, secretKey),
            new Amazon.S3.AmazonS3Config
            {
                ServiceURL = serviceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
            });

    private string serviceUrl = "";
    private string accessKey = "";
    private string secretKey = "";

    /// <summary>
    /// Bytes without an array to hold them: 128 MiB generated as it is read, so
    /// the test itself does not do the thing it is checking the Server does not.
    /// </summary>
    private sealed class PatternStream(long length) : Stream
    {
        private long position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - position);
            for (var i = 0; i < take; i++) buffer[offset + i] = (byte)((position + i) % 251);
            position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var take = (int)Math.Min(buffer.Length, length - position);
            for (var i = 0; i < take; i++) buffer.Span[i] = (byte)((position + i) % 251);
            position += take;
            return ValueTask.FromResult(take);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Task.FromResult(Read(buffer, offset, count));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

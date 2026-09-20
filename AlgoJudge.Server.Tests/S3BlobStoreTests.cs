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
    /// Which implementation this suite starts: <c>rustfs</c> or <c>seaweedfs</c>.
    /// <para>
    /// <b>Two, because the specification names two.</b> RustFS is the local
    /// development endpoint and SeaweedFS is the reference implementation a
    /// release is checked against (§10.3). Anything else is reached with
    /// <see cref="EndpointVariable"/>, which starts nothing — and the encryption
    /// item then has no data directory to look in and says so.
    /// </para>
    /// </summary>
    public const string ImplementationVariable = "ALGOJUDGE_S3";

    private const string AccessKey = "algojudge-test";
    private const string SecretKey = "algojudge-test-only";

    /// <summary>
    /// The one identity SeaweedFS is given. Without a configuration it refuses
    /// every signed request outright — "Signed request requires setting up
    /// SeaweedFS S3 authentication" — so an unconfigured one is not an
    /// anonymous endpoint but a closed door.
    /// </summary>
    private const string SeaweedIdentities = """
        {
          "identities": [
            {
              "name": "algojudge",
              "credentials": [
                { "accessKey": "algojudge-test", "secretKey": "algojudge-test-only" }
              ],
              "actions": ["Admin", "Read", "Write", "List", "Tagging"]
            }
          ]
        }
        """;

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
            var implementation =
                Environment.GetEnvironmentVariable(ImplementationVariable)?.Trim().ToLowerInvariant();

            ushort port = implementation == "seaweedfs" ? (ushort)8333 : (ushort)9000;
            container = (implementation == "seaweedfs" ? Seaweed() : Rustfs())
                .WithPortBinding(port, assignRandomHostPort: true)
                .Build();

            await container.StartAsync();
            endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(port)}";
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
        var health = await ServingAsync();
        Assert.True(health.Reachable, $"the S3 endpoint did not answer: {health.Detail}");
        Assert.True(health.SmokeTestPassed, $"the S3 endpoint failed its smoke test: {health.Detail}");
    }

    /// <summary>
    /// Asks the store until it is serving, and says why if it never does.
    /// <para>
    /// <b>Because no wait strategy is enough, and that was measured rather than
    /// assumed.</b> These stores open their port in about 100 ms and answer
    /// seconds later — 2.1 s for SeaweedFS 4.43, 3.1 s for 4.44 — and waiting on
    /// an HTTP answer does not close the gap either, because a 403 comes from
    /// the auth layer before the filer behind it can serve a bucket. While the
    /// pin sat at 4.43 the suite was not correct, only fast enough; one second
    /// of startup was the whole difference between green and three failures.
    /// </para>
    /// <para>
    /// <b>It retries the probe, not the assertions.</b> What comes back is the
    /// last answer, asserted once by the caller, so a store that is genuinely
    /// wrong still fails on its own message — and a store that never arrives
    /// says how many times it was asked and for how long, rather than timing out
    /// as a bare cancellation.
    /// </para>
    /// </summary>
    private async Task<StoreHealth> ServingAsync()
    {
        var patience = TimeSpan.FromSeconds(60);
        var started = DateTime.UtcNow;
        var attempts = 0;
        StoreHealth health;

        do
        {
            attempts++;
            health = await store.CheckHealthAsync(CancellationToken.None);
            if (health.Ok) return health;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        while (DateTime.UtcNow - started < patience);

        return health with
        {
            Detail = $"gave up after {attempts} attempts over {patience.TotalSeconds:0} s — "
                + $"the last answer was: {health.Detail}",
        };
    }

    public async Task DisposeAsync()
    {
        store.Dispose();
        if (container is not null) await container.DisposeAsync();
    }

    /// <summary>
    /// The local development endpoint. Pinned, like every other image here: one
    /// that moves under a suite produces a failure nobody can reproduce.
    /// </summary>
    private ContainerBuilder Rustfs()
    {
        return new ContainerBuilder("rustfs/rustfs:1.0.0")
            // **Readiness is the implementation's business, not the caller's.**
            // Waiting for the port was waiting for the wrong thing: both stores
            // open it in about 100 ms and answer seconds later.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(9000).ForPath("/").ForStatusCodeMatching(_ => true)))
            .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
            // RustFS implements SSE-S3 and refuses to enable it without a master
            // key. Thirty-two bytes of nothing in particular: it makes one
            // assertion possible and guards no data that outlives the test.
            .WithEnvironment(
                "RUSTFS_SSE_S3_MASTER_KEY",
                Convert.ToBase64String(Enumerable.Repeat((byte)0x2A, 32).ToArray()));
    }

    /// <summary>
    /// The reference implementation, which a release is checked against.
    /// <para>
    /// Its S3 gateway needs both a flag and an identity file; started without
    /// them it answers every signed request with a refusal rather than serving
    /// anonymously.
    /// </para>
    /// </summary>
    private ContainerBuilder Seaweed()
    {
        // **4.47**, the newest stable. The pin follows the newest release this
        // suite agrees on, and the suite is what decides: measured 2026-09-20,
        // fifteen of the sixteen pass here and the sixteenth skips for the
        // reason `EncryptionCapableFactAttribute` gives.
        //
        // **A version difference seen here is worth doubting twice.** Two of
        // them turned out to be ours rather than the image's — a readiness race
        // that `ServingAsync` closed, and an intermittent test since deleted —
        // and each read as a broken image until it was measured properly.
        // `CLAUDE.md` carries those measurements.
        return new ContainerBuilder("chrislusf/seaweedfs:4.47")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(8333).ForPath("/").ForStatusCodeMatching(_ => true)))
            .WithResourceMapping(
                System.Text.Encoding.UTF8.GetBytes(SeaweedIdentities), "/etc/seaweedfs/s3.json")
            .WithCommand("server", "-s3", "-s3.config=/etc/seaweedfs/s3.json", "-dir=/data");
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
    /// §13.3's last item, as far as a client can honestly take it: where the
    /// bucket is configured to encrypt by default, the store keeps accepting
    /// writes and reports the object it stored as encrypted.
    /// <para>
    /// <b>It does not grep the store's data directory.</b> That check lived here
    /// until 2026-09-07 and had to go: measured against SeaweedFS, the object's
    /// bytes were on disk, complete and contiguous — <c>od</c> showed all
    /// forty-eight characters — while <c>grep</c> found a forty-four character
    /// prefix of them in that same file and not the whole string. A method that
    /// answers "absent" about bytes that are present cannot decide this, and it
    /// fails in the dangerous direction: <c>false</c> is what the assertion
    /// reads as "encrypted", so a store that encrypted nothing would have
    /// passed.
    /// </para>
    /// <para>
    /// What is left is what the S3 contract can state, and it is not nothing: a
    /// store that takes the configuration and then reports <c>AES256</c> for the
    /// object is saying it encrypted it. Whether it really did is that
    /// implementation's business, measured by hand rather than asserted here.
    /// </para>
    /// </summary>
    [EncryptionCapableFact]
    public async Task Where_encryption_is_on_the_store_says_so_about_the_object()
    {
        using var client = ClientFor();

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

        // The write comes after the configuration on purpose: a store that
        // accepts the setting and then refuses every write fails here, which is
        // exactly what it should do.
        var fileId = Guid.NewGuid();
        var body = System.Text.Encoding.UTF8.GetBytes($"algojudge-encrypted-{Guid.NewGuid():N}");
        var written = await Store.WriteAsync(fileId, new MemoryStream(body), CancellationToken.None);

        var head = await client.GetObjectMetadataAsync(
            Bucket, new BlobKey(fileId, written.Sha256).Path);

        Assert.Equal(Amazon.S3.ServerSideEncryptionMethod.AES256, head.ServerSideEncryptionMethod);
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

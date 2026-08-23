using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace AlgoJudge.Server.Storage
{
    /// <summary>
    /// How to reach one bucket. Straight from the environment, never the database.
    /// </summary>
    public record S3StoreOptions
    {
        public required string Endpoint { get; init; }
        public required string Bucket { get; init; }
        public required string AccessKey { get; init; }
        public required string SecretKey { get; init; }

        /// <summary>
        /// Signed into every request. Meaningless to most S3-compatible servers
        /// and required by the signature all the same.
        /// </summary>
        public string Region { get; init; } = "us-east-1";

        /// <summary>
        /// Whether this Server may create the bucket when it is missing.
        /// <para>
        /// <b>Off, and it has to stay off anywhere real.</b> §10.3 and A61 put
        /// bucket creation with the operator for a concrete reason: on OVHcloud
        /// encryption at rest is applied <i>at creation</i> with
        /// <c>PutBucketEncryption</c> and cannot be added convincingly
        /// afterwards, so a bucket this Server made would be an unencrypted one
        /// nobody chose.
        /// </para>
        /// <para>
        /// It exists because the development stack has to come up from nothing
        /// with one command, and the alternative was a second image in the
        /// Compose file whose only job is one <c>mb</c>. Set in
        /// <c>example-server-development-docker-compose.yaml</c> and nowhere else.
        /// </para>
        /// </summary>
        public bool CreateBucket { get; init; }

        /// <summary>
        /// How long one request may take before it is abandoned.
        /// <para>
        /// <b>The SDK's own default is no deadline at all.</b> Measured against
        /// AWSSDK.S3 on 2026-08-23: an <c>AmazonS3Config</c> nobody assigns to
        /// carries a <c>Timeout</c> of <b>24 days</b>, which is
        /// <c>int.MaxValue</c> milliseconds wearing a hat. A request that is
        /// never answered was therefore never given up on, and this store takes
        /// <see cref="S3BlobStore.bucketGate"/> across its S3 calls — so one such
        /// request would have queued every upload in the installation behind it,
        /// for ever.
        /// </para>
        /// <para>
        /// Ten minutes, and generous on purpose: a write is a single
        /// <c>PutObject</c> of up to 128 MiB (§10.3, A59), which needs about
        /// 1.8 Mbit/s to fit. Short enough to bound the hang, long enough that no
        /// honest upload meets it. Lower it only where the link is known.
        /// </para>
        /// </summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// How many times a retryable failure is retried before it is reported.
        /// <para>
        /// <b>Two, which is the SDK's own default</b> — measured the same day, so
        /// this changes nothing and says so out loud. It is here because the
        /// number matters to how long a failing write takes and was not
        /// previously anybody's decision. Zero is a legitimate setting for a
        /// store whose failures are not worth waiting through.
        /// </para>
        /// </summary>
        public int MaxErrorRetry { get; init; } = 2;
    }

    /// <summary>
    /// Bytes in an object store.
    /// <para>
    /// <b>Single-part writes, deliberately (§10.3, A59).</b> A multipart upload's
    /// <c>x-amz-checksum-sha256</c> is a checksum <i>of checksums</i>, not the
    /// SHA-256 of the object — so anything resting on that value would be
    /// resting on a different number that looks like the right kind of thing.
    /// 128 MiB is well inside what one <c>PutObject</c> may carry.
    /// </para>
    /// <para>
    /// <b>Path style, and both checksum modes at <c>WHEN_REQUIRED</c>.</b> The
    /// SDK's defaults break several S3-compatible implementations, and virtual
    /// host style needs DNS somebody has to own. Neither is a preference.
    /// </para>
    /// </summary>
    public sealed class S3BlobStore : IBlobStore, IDisposable
    {
        private readonly S3StoreOptions options;
        private readonly string spoolPath;
        private readonly AmazonS3Client client;

        /// <summary>
        /// Asked once, before the first write, and never again.
        /// <para>
        /// <b>Not only in the health check</b>, which is where it lived until the
        /// development stack refused to come up: the seeder writes its documents
        /// long before anything polls health, and met "the specified bucket does
        /// not exist" as an unhandled exception at startup. A write has to be
        /// able to say whether the place it is writing to is there.
        /// </para>
        /// </summary>
        private readonly SemaphoreSlim bucketGate = new(1, 1);
        private bool bucketKnown;

        public S3BlobStore(string id, S3StoreOptions options, string spoolPath)
        {
            Id = id;
            this.options = options;
            this.spoolPath = spoolPath;

            client = new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey), ConfigFor(options));
        }

        /// <summary>
        /// The client configuration one set of options produces.
        /// <para>
        /// Separated from the constructor so it can be asserted on without an
        /// endpoint to talk to: every value here is a decision, and three of them
        /// exist because an SDK default was wrong for this product.
        /// </para>
        /// </summary>
        internal static AmazonS3Config ConfigFor(S3StoreOptions options) => new()
        {
            ServiceURL = options.Endpoint,
            // Bucket in the path, not in the hostname: virtual host style
            // needs wildcard DNS pointing at the endpoint, which a
            // self-hosted deployment almost never has.
            ForcePathStyle = true,
            AuthenticationRegion = options.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = options.Timeout,
            MaxErrorRetry = options.MaxErrorRetry,
        };

        /// <summary>
        /// What the client this store talks through was actually built with.
        /// <para>
        /// The client's own, not the options it came from. A test reading the
        /// options record proves a deployment's setting was <i>parsed</i>, which
        /// is one step short of the thing that matters — and a first attempt at
        /// this did exactly that and stayed green when the assignment was
        /// deleted.
        /// </para>
        /// </summary>
        internal AmazonS3Config Configuration => (AmazonS3Config)client.Config;

        public string Id { get; }

        public async Task<BlobWriteResult> WriteAsync(
            Guid fileId, Stream content, CancellationToken ct)
        {
            await EnsureBucketOnceAsync(ct);

            Directory.CreateDirectory(spoolPath);
            var spool = Path.Combine(spoolPath, $"{Guid.NewGuid():N}.blob");

            try
            {
                string sha256;
                long size;

                // Spooled for the same reason the database store spools: an
                // object needs a length before it can be signed and sent, and a
                // multipart section from a socket has none. Disk is the only
                // place to learn it that is not memory.
                await using (var hashing = new HashingStream(content))
                await using (var writing = new FileStream(
                    spool, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await hashing.CopyToAsync(writing, ct);
                    sha256 = hashing.Sha256;
                    size = hashing.BytesRead;
                }

                await using (var stored = new FileStream(
                    spool, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await client.PutObjectAsync(new PutObjectRequest
                    {
                        BucketName = options.Bucket,
                        Key = new BlobKey(fileId, sha256).Path,
                        InputStream = stored,
                        AutoCloseStream = false,
                        // The object appears whole or not at all: S3 has no
                        // partially visible object, so §5.2 costs nothing here.
                    }, ct);
                }

                return new BlobWriteResult { Sha256 = sha256, SizeBytes = size };
            }
            finally
            {
                if (File.Exists(spool)) File.Delete(spool);
            }
        }

        public Task<Stream> OpenReadAsync(BlobKey key, CancellationToken ct) =>
            OpenReadAsync(key, 0, null, ct);

        public async Task<Stream> OpenReadAsync(
            BlobKey key, long offset, long? length, CancellationToken ct)
        {
            var request = new GetObjectRequest { BucketName = options.Bucket, Key = key.Path };

            if (offset > 0 || length is not null)
            {
                // Inclusive at both ends, unlike everything else that takes an
                // offset and a count — a range of `0-0` is one byte.
                request.ByteRange = length is { } take
                    ? new ByteRange(offset, offset + take - 1)
                    : new ByteRange($"bytes={offset}-");
            }

            try
            {
                var response = await client.GetObjectAsync(request, ct);
                return new ResponseOwnedStream(response);
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new BlobMissingException(key);
            }
        }

        public async Task<bool> ExistsAsync(BlobKey key, CancellationToken ct)
        {
            try
            {
                await client.GetObjectMetadataAsync(options.Bucket, key.Path, ct);
                return true;
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        /// <summary>
        /// Idempotent by the protocol: deleting an object that is not there is a
        /// success in S3, so there is nothing to add.
        /// </summary>
        public async Task DeleteAsync(BlobKey key, CancellationToken ct) =>
            await client.DeleteObjectAsync(options.Bucket, key.Path, ct);

        public Task<BlobDelivery> PrepareDeliveryAsync(BlobKey key, CancellationToken ct) =>
            Task.FromResult(new BlobDelivery { Kind = BlobDeliveryKind.StreamFromServer });

        public async Task<StoreHealth> CheckHealthAsync(CancellationToken ct)
        {
            try
            {
                await EnsureBucketOnceAsync(ct);

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

        /// <summary>
        /// Once per process. A hundred uploads arriving together ask the endpoint
        /// one question between them, not a hundred.
        /// </summary>
        private async Task EnsureBucketOnceAsync(CancellationToken ct)
        {
            if (bucketKnown) return;

            await bucketGate.WaitAsync(ct);
            try
            {
                if (bucketKnown) return;
                await EnsureBucketAsync(ct);
                bucketKnown = true;
            }
            finally
            {
                bucketGate.Release();
            }
        }

        /// <summary>
        /// Asks whether the bucket is there, and makes it only where an operator
        /// has said this Server may. See <see cref="S3StoreOptions.CreateBucket"/>.
        /// </summary>
        private async Task EnsureBucketAsync(CancellationToken ct)
        {
            try
            {
                await client.GetBucketLocationAsync(options.Bucket, ct);
                return;
            }
            catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (!options.CreateBucket) throw;
            }

            await client.PutBucketAsync(new PutBucketRequest { BucketName = options.Bucket }, ct);
        }

        public void Dispose()
        {
            bucketGate.Dispose();
            client.Dispose();
        }

        /// <summary>
        /// The object's bytes, and the response they are arriving through.
        /// Closing one closes the other.
        /// </summary>
        private sealed class ResponseOwnedStream(GetObjectResponse response) : Stream
        {
            public override int Read(byte[] buffer, int offset, int count) =>
                response.ResponseStream.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                response.ResponseStream.ReadAsync(buffer, ct);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                response.ResponseStream.ReadAsync(buffer, offset, count, ct);

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => response.ContentLength;

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) response.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}

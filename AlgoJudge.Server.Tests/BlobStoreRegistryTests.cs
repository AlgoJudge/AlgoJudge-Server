using AlgoJudge.Server.Storage;
using Amazon.S3;
using Microsoft.Extensions.Configuration;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a deployment gets for saying nothing, and for saying it wrongly.
/// <para>
/// All of it is about startup, because that is the only moment these mistakes
/// are cheap: a store named and not configured is a 503 at somebody's download,
/// and a default nobody set used to be a database quietly swallowing every
/// package an installation ever accepted.
/// </para>
/// </summary>
public class BlobStoreRegistryTests
{
    private static IConfiguration Configured(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    /// <summary>
    /// <para>
    /// Until 2026-08-12 this synthesized a <c>postgres</c> store and carried on,
    /// which meant an installation could take a hundred gigabytes of submissions
    /// into its database without anybody choosing that. Where the files go is
    /// now something somebody says out loud.
    /// </para>
    /// </summary>
    /// <summary>
    /// An S3 request is given up on, and the SDK would not have given up at all.
    /// <para>
    /// Measured 2026-08-23 against AWSSDK.S3: an <c>AmazonS3Config</c> nobody
    /// assigns to carries a <c>Timeout</c> of <b>24 days</b> —
    /// <c>int.MaxValue</c> milliseconds. <c>S3BlobStore</c> holds its bucket gate
    /// across S3 calls, so one unanswered request would have queued every upload
    /// in the installation behind it with no end.
    /// </para>
    /// <para>
    /// The bound has to be finite and it has to be generous: a write is one
    /// <c>PutObject</c> of up to 128 MiB. This asserts both halves, because a
    /// deadline short enough to cut an honest upload is its own defect.
    /// </para>
    /// </summary>
    [Fact]
    public void An_s3_request_carries_a_deadline_the_sdk_would_not_have_given_it()
    {
        var config = S3BlobStore.ConfigFor(new S3StoreOptions
        {
            Endpoint = "http://127.0.0.1:9000",
            Bucket = "objects",
            AccessKey = "key",
            SecretKey = "secret",
        });

        Assert.NotNull(config.Timeout);
        Assert.True(
            config.Timeout < TimeSpan.FromHours(1),
            $"the request deadline is {config.Timeout}, which is the SDK's own absence of one");
        Assert.True(
            config.Timeout >= TimeSpan.FromMinutes(5),
            $"the request deadline is {config.Timeout}, short enough to cut a 128 MiB upload");
        Assert.Equal(2, config.MaxErrorRetry);
    }

    /// <summary>
    /// And a deployment may say both, because the right numbers depend on a link
    /// this Server has never seen.
    /// </summary>
    [Fact]
    public void A_deployment_may_state_its_own_deadline_and_retry_count()
    {
        var registry = new BlobStoreRegistry(Configured(
            ("Storage:Stores:objects:Kind", "s3"),
            ("Storage:Stores:objects:Endpoint", "http://127.0.0.1:9000"),
            ("Storage:Stores:objects:Bucket", "objects"),
            ("Storage:Stores:objects:AccessKey", "key"),
            ("Storage:Stores:objects:SecretKey", "secret"),
            ("Storage:Stores:objects:TimeoutSeconds", "45"),
            ("Storage:Stores:objects:MaxErrorRetry", "0")));

        // Read off the client, not off the options: parsing a setting and
        // applying it are two things, and only one of them is the point.
        var store = Assert.IsType<S3BlobStore>(registry.Find("objects"));
        Assert.Equal(TimeSpan.FromSeconds(45), store.Configuration.Timeout);
        Assert.Equal(0, store.Configuration.MaxErrorRetry);
    }

    [Fact]
    public void An_installation_that_configures_no_storage_does_not_start()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => new BlobStoreRegistry(Configured()));

        // The message has to say what to do, because it is the last thing an
        // operator sees before the container exits.
        Assert.Contains("Storage__Stores", refused.Message);
    }

    [Fact]
    public void A_default_naming_a_store_nobody_configured_does_not_start()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new BlobStoreRegistry(Configured(
            ("Storage:Stores:local:Kind", "filesystem"),
            ("Storage:Stores:local:Path", Path.GetTempPath()),
            ("Storage:Default", "somewhere-else"))));

        Assert.Contains("somewhere-else", refused.Message);
    }

    /// <summary>
    /// A store that cannot work without a setting says so at startup rather than
    /// at the first upload — a <c>filesystem</c> store with no path would write
    /// somewhere relative to the working directory, which is a place nobody
    /// chose and no backup covers.
    /// </summary>
    [Fact]
    public void A_store_missing_what_it_needs_does_not_start()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new BlobStoreRegistry(Configured(
            ("Storage:Stores:local:Kind", "filesystem"))));

        Assert.Contains("Path", refused.Message);
    }

    [Fact]
    public void A_kind_this_Server_does_not_implement_does_not_start()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => new BlobStoreRegistry(Configured(
            ("Storage:Stores:tape:Kind", "tape-library"))));

        Assert.Contains("tape", refused.Message);
    }

    /// <summary>
    /// <c>objects</c> is the shipped name of the default, so a deployment that
    /// follows the documented configuration needs no <c>Storage__Default</c> at
    /// all — and one that calls its store something else has to say so.
    /// </summary>
    [Fact]
    public void The_default_is_an_object_store_by_name()
    {
        var registry = new BlobStoreRegistry(Configured(
            ("Storage:Stores:objects:Kind", "filesystem"),
            ("Storage:Stores:objects:Path", Path.GetTempPath()),
            ("Storage:Stores:pg:Kind", "postgres")));

        Assert.Equal(BlobStoreRegistry.DefaultStoreId, registry.Default.Id);
        Assert.Equal("objects", registry.Default.Id);
    }

    [Fact]
    public void Several_stores_of_one_kind_are_told_apart_by_their_id()
    {
        var registry = new BlobStoreRegistry(Configured(
            ("Storage:Stores:cold:Kind", "filesystem"),
            ("Storage:Stores:cold:Path", Path.Combine(Path.GetTempPath(), "cold")),
            ("Storage:Stores:warm:Kind", "filesystem"),
            ("Storage:Stores:warm:Path", Path.Combine(Path.GetTempPath(), "warm")),
            ("Storage:Default", "warm")));

        // A65/§3: a store id names a place, not a kind. Two volumes are two
        // stores, and the day one is retired the rows still say which.
        Assert.Equal(2, registry.All.Count);
        Assert.Equal("warm", registry.Default.Id);
        Assert.NotNull(registry.Find("cold"));
        Assert.Null(registry.Find("neither"));
    }
}

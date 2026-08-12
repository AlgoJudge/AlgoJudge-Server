using System.Security.Cryptography;
using System.Text;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Storage;
using AlgoJudge.Server.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Moving every file from one store to another, while the product is watching.
/// <para>
/// <b>Its own database and its own stores.</b> A migration is a statement about
/// <i>every</i> file an installation holds, so running one against the shared
/// suite would move every other test's files to a volume those tests know
/// nothing about — and they would answer 503 for the rest of the run.
/// </para>
/// </summary>
[Collection("storage")]
public sealed class StorageMigrationTests : IAsyncLifetime
{
    private PostgreSqlContainer container = null!;
    private ServiceProvider services = null!;
    private string volume = "";

    private IBlobStore Source => Registry.Find("pg")!;
    private IBlobStore Target => Registry.Find("objects")!;
    private IBlobStoreRegistry Registry => services.GetRequiredService<IBlobStoreRegistry>();

    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder()
            .WithImage("postgres:18")
            .WithDatabase("algojudge")
            .WithUsername("algojudge")
            .WithPassword("test")
            .Build();
        await container.StartAsync();

        volume = Path.Combine(Path.GetTempPath(), $"algojudge-migrate-{Guid.NewGuid():N}");

        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        collection.AddSingleton(TimeProvider.System);
        collection.AddDbContext<ApplicationDbContext>(
            options => options.UseNpgsql(container.GetConnectionString()));

        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DbConnectionString"] = container.GetConnectionString(),
                // Two stores, and the target is where new writes go — which is
                // what a migration aims at.
                ["Storage:Stores:pg:Kind"] = "postgres",
                ["Storage:Stores:objects:Kind"] = "filesystem",
                ["Storage:Stores:objects:Path"] = volume,
                ["Storage:Default"] = "objects",
                // No window: a negative hour means any time. Setting it to the
                // current hour instead would make every test here flake on the
                // one run that crossed an hour boundary.
                [StorageMigrator.StartHourSetting] = "-1",
                [StorageMigrator.BudgetMinutesSetting] = "10",
                [StorageMigrator.GraceMinutesSetting] = "60",
            })
            .Build());

        collection.AddSingleton<IBlobStoreRegistry>(
            provider => new BlobStoreRegistry(provider.GetRequiredService<IConfiguration>()));
        collection.AddScoped<IStorageMigrations, StorageMigrations>();
        collection.AddSingleton<StorageMigrator>();

        services = collection.BuildServiceProvider();

        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await services.DisposeAsync();
        await container.DisposeAsync();
        if (Directory.Exists(volume)) Directory.Delete(volume, recursive: true);
    }

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options);

    /// <summary>A file in the source store, exactly as an upload would leave it.</summary>
    private async Task<(Guid Id, byte[] Bytes)> StoredInSourceAsync(int size = 4096)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);

        var fileId = Guid.NewGuid();
        var written = await Source.WriteAsync(fileId, new MemoryStream(bytes), CancellationToken.None);

        await using var context = NewContext();
        context.Files.Add(new Database.Models.File
        {
            Id = fileId,
            Name = "payload.bin",
            MimeType = "application/octet-stream",
            SizeBytes = written.SizeBytes,
            Sha256 = written.Sha256,
            StorageId = "pg",
        });
        await context.SaveChangesAsync();

        return (fileId, bytes);
    }

    private async Task RequestAsync()
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IStorageMigrations>()
            .RequestAsync(CancellationToken.None);
    }

    /// <summary>One tick, answering whether anything is still in flight.</summary>
    private Task<bool> TickAsync() =>
        services.GetRequiredService<StorageMigrator>().RunOnceAsync(CancellationToken.None);

    private async Task<Database.Models.File> FileAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.Files.AsNoTracking().FirstAsync(f => f.Id == id);
    }

    private async Task<byte[]> ReadAsync(IBlobStore store, Database.Models.File file)
    {
        await using var stream = await store.OpenReadAsync(
            new BlobKey(file.Id, file.Sha256), CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task Every_file_ends_up_on_the_target_with_its_checksum_intact()
    {
        var first = await StoredInSourceAsync();
        var second = await StoredInSourceAsync(200_000);

        await RequestAsync();
        await TickAsync();

        foreach (var (id, bytes) in new[] { first, second })
        {
            var file = await FileAsync(id);
            Assert.Equal("objects", file.StorageId);
            Assert.Equal(bytes, await ReadAsync(Target, file));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), file.Sha256);
        }

        await using var context = NewContext();
        var migration = await context.StorageMigrations.AsNoTracking().FirstAsync();
        Assert.Equal(StorageMigrationState.Finished, migration.State);
        Assert.Equal(2, migration.FilesMoved);
    }

    /// <summary>
    /// <para>
    /// A read follows its own row's <c>StorageId</c> for the whole move (A76),
    /// so there is no moment of global switch-over and no window in which some
    /// files are unreadable. Checked by looking at both sides while one has moved
    /// and one has not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Reads_are_correct_on_both_sides_while_it_is_running()
    {
        var moved = await StoredInSourceAsync();
        var waiting = await StoredInSourceAsync();

        // One file at a time: the budget is what stops it, so a zero budget moves
        // nothing and a run with one file in it is arranged by moving that one by
        // hand first.
        await RequestAsync();

        using (var scope = services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var migration = await context.StorageMigrations.FirstAsync();
            migration.State = StorageMigrationState.Running;
            migration.StartedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // Move exactly one, by hand, the way the worker does.
        await MoveOneByHandAsync(moved.Id);

        var movedRow = await FileAsync(moved.Id);
        var waitingRow = await FileAsync(waiting.Id);

        Assert.Equal("objects", movedRow.StorageId);
        Assert.Equal("pg", waitingRow.StorageId);

        // Each is readable where its own row says it is, and both are themselves.
        Assert.Equal(moved.Bytes, await ReadAsync(Target, movedRow));
        Assert.Equal(waiting.Bytes, await ReadAsync(Source, waitingRow));

        // And the one that moved is still readable at the source too, because the
        // stale copy outlives the switch — which is what makes a reader that
        // resolved the row a moment ago safe.
        Assert.Equal(moved.Bytes, await ReadAsync(Source, movedRow));
    }

    /// <summary>
    /// The stale copy goes on a grace period, never with the switch (A78).
    /// </summary>
    [Fact]
    public async Task The_source_copy_outlives_the_switch_and_then_goes()
    {
        var (id, bytes) = await StoredInSourceAsync();

        await RequestAsync();
        await TickAsync();

        var file = await FileAsync(id);
        Assert.Equal("pg", file.PreviousStorageId);
        Assert.NotNull(file.PreviousCopyDeleteAfter);

        // Still there, and still correct: a reader mid-flight is reading it.
        Assert.Equal(bytes, await ReadAsync(Source, file));

        // The grace period passes.
        await using (var context = NewContext())
        {
            var row = await context.Files.FirstAsync(f => f.Id == id);
            row.PreviousCopyDeleteAfter = DateTime.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();
        }

        await TickAsync();

        var after = await FileAsync(id);
        Assert.Null(after.PreviousStorageId);
        Assert.Null(after.PreviousCopyDeleteAfter);
        Assert.False(await Source.ExistsAsync(new BlobKey(id, after.Sha256), CancellationToken.None));

        // And the file itself is untouched by any of it.
        Assert.Equal(bytes, await ReadAsync(Target, after));
    }

    /// <summary>
    /// <para>
    /// Nothing moves while a Runner is holding a job or a series is open (A81).
    /// The reason is load rather than danger — a read follows its own row
    /// throughout — and a contest is the worst hour of the year to add any.
    /// </para>
    /// </summary>
    [Fact]
    public async Task It_waits_for_the_evaluation_queue()
    {
        var (id, _) = await StoredInSourceAsync();
        await QueueAJobAsync();

        await RequestAsync();
        await TickAsync();

        Assert.Equal("pg", (await FileAsync(id)).StorageId);

        await using var context = NewContext();
        var migration = await context.StorageMigrations.AsNoTracking().FirstAsync();
        Assert.Equal(StorageMigrationState.Requested, migration.State);
        Assert.Contains("queue", migration.Detail);
    }

    /// <summary>
    /// A crash is uninteresting because the worker keeps no state of its own
    /// (A85): what has moved is on the files, so a second run picks up whatever
    /// the first did not finish, and moves nothing twice.
    /// </summary>
    [Fact]
    public async Task Stopping_halfway_and_running_again_loses_and_duplicates_nothing()
    {
        var files = new List<(Guid Id, byte[] Bytes)>();
        for (var i = 0; i < 4; i++) files.Add(await StoredInSourceAsync());

        await RequestAsync();

        // The first run is cut short after two files, exactly as a killed process
        // would be — no cleanup, no note of where it got to.
        await using (var context = NewContext())
        {
            var migration = await context.StorageMigrations.FirstAsync();
            migration.State = StorageMigrationState.Running;
            migration.StartedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        await MoveOneByHandAsync(files[0].Id);
        await MoveOneByHandAsync(files[1].Id);

        // A fresh run, as a restarted process would do.
        await TickAsync();

        foreach (var (id, bytes) in files)
        {
            var file = await FileAsync(id);
            Assert.Equal("objects", file.StorageId);
            Assert.Equal(bytes, await ReadAsync(Target, file));
        }

        await using var final = NewContext();
        var finished = await final.StorageMigrations.AsNoTracking().FirstAsync();
        Assert.Equal(StorageMigrationState.Finished, finished.State);

        // Two by hand and two by the worker: the count is of what this run moved,
        // and moving one twice would show here.
        Assert.Equal(2, finished.FilesMoved);
    }

    [Fact]
    public async Task A_second_migration_is_refused_while_one_is_live()
    {
        await RequestAsync();

        using var scope = services.CreateScope();
        var again = scope.ServiceProvider.GetRequiredService<IStorageMigrations>();

        var refused = await Assert.ThrowsAsync<Utils.ConflictException>(
            () => again.RequestAsync(CancellationToken.None));

        Assert.Equal("storage.migration.running", refused.Code);
    }

    /// <summary>
    /// A copy that arrives as something else is refused, and the file stays put.
    /// <para>
    /// <b>A77, and the only moment it matters.</b> A migration is the one time
    /// the product's own copy of somebody's submission crosses a network it did
    /// not before. Corruption here would surface weeks later as a Runner refusing
    /// to judge, with nothing left pointing at the cause — so it is checked at
    /// the copy, and a file that fails stays where it was.
    /// </para>
    /// <para>
    /// Written because sabotage found the check was unprotected: removing the
    /// comparison altogether left every test green, since two working stores
    /// never disagree.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_file_that_arrives_as_something_else_is_not_committed()
    {
        var (id, bytes) = await StoredInSourceAsync();

        var migrator = new StorageMigrator(
            services.GetRequiredService<IServiceScopeFactory>(),
            new CorruptingRegistry(Registry),
            TimeProvider.System,
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<ILogger<StorageMigrator>>());

        await RequestAsync();
        await migrator.RunOnceAsync(CancellationToken.None);

        // Still where it was, still itself.
        var file = await FileAsync(id);
        Assert.Equal("pg", file.StorageId);
        Assert.Null(file.PreviousStorageId);
        Assert.Equal(bytes, await ReadAsync(Source, file));

        // And nothing was left at the target under its key.
        Assert.False(await Target.ExistsAsync(new BlobKey(id, file.Sha256), CancellationToken.None));
    }

    /// <summary>
    /// A file whose stale copy has not been swept yet is left alone.
    /// <para>
    /// It only arises with more than two stores — moved to B, then the default
    /// changes to C before the grace period passes — and moving it again would
    /// overwrite the one column that remembers where the copy on A is. So it
    /// waits one sweep, which costs an hour and loses nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_file_with_a_stale_copy_still_pending_is_left_for_the_next_run()
    {
        var (id, _) = await StoredInSourceAsync();

        // As though it had already moved once, from somewhere else, and its old
        // copy were still inside its grace period.
        await using (var context = NewContext())
        {
            var row = await context.Files.FirstAsync(f => f.Id == id);
            row.PreviousStorageId = "somewhere-else";
            row.PreviousCopyDeleteAfter = DateTime.UtcNow.AddHours(1);
            await context.SaveChangesAsync();
        }

        await RequestAsync();
        await TickAsync();

        var file = await FileAsync(id);
        Assert.Equal("pg", file.StorageId);
        Assert.Equal("somewhere-else", file.PreviousStorageId);
    }

    /// <summary>
    /// A registry whose target hands back bytes that are not what it was given.
    /// The source is the real one, so what fails is the copy and nothing else.
    /// </summary>
    private sealed class CorruptingRegistry(IBlobStoreRegistry inner) : IBlobStoreRegistry
    {
        public IBlobStore Default => new CorruptingStore(inner.Default);

        public IBlobStore? Find(string storageId) =>
            storageId == "objects"
                ? new CorruptingStore(inner.Find(storageId)!)
                : inner.Find(storageId);

        public IReadOnlyList<IBlobStore> All => inner.All;
    }

    private sealed class CorruptingStore(IBlobStore inner) : IBlobStore
    {
        public string Id => inner.Id;

        public async Task<BlobWriteResult> WriteAsync(Guid fileId, Stream content, CancellationToken ct)
        {
            var written = await inner.WriteAsync(fileId, content, ct);
            // The bytes are there; what comes back is a lie about them, which is
            // exactly the shape of a silent corruption in transit.
            return written with { Sha256 = new string('0', 64) };
        }

        public Task<Stream> OpenReadAsync(BlobKey key, CancellationToken ct) => inner.OpenReadAsync(key, ct);
        public Task<Stream> OpenReadAsync(BlobKey key, long offset, long? length, CancellationToken ct) =>
            inner.OpenReadAsync(key, offset, length, ct);
        public Task<bool> ExistsAsync(BlobKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);
        public Task DeleteAsync(BlobKey key, CancellationToken ct) => inner.DeleteAsync(key, ct);
        public Task<BlobDelivery> PrepareDeliveryAsync(BlobKey key, CancellationToken ct) =>
            inner.PrepareDeliveryAsync(key, ct);
        public Task<StoreHealth> CheckHealthAsync(CancellationToken ct) => inner.CheckHealthAsync(ct);
    }

    /// <summary>
    /// A run that has spent its budget stops, and stays stopped until the next
    /// window.
    /// <para>
    /// <b>Written because it did neither.</b> The window was only checked while
    /// a migration had not begun, and the budget was recomputed on every
    /// thirty-second tick — so a run that started at 02:00 was still copying at
    /// nine in the morning, with a fresh half hour handed to it every half
    /// minute. Both are what the window exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_run_that_has_spent_its_budget_waits_for_the_next_window()
    {
        await StoredInSourceAsync();
        await StoredInSourceAsync();

        await RequestAsync();

        // Begun, and its stretch started long enough ago that the budget is
        // gone. A tick now must move nothing.
        await using (var context = NewContext())
        {
            var migration = await context.StorageMigrations.FirstAsync();
            migration.State = StorageMigrationState.Running;
            migration.StartedAt = DateTime.UtcNow.AddMinutes(-11);
            await context.SaveChangesAsync();
        }

        await TickAsync();

        await using var after = NewContext();
        Assert.Equal(2, await after.Files.CountAsync(f => f.StorageId == "pg"));

        // **Back to `Requested`**, so the window gates it again. Left `Running`
        // it would take a fresh stretch thirty seconds later and the budget would
        // have bounded one call rather than the run.
        var stopped = await after.StorageMigrations.AsNoTracking().FirstAsync();
        Assert.Equal(StorageMigrationState.Requested, stopped.State);
        Assert.Null(stopped.StartedAt);
        Assert.Contains("budget", stopped.Detail);
    }

    /// <summary>
    /// Outside its hour, a migration that has already begun does not move files
    /// either — the window is about when work happens, not about when it is
    /// asked for.
    /// </summary>
    [Fact]
    public async Task A_run_outside_its_window_moves_nothing_even_once_it_has_begun()
    {
        await StoredInSourceAsync();
        await RequestAsync();

        await using (var context = NewContext())
        {
            var migration = await context.StorageMigrations.FirstAsync();
            migration.State = StorageMigrationState.Running;
            migration.StartedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        // A window three hours from now, so the current hour is not it.
        var elsewhere = WithStartHour((DateTime.UtcNow.Hour + 3) % 24);
        await elsewhere.GetRequiredService<StorageMigrator>().RunOnceAsync(CancellationToken.None);

        await using var after = NewContext();
        Assert.Equal(1, await after.Files.CountAsync(f => f.StorageId == "pg"));
        Assert.Contains(
            "window", (await after.StorageMigrations.AsNoTracking().FirstAsync()).Detail);
    }

    /// <summary>The same wiring with one setting changed, for the window tests.</summary>
    private ServiceProvider WithStartHour(int hour)
    {
        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        collection.AddSingleton(TimeProvider.System);
        collection.AddDbContext<ApplicationDbContext>(
            options => options.UseNpgsql(container.GetConnectionString()));
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DbConnectionString"] = container.GetConnectionString(),
                ["Storage:Stores:pg:Kind"] = "postgres",
                ["Storage:Stores:objects:Kind"] = "filesystem",
                ["Storage:Stores:objects:Path"] = volume,
                ["Storage:Default"] = "objects",
                [StorageMigrator.StartHourSetting] = hour.ToString(),
                [StorageMigrator.BudgetMinutesSetting] = "10",
                [StorageMigrator.GraceMinutesSetting] = "60",
            })
            .Build());
        collection.AddSingleton<IBlobStoreRegistry>(
            provider => new BlobStoreRegistry(provider.GetRequiredService<IConfiguration>()));
        collection.AddScoped<IStorageMigrations, StorageMigrations>();
        collection.AddSingleton<StorageMigrator>();
        return collection.BuildServiceProvider();
    }

    /// <summary>
    /// The tick follows the work.
    /// <para>
    /// An installation with nothing to migrate is the common case and the
    /// permanent one, and a thirty-second tick there is two indexed queries
    /// every thirty seconds for ever, to learn nothing. What decides the pace is
    /// whether anything is in flight, so that is what a run reports.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tick_says_whether_anything_is_in_flight()
    {
        // Nothing asked for, nothing stored: nothing to come back for.
        Assert.False(await TickAsync());

        await StoredInSourceAsync();
        Assert.False(await TickAsync());

        // A migration is a reason to look often.
        await RequestAsync();
        Assert.True(await TickAsync());

        // It finished, but a stale copy is still inside its grace period — which
        // is also work, and also a reason to come back.
        await using (var context = NewContext())
        {
            Assert.Equal(
                StorageMigrationState.Finished,
                (await context.StorageMigrations.AsNoTracking().FirstAsync()).State);
            Assert.True(await context.Files.AnyAsync(f => f.PreviousCopyDeleteAfter != null));
        }

        Assert.True(await TickAsync());

        // The grace period passes and the copy goes; now there is nothing left.
        await using (var context = NewContext())
        {
            foreach (var file in await context.Files.ToListAsync())
            {
                file.PreviousCopyDeleteAfter = DateTime.UtcNow.AddMinutes(-1);
            }
            await context.SaveChangesAsync();
        }

        Assert.False(await TickAsync());
    }

    /// <summary>Moves one file the way the worker does, for tests that need a half-done run.</summary>
    private async Task MoveOneByHandAsync(Guid id)
    {
        await using var context = NewContext();
        var file = await context.Files.FirstAsync(f => f.Id == id);

        var key = new BlobKey(file.Id, file.Sha256);
        await using (var reading = await Source.OpenReadAsync(key, CancellationToken.None))
        {
            await Target.WriteAsync(file.Id, reading, CancellationToken.None);
        }

        file.PreviousStorageId = file.StorageId;
        file.StorageId = "objects";
        file.PreviousCopyDeleteAfter = DateTime.UtcNow.AddHours(1);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The smallest thing that makes the queue look busy.
    /// <para>
    /// It never runs — nothing here judges anything — and the worker only asks
    /// whether a job in <c>Queued</c> or <c>Running</c> exists at all. The graph
    /// beneath it is what the schema insists on, not what the question needs.
    /// </para>
    /// </summary>
    private async Task QueueAJobAsync()
    {
        await using var context = NewContext();

        var userId = Guid.NewGuid().ToString();
        var user = new User { Id = userId, UserName = $"someone-{Guid.NewGuid():N}"[..20] };
        context.Users.Add(user);

        var activity = new Activity
        {
            Slug = $"a{Guid.NewGuid():N}"[..12],
            Name = "Activity",
            Type = "contest@1",
            RankingType = "sum@1",
            TimeZone = "UTC",
        };
        var series = new Series { Activity = activity, Slug = "r1", Name = "Round" };
        var problem = new Problem
        {
            Slug = $"p{Guid.NewGuid():N}"[..12],
            Name = "Problem",
            Type = "standard-io@1",
            OwnerUserId = userId,
        };
        var version = new ProblemVersion { Problem = problem, Version = 1 };
        var assignment = new SeriesProblem
        {
            Activity = activity, Series = series, Problem = problem, Slug = "A",
        };
        var submission = new Submission { UserId = userId, SeriesProblem = assignment };

        context.EvaluationJobs.Add(new EvaluationJob
        {
            Submission = submission,
            ProblemVersion = version,
            Attempt = 1,
            State = EvaluationJobState.Queued,
        });

        await context.SaveChangesAsync();
    }
}

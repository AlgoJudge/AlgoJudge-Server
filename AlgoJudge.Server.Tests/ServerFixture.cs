using System.Net;
using AlgoJudge.Server.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A real Server against a real PostgreSQL 18.
/// <para>
/// <b>Not an in-memory provider.</b> The job queue rests on
/// <c>SELECT … FOR UPDATE SKIP LOCKED</c>, on two filtered unique indexes and on
/// two check constraints — none of which the in-memory provider has. A suite
/// that passed on it would be testing a database this product never runs on.
/// </para>
/// <para>
/// The image is pinned to 18 for the same reason the Compose file is: 18 moved
/// where the data directory lives, and finding that out in production is
/// expensive.
/// </para>
/// </summary>
public sealed class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// Point the suite at a database somebody else started.
    /// <para>
    /// Testcontainers is the default and is what CI uses. This exists because it
    /// needs to reach a Docker daemon, and a developer whose Docker lives inside
    /// WSL while the SDK lives on Windows has no socket the library can open. In
    /// that case they start a container by hand and set this.
    /// </para>
    /// <para>
    /// The database it names is <b>emptied</b> at the start of the run — the
    /// suite owns whatever it is given, so it must never be pointed at anything
    /// real.
    /// </para>
    /// </summary>
    public const string ExternalDatabaseVariable = "ALGOJUDGE_TEST_DB";

    private PostgreSqlContainer? container;
    private string connectionString = "";

    public string ConnectionString => connectionString;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalDatabaseVariable);

        if (!string.IsNullOrWhiteSpace(external))
        {
            connectionString = external;
            await ResetAsync();
            return;
        }

        container = new PostgreSqlBuilder()
            .WithImage("postgres:18")
            .WithDatabase("algojudge")
            .WithUsername("algojudge")
            .WithPassword("test")
            .Build();

        await container.StartAsync();
        connectionString = container.GetConnectionString();
    }

    /// <summary>
    /// Drops everything, so a run starts from the same place whether the
    /// database is fresh or reused.
    /// </summary>
    private async Task ResetAsync()
    {
        await using var context = NewContext();
        await context.Database.EnsureDeletedAsync();
    }

    /// <summary>
    /// The admin token this host runs with.
    /// <para>
    /// Set, because <c>/admin</c> is closed without one and the tests that check
    /// the maintenance switch need it open. A test that wants it <b>closed</b>
    /// builds its own host with <see cref="Closed"/> rather than changing this
    /// one — the suite shares a database and a Server, and a setting one test
    /// turned off would be a setting every later test ran under.
    /// </para>
    /// </summary>
    public const string AdminToken = "admin-token-for-the-suite";

    /// <summary>
    /// A client on a host configured with <b>no</b> admin token, for the one
    /// rule that cannot be checked any other way: an empty token closes the
    /// whole group.
    /// </summary>
    public HttpClient Closed() =>
        WithWebHostBuilder(builder => builder.UseSetting("Admin:Token", "")).CreateClient();

    /// <summary>
    /// The eight the application registers through a factory: the drainer, the
    /// lease reaper, the deletion sweeper, the address sweeper, the series
    /// scheduler, the file collector, the storage migrator and the merge
    /// sweeper.
    /// </summary>
    private const int ExpectedSweepers = 8;

    /// <summary>
    /// The one registered by type: <see cref="Lti.Workers.GradeSyncWorker"/>.
    /// <para>
    /// <b>This line used to say it did not sweep on its own timer.</b> It does —
    /// a <c>BackgroundService</c> that sweeps once on start and once a minute
    /// after that — and the shape filter below walked straight past it, so every
    /// host a test built left one running against the shared database.
    /// </para>
    /// </summary>
    private const int ExpectedTypedSweepers = 1;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development, so the host applies migrations and seeds the data the
        // end-to-end test walks over.
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString);
        builder.UseSetting("Admin:Token", AdminToken);

        // **Named, because nothing is assumed any more.** An installation that
        // configures no storage refuses to start, and the suite is an
        // installation. `postgres` rather than an object store: the whole point
        // is that everything above IBlobStore behaves identically, and a suite
        // that needed a bucket to run would be slower for no reading it gives.
        builder.UseSetting("Storage:Stores:pg:Kind", "postgres");

        // A reserved slug namespace, so the rule that keeps an importer's names
        // to itself is in force for the whole suite rather than only inside the
        // test that asserts it. Nothing else here creates a slug starting this
        // way, and an installation that imports nothing configures none.
        builder.UseSetting("Problems:ReservedSlugPrefixes:0", "Imported-");
        builder.UseSetting("Storage:Default", "pg");

        // **Named for the same reason storage is.** An installation that trusts
        // no named proxy refuses to start, because trusting every sender of
        // `X-Forwarded-For` means a visitor can state their own address — and
        // once a judge is shown that address, a wrong one reads as an alibi.
        // Loopback here: `TestServer` sends nothing through a proxy, so this
        // says "believe nobody" in the only shape the setting has.
        builder.UseSetting("Forwarded:KnownProxies", "127.0.0.1");

        // `TestServer` leaves `RemoteIpAddress` null, and the maintenance switch
        // answers only to a caller on the loopback interface — so without this
        // every test of it would see a 404 and prove nothing.
        //
        // Inserted at the **very front** by a startup filter, ahead of the
        // middleware that stamps the true peer, which is the only way to stand
        // in for a real socket.
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new CallerAddress(IPAddress.Loopback)));

        // **The background workers are off, in every host this fixture builds.**
        //
        // They sweep on their own timers — the series scheduler every fifteen
        // seconds — against the one database the whole suite shares, and every
        // host a test builds runs its own copy of them. So a round a test creates
        // can be opened by a scheduler in another host: the row looks right, and
        // the announcement the test is watching for went to that host's event hub
        // instead. That is exactly what
        // `Two_schedulers_at_once_announce_each_round_exactly_once` kept failing
        // on, once per full run and never alone.
        //
        // Nothing here waits for a worker to act; the tests that exercise one
        // resolve it and call `TickAsync` themselves, which is both deterministic
        // and what they already did.
        //
        // **Removed by their registration shape rather than by type**, because
        // they are registered through factories while the framework's own
        // `GenericWebHostService` — the one that serves HTTP — is registered by
        // type. Removing every `IHostedService` would take the server down with
        // them. The count is asserted so that a worker registered the other way
        // fails here loudly instead of quietly sweeping under the suite again.
        builder.ConfigureServices(services =>
        {
            var sweepers = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                            && d.ImplementationFactory is not null)
                .ToList();

            if (sweepers.Count != ExpectedSweepers)
            {
                throw new InvalidOperationException(
                    $"The fixture expected to switch off {ExpectedSweepers} background workers "
                    + $"and found {sweepers.Count}. If one was added or registered by type, say so "
                    + "here — a worker left running sweeps the shared test database.");
            }

            // **And the one registered by type.** `AddHostedService<T>()` records
            // an implementation *type*, so the filter above cannot see it.
            //
            // What that cost: `LtiGradeSyncTests` builds eleven hosts and
            // disposes none, each sweeping the one shared database on its own
            // timer and posting to its own gradebook. A test's pending grade was
            // taken by somebody else's worker and posted somewhere the test could
            // not see, so its own count stayed at nought.
            // `A_settled_grade_is_not_posted_again_on_every_sweep` and
            // `An_excluded_submission_earns_no_grade` failed on it about one full
            // run in two and never alone. Measured 2026-08-24: three of six runs,
            // `Expected: 1, Actual: 0`.
            var typed = services
                .Where(d => d.ServiceType == typeof(IHostedService)
                            && d.ImplementationType == typeof(Lti.Workers.GradeSyncWorker))
                .ToList();

            if (typed.Count != ExpectedTypedSweepers)
            {
                throw new InvalidOperationException(
                    $"The fixture expected {ExpectedTypedSweepers} background worker registered by "
                    + $"type and found {typed.Count}. A worker left running sweeps the shared test "
                    + "database, and the tests that call it themselves then race with it.");
            }

            foreach (var sweeper in sweepers.Concat(typed))
            {
                services.Remove(sweeper);
            }

            // **Registered back as a plain singleton**, because switching a
            // worker off must not make it unreachable: `LtiGradeSyncTests`
            // resolves this one and calls `RunOnceAsync` itself, which is the
            // deterministic half of the arrangement. Removing it outright left
            // eleven tests unable to find it at all.
            services.AddSingleton<Lti.Workers.GradeSyncWorker>();
        });
    }

    /// <summary>
    /// The address a request came from — the socket, not a header.
    /// <para>
    /// A test sets this to arrive from somewhere else. It exists because
    /// <c>X-Forwarded-For</c> <b>cannot</b> stand in for it: forging that header
    /// is the attack the switch has to survive, so a test that only set the
    /// header would be asserting against a Server that agreed with it for the
    /// wrong reason. There has to be a way to be genuinely somebody else.
    /// </para>
    /// <para>
    /// Read only by the fixture, never by the Server, and only in the test host.
    /// </para>
    /// </summary>
    public const string PeerHeader = "X-Test-Peer";

    /// <summary>
    /// Gives every request in the test host a remote address: loopback, or
    /// whatever <see cref="PeerHeader"/> asked for.
    /// </summary>
    private sealed class CallerAddress(IPAddress fallback) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, step) =>
            {
                context.Connection.RemoteIpAddress =
                    context.Request.Headers.TryGetValue(PeerHeader, out var asked)
                    && IPAddress.TryParse(asked.ToString(), out var peer)
                        ? peer
                        : fallback;
                await step();
            });
            next(app);
        };
    }

    public ApplicationDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ApplicationDbContext(options);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (container is not null) await container.DisposeAsync();
    }
}

/// <summary>
/// One collection, so the tests share a database and run one at a time. They
/// walk over the same seeded activity, and a parallel run would have them
/// claiming each other's jobs.
/// </summary>
[CollectionDefinition("server")]
public class ServerCollection : ICollectionFixture<ServerFixture>;

using System.Net;
using AlgoJudge.Server.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development, so the host applies migrations and seeds the data the
        // end-to-end test walks over.
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString);
        builder.UseSetting("Admin:Token", AdminToken);

        // `TestServer` leaves `RemoteIpAddress` null, and the maintenance switch
        // answers only to a caller on the loopback interface — so without this
        // every test of it would see a 404 and prove nothing.
        //
        // Inserted at the **very front** by a startup filter, ahead of the
        // middleware that stamps the true peer, which is the only way to stand
        // in for a real socket.
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new CallerAddress(IPAddress.Loopback)));
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

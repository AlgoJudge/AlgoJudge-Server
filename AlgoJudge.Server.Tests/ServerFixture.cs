using AlgoJudge.Server.Database;
using Microsoft.AspNetCore.Hosting;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development, so the host applies migrations and seeds the data the
        // end-to-end test walks over.
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DbConnectionString", connectionString);
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

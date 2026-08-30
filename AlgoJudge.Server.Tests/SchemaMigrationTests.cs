using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// How a production database gets its schema, which until 2026-08-30 it could
/// not.
///
/// <para>
/// <b>The claim under test is that an installation can exist at all.</b> Outside
/// Development a pending migration threw, and nothing shipped could apply one —
/// so a fresh database had every migration pending and the Server never started.
/// <c>Database:MigrateOnStart</c> is the operator's way to say yes; the refusal
/// is unchanged when they have not.
/// </para>
///
/// <para>
/// <b>Its own empty database, and that is the point.</b> Every other test here
/// runs against a database <c>ServerFixture</c> has already migrated, which is
/// exactly the state these tests must not start from.
/// </para>
/// </summary>
[Collection("storage")]
public class SchemaMigrationTests : IAsyncLifetime
{
    private PostgreSqlContainer container = null!;

    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("algojudge")
            .WithUsername("algojudge")
            .WithPassword("test")
            .Build();
        await container.StartAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    private ApplicationDbContext Application() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .Options);

    private LtiDbContext Lti() =>
        new(new DbContextOptionsBuilder<LtiDbContext>()
            .UseNpgsql(container.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Lti"))
            .Options);

    /// <summary>
    /// The refusal is unchanged, and it now says what to do about it.
    ///
    /// <para>
    /// The second half is not decoration. This is the message a fresh
    /// installation meets on its first start, and "Database has pending
    /// migrations" — what it said before — leaves an operator with a stopped
    /// container and nothing to try.
    /// </para>
    /// </summary>
    [Fact]
    public void An_installation_that_has_not_asked_is_still_refused()
    {
        using var db = Application();

        var refused = Assert.Throws<InvalidOperationException>(
            () => Schema.Ensure(
                db.Database, migrate: false, "The database", NullLogger.Instance));

        Assert.Contains("AJ_Database__MigrateOnStart", refused.Message);
        Assert.Contains("pending migration", refused.Message);
    }

    /// <summary>
    /// With the switch on, an empty database becomes one the Server can serve —
    /// <b>both</b> histories, because two contexts share this database and the
    /// LTI module refuses on its own.
    /// </summary>
    [Fact]
    public async Task An_installation_that_asked_gets_its_schema()
    {
        using (var db = Application())
        {
            Schema.Ensure(db.Database, migrate: true, "The database", NullLogger.Instance);
        }

        using (var lti = Lti())
        {
            Schema.Ensure(
                lti.Database, migrate: true, "The LTI module's schema", NullLogger.Instance);
        }

        Assert.True(await TableExistsAsync("Activities"));
        Assert.True(await TableExistsAsync("LtiPlatforms"));

        // Two history tables, not one. Sharing them makes each context read the
        // other's rows as migrations it does not have, and both refuse.
        Assert.True(await TableExistsAsync("__EFMigrationsHistory"));
        Assert.True(await TableExistsAsync("__EFMigrationsHistory_Lti"));

        // Applying twice is what a restart does, so it has to be a no-op rather
        // than an error.
        using var again = Application();
        Schema.Ensure(again.Database, migrate: true, "The database", NullLogger.Instance);
        Assert.Empty(again.Database.GetPendingMigrations());
    }

    /// <summary>
    /// Two instances starting together migrate once.
    ///
    /// <para>
    /// <b>This is the test the advisory lock exists for.</b> Several Server
    /// instances against one database is a supported arrangement and they start
    /// together after an update — so without the lock one of them dies on a
    /// fresh installation, during the only start anybody is watching.
    /// </para>
    ///
    /// <para>
    /// <b>Proved by sabotage on 2026-08-30</b>: with the two
    /// <c>pg_advisory_lock</c> calls taken out of <c>Schema.Migrate</c> this
    /// fails with <c>23505 duplicate key value violates unique constraint
    /// "PK___EFMigrationsHistory"</c>. EF Core 10 therefore has no migration
    /// lock of its own, which is the question this test really settles.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_instances_starting_together_migrate_once()
    {
        // Started together rather than merely both started: a barrier both wait
        // on, so neither can finish before the other has begun. Two sequential
        // calls would pass with no lock at all and prove nothing.
        using var barrier = new Barrier(2);

        async Task StartOne()
        {
            await Task.Yield();
            using var db = Application();
            barrier.SignalAndWait();
            Schema.Ensure(db.Database, migrate: true, "The database", NullLogger.Instance);
        }

        // Neither threw, which is most of the claim: without the lock one of
        // them dies here rather than at the assertion below.
        await Task.WhenAll(Task.Run(StartOne), Task.Run(StartOne));

        Assert.True(await TableExistsAsync("Activities"));

        // Each migration recorded exactly once. Counted against what the
        // assembly declares rather than a literal, so adding a migration does
        // not silently make this test about a number instead of about the lock.
        using var db = Application();
        Assert.Equal(
            db.Database.GetMigrations().Count(),
            await CountAsync("SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
    }

    private async Task<bool> TableExistsAsync(string name) =>
        await CountAsync(
            $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name = '{name}'") == 1;

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}

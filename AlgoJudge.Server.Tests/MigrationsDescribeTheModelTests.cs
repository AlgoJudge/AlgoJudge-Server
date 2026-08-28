using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Internal;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What `dotnet ef migrations has-pending-model-changes` answers, asserted on
/// every run instead of remembered.
/// <para>
/// <b>Written because the snapshot had already gone stale once.</b> On
/// 2026-08-28 `ApplicationDbContextModelSnapshot.cs` still declared
/// `Runner.RowVersion` — a property removed the same day — and nothing noticed,
/// because removing a mapped property does not regenerate the snapshot. It read
/// as harmless: EF builds the runtime model from the entity classes. It is not,
/// because the snapshot is the differ's *before*, so the next migration would
/// have opened by dropping a PostgreSQL system column.
/// </para>
/// <para>
/// <b>Both contexts</b>, because the LTI module keeps its own migrations and its
/// own history table, and a check that covered one would say nothing about the
/// other.
/// </para>
/// </summary>
public class MigrationsDescribeTheModelTests
{
    [Fact]
    public void The_core_migrations_describe_the_core_model() =>
        AssertNoDifferences(new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(Unreachable).Options));

    [Fact]
    public void The_lti_migrations_describe_the_lti_model() =>
        AssertNoDifferences(new LtiDbContext(
            new DbContextOptionsBuilder<LtiDbContext>().UseNpgsql(Unreachable).Options));

    /// <summary>
    /// Nothing here connects: the differ compares two models, and a model is
    /// built from the assembly rather than read from a server.
    /// </summary>
    private const string Unreachable = "Host=localhost;Database=none;Username=none;Password=none";

    private static void AssertNoDifferences(DbContext context)
    {
        using var _ = context;

        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var source = context.GetService<IModelRuntimeInitializer>()
            .Initialize(((IMutableModel)snapshot!.Model).FinalizeModel(), designTime: true);
        var target = context.GetService<IDesignTimeModel>().Model;

        var differences = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(source.GetRelationalModel(), target.GetRelationalModel());

        Assert.True(
            differences.Count == 0,
            $"{context.GetType().Name}: the migrations no longer describe the model — "
            + $"{differences.Count} operation(s), starting with "
            + $"{differences.FirstOrDefault()?.GetType().Name}. Add a migration, or regenerate "
            + "the snapshot if a property was removed.");
    }
}

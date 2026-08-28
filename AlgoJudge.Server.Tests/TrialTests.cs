using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// That a trial can actually be stored, against a real PostgreSQL.
///
/// <para>
/// These exist because of how the table nearly shipped without existing: the
/// migration was generated empty, the model snapshot never learned about the
/// entity, and the whole suite stayed green — because nothing in it touched
/// <c>Trials</c>. A mapping no test exercises is a mapping nobody has run, and
/// the schema comes from migrations rather than from the model
/// (<c>Program.cs</c> calls <c>Database.Migrate()</c>), so the two can disagree
/// silently until the first request arrives.
/// </para>
/// </summary>
[Collection("server-3")]
public class TrialTests(ServerFixture server)
{
    /// <summary>
    /// Round-trips a row, which is what proves the table is in the database and
    /// not merely in the model.
    /// </summary>
    [Fact]
    public async Task A_trial_can_be_stored_and_read_back()
    {
        await using var context = server.NewContext();

        var activity = await context.Activities.FirstAsync(a => a.Slug == "DEV-2026");
        var trial = new Trial
        {
            ActivityId = activity.Id,
            UserId = "someone",
            PackageFileId = Guid.NewGuid(),
            ProblemType = "standard-io@1",
        };

        context.Trials.Add(trial);
        await context.SaveChangesAsync();

        await using var reader = server.NewContext();
        var stored = await reader.Trials.SingleAsync(t => t.Id == trial.Id);

        Assert.Equal(EvaluationJobState.Queued, stored.State);
        Assert.Equal("standard-io@1", stored.ProblemType);
        Assert.Null(stored.Measurement);

        reader.Trials.Remove(stored);
        await reader.SaveChangesAsync();
    }

    /// <summary>
    /// The package outlives nothing, and the row outlives the package.
    /// <para>
    /// <see cref="Trial.PackageFileId"/> is nullable on purpose: the bytes are
    /// removed the moment a trial finishes, and what was measured has to stay
    /// readable afterwards. A column that refused the null would force either
    /// keeping every trial package forever or deleting the measurements with
    /// them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_finished_trial_keeps_its_measurement_after_the_package_is_gone()
    {
        await using var context = server.NewContext();

        var activity = await context.Activities.FirstAsync(a => a.Slug == "DEV-2026");
        var trial = new Trial
        {
            ActivityId = activity.Id,
            UserId = "someone",
            PackageFileId = Guid.NewGuid(),
            ProblemType = "standard-io@1",
        };

        context.Trials.Add(trial);
        await context.SaveChangesAsync();

        trial.PackageFileId = null;
        trial.State = EvaluationJobState.Completed;
        trial.FinishedAt = DateTime.UtcNow;
        trial.Measurement = """{"groups":[]}""";
        await context.SaveChangesAsync();

        await using var reader = server.NewContext();
        var stored = await reader.Trials.SingleAsync(t => t.Id == trial.Id);

        Assert.Null(stored.PackageFileId);
        Assert.Equal("""{"groups":[]}""", stored.Measurement);

        reader.Trials.Remove(stored);
        await reader.SaveChangesAsync();
    }

    /// <summary>
    /// The filtered unique index, which is what makes reporting a trial
    /// idempotent — the same mechanism a result uses, on its own table.
    /// <para>
    /// Both halves matter. Two live leases sharing a token must be impossible,
    /// and two trials with no lease at all must be ordinary — an unfiltered
    /// unique index would allow one queued trial in the whole installation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_trials_may_share_no_lease_but_never_the_same_one()
    {
        await using var context = server.NewContext();

        var activity = await context.Activities.FirstAsync(a => a.Slug == "DEV-2026");
        Trial Queued() => new()
        {
            ActivityId = activity.Id,
            UserId = "someone",
            ProblemType = "standard-io@1",
        };

        var first = Queued();
        var second = Queued();
        context.Trials.AddRange(first, second);

        // Two unclaimed trials, both with a null lease token: the filter is what
        // makes this the ordinary case rather than a conflict.
        await context.SaveChangesAsync();

        var token = Guid.NewGuid();
        first.LeaseToken = token;
        await context.SaveChangesAsync();

        second.LeaseToken = token;
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        await using var cleanup = server.NewContext();
        cleanup.Trials.RemoveRange(
            await cleanup.Trials.Where(t => t.Id == first.Id || t.Id == second.Id).ToListAsync());
        await cleanup.SaveChangesAsync();
    }
}

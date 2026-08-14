using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Takes a lease away in the one instant that is hard to hit by waiting: after a
/// request has read the job and before its <c>UPDATE</c> reaches the database.
///
/// <para>
/// <b>Why an interceptor and not a sleep.</b> The race this reproduces was found
/// as a test that failed roughly one run in four and passed on its own — which is
/// exactly the shape of defect that gets rerun until it goes green and then
/// forgotten. Until it fails on demand, no fix for it can be checked.
/// </para>
///
/// <para>
/// The reclaim runs on <b>its own connection</b>, so it is a genuinely separate
/// transaction rather than a change the saving context can see in its own. That
/// is what makes the row's <c>xmin</c> move under it, which is the whole
/// mechanism: the concurrency token is doing its job, and the question is only
/// what the Server does about it.
/// </para>
/// </summary>
public sealed class ReclaimWhileSaving(string connectionString) : SaveChangesInterceptor
{
    /// <summary>The job to snatch, once armed. Null disarms it entirely.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Whether it actually fired, so a test cannot pass by not racing.</summary>
    public bool Fired { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (JobId is { } wanted && !Fired && eventData.Context is { } context
            && context.ChangeTracker.Entries<EvaluationJob>().Any(e => e.Entity.Id == wanted))
        {
            Fired = true;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // What `LeaseReaper` does to an expired lease, written here directly:
            // the token goes first, so a Runner reporting against the old lease is
            // refused rather than allowed to overwrite whoever holds the job now.
            command.CommandText = """
                UPDATE "EvaluationJobs"
                   SET "LeaseToken" = NULL,
                       "LeaseExpiresAt" = NULL,
                       "RunnerId" = NULL,
                       "ClaimedAt" = NULL,
                       "State" = 0
                 WHERE "Id" = @id
                """;
            command.Parameters.AddWithValue("id", wanted);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

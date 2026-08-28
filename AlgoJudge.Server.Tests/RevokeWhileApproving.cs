using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Npgsql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Revokes a Runner in the one instant that decides whether approving it is
/// atomic: after the request has read the row and before its <c>UPDATE</c>
/// reaches the database.
///
/// <para>
/// <b>Why an interceptor and not two calls in order.</b> Approving a Runner that
/// is <i>already</i> revoked is refused by any implementation, including the
/// read-then-write one this replaced — so a test that revokes first proves
/// nothing. The only version that tells them apart revokes <b>in between</b>,
/// and that instant cannot be reached by waiting.
/// </para>
///
/// <para>
/// The revoke runs on <b>its own connection</b>, so it is a separate
/// transaction the reading context cannot see in its own. Sister to
/// <see cref="ReclaimWhileSaving"/>, which does the same to a job — except that
/// this one hangs off the <i>read</i>, because the write it is racing carries
/// its own condition and never reads at all.
/// </para>
/// </summary>
public sealed class RevokeWhileApproving(string connectionString) : DbCommandInterceptor
{
    /// <summary>The Runner to revoke, once armed. Null disarms it entirely.</summary>
    public Guid? RunnerId { get; set; }

    /// <summary>Whether it actually fired, so a test cannot pass by not racing.</summary>
    public bool Fired { get; private set; }

    /// <summary>
    /// <b>After the read, not before it.</b> Hooked to <c>Executing</c> the
    /// revoke landed first, so the <c>SELECT</c> returned an already-revoked
    /// Runner and every implementation refused — the test passed under its own
    /// sabotage. The row has to be read as pending and revoked afterwards.
    /// </summary>
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (RunnerId is { } wanted
            && !Fired
            && command.CommandText.Contains("\"Runners\"", StringComparison.Ordinal)
            && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal))
        {
            Fired = true;

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var revoke = connection.CreateCommand();
            revoke.CommandText = """
                UPDATE "Runners"
                   SET "State" = 2,
                       "RevokedAt" = now()
                 WHERE "Id" = @id
                """;
            revoke.Parameters.AddWithValue("id", wanted);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }
}

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Announces a round from somewhere else in the one instant that is hard to hit
/// by waiting: after a sweep has read its candidates and before it writes them.
///
/// <para>
/// <b>Why this exists.</b> The first attempt at reproducing the scheduler race
/// ran two sweeps at once and asserted the outcome — and it passed against the
/// broken code, because the two passes happened not to overlap. A test that only
/// sometimes exercises the thing it is named after is worth nothing, and would
/// have shipped a fix nobody had checked.
/// </para>
///
/// <para>
/// It hooks the <b>read</b> rather than the save, because that is the only point
/// both shapes of the code share: the old one saved a batch and the new one
/// issues conditional updates, so an interceptor on saving never fires against
/// the fix at all.
/// </para>
/// </summary>
public sealed class StealRoundsWhileReading(string connectionString) : DbCommandInterceptor
{
    /// <summary>The rounds to announce from underneath. Empty disarms it.</summary>
    public IReadOnlyList<Guid> Rounds { get; set; } = [];

    /// <summary>
    /// How many rounds it actually took. <b>Counted from the update rather than
    /// from having run</b>: a flag set before the statement executes is true even
    /// when the statement changed nothing, and a test asserting that flag then
    /// passes without any race having happened. Cost one wrong conclusion.
    /// </summary>
    public int Stolen { get; private set; }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        // The sweep's own candidate query, and nothing else: it is the only read
        // that asks for rounds whose start has not been announced.
        // **The sweep's own candidate query, and only it.** Every query that
        // loads a round mentions `StartAnnouncedAt`, because EF lists all the
        // columns; the one that is looking for rounds to open is the one that
        // also tests `PausedAt`.
        if (Rounds.Count > 0 && Stolen == 0
            && command.CommandText.Contains("StartAnnouncedAt")
            && command.CommandText.Contains("PausedAt")
            && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            // Its own connection, so this is a separate transaction that the
            // reading context cannot see inside its own — which is what makes
            // the row move under it.
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var steal = connection.CreateCommand();
            steal.CommandText = """
                UPDATE "Series"
                   SET "StartAnnouncedAt" = now() AT TIME ZONE 'utc',
                       "IsOpen" = true
                 WHERE "Id" = ANY(@ids)
                """;
            steal.Parameters.AddWithValue("ids", Rounds.ToArray());
            Stolen = await steal.ExecuteNonQueryAsync(cancellationToken);
        }

        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }
}

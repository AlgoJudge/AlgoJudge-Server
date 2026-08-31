using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Holds one submission open in the instant that decides whether the per-problem
/// ceiling can be raced past: after it has counted what the contestant has sent
/// and before its own row reaches the database.
///
/// <para>
/// <b>Why it holds rather than writes.</b> Its sisters —
/// <see cref="RevokeWhileApproving"/>, <see cref="ReclaimWhileSaving"/> — do
/// their sabotage on a connection of their own, which is right for a race
/// against a conditional write. It would be wrong here: an advisory lock only
/// serialises code that <i>takes</i> it, so an <c>INSERT</c> on another
/// connection models a writer that bypasses the lock, and the test would fail
/// against a correct implementation. What is wanted is two real requests, one
/// parked inside its transaction so the other has to arrive while it is there.
/// </para>
///
/// <para>
/// <b>After the read, not before it</b>, for the same reason
/// <see cref="RevokeWhileApproving"/> gives: parked before the count, the second
/// request would be the one that counted first and the interleave under test
/// would never happen.
/// </para>
/// </summary>
public sealed class HoldTheCeilingCount : DbCommandInterceptor
{
    private readonly TaskCompletionSource released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Armed by the test once the fixture is built, so sign-in is not caught.</summary>
    public bool Armed { get; set; }

    /// <summary>Whether it actually fired, so a test cannot pass by not racing.</summary>
    public bool Fired { get; private set; }

    /// <summary>Lets the parked request finish.</summary>
    public void Release() => released.TrySetResult();

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (Armed
            && !Fired
            && command.CommandText.Contains("\"Submissions\"", StringComparison.Ordinal)
            && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase))
        {
            Fired = true;

            // Bounded, so a test that never releases fails on an assertion rather
            // than hanging a CI run to its own timeout.
            await Task.WhenAny(released.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
        }

        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }
}

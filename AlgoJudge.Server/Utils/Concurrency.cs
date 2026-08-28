using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Utils
{
    /// <summary>
    /// Saving a row that carries an optimistic-concurrency token, and answering
    /// the caller who lost the race.
    /// <para>
    /// <b>A token turns a silent overwrite into an exception, and an unhandled
    /// one into a 500.</b> That is not theoretical: it happened on
    /// <c>EvaluationJob</c>, where a lease renewal that raced the reaper answered
    /// 500 — the one reply a Runner cannot act on. See
    /// <c>RunnerService.ExtendAsync</c>, which is where this pattern was first
    /// written out by hand.
    /// </para>
    /// <para>
    /// <b>The loser gets the answer it would have got had it read a moment
    /// later.</b> Every one of these paths already guards itself — a deletion
    /// must still be pending, a merge must not already be anonymised — and those
    /// guards were simply evaluated against a row that then moved. So the row is
    /// re-read and the guard run again, and it produces its own refusal, with its
    /// own code. Nothing new appears in the API.
    /// </para>
    /// </summary>
    public static class Concurrency
    {
        /// <summary>
        /// Saves, and on a lost race re-reads and lets <paramref name="reconsider"/>
        /// refuse.
        /// </summary>
        /// <param name="reconsider">
        /// The path's own guard, run again against the row as it now is. It is
        /// expected to throw; if it does not, somebody moved a column the guard
        /// does not read, and the caller is asked to try again — the pending
        /// changes cannot simply be re-applied, because reloading the entry is
        /// what discarded them.
        /// </param>
        public static async Task SaveAsync(
            DbContext context, Func<CancellationToken, Task> reconsider, CancellationToken ct)
        {
            try
            {
                await context.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException conflict)
            {
                foreach (var entry in conflict.Entries)
                {
                    await entry.ReloadAsync(ct);
                }

                await reconsider(ct);

                throw new ConflictException(
                    "Somebody changed this at the same moment. Nothing was written; read it "
                    + "again and repeat what you meant.",
                    "concurrency.conflict");
            }
        }

        /// <summary>
        /// The same, for a path whose only guard is "nobody else has touched
        /// this" — a settings object replaced whole, say.
        /// </summary>
        public static Task SaveAsync(DbContext context, CancellationToken ct) =>
            SaveAsync(context, _ => Task.CompletedTask, ct);
    }
}

using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Who a submission counts for: a person, or the group they compete as.
    /// <para>
    /// <b>One rule, in one place, because two copies of it would disagree.</b>
    /// The ceiling that refuses a submission and the figure that tells somebody
    /// how many are left are computed by different services, and a screen saying
    /// "one left" over a Server that refuses is worse than either being wrong
    /// alone.
    /// </para>
    /// </summary>
    public static class Contestant
    {
        /// <summary>
        /// The group somebody competes as in this activity, or null for somebody
        /// competing as themselves.
        /// </summary>
        public static async Task<Guid?> GroupAsync(
            ApplicationDbContext context, Guid activityId, string userId, CancellationToken ct) =>
            await context.Grants
                .AsNoTracking()
                .Where(g => g.ActivityId == activityId && g.UserId == userId)
                .Select(g => g.GroupId)
                .FirstOrDefaultAsync(ct);

        /// <summary>
        /// Everything that counts against this contestant, out of a query already
        /// narrowed to one assignment.
        /// <para>
        /// <b>The ungrouped half is the one that is easy to get wrong.</b> It is
        /// not "everything this person sent" — it is what they sent <i>while not
        /// in a group</i>. Leaving that out would let somebody leave a group and
        /// find their allowance topped up with the group's spending, or come back
        /// to a fresh one; keeping it in means a move changes what happens next
        /// and nothing that already happened, which is the rule the whole
        /// stamping arrangement exists for.
        /// </para>
        /// <para>
        /// <b>An exclusion is not a refund</b>, so nothing here reads
        /// <see cref="Submission.ExcludedAt"/>: it rules on a result, and the
        /// attempt was still made. Filtering it out would be a silent way to
        /// raise a ceiling.
        /// </para>
        /// </summary>
        public static IQueryable<Submission> Sent(
            IQueryable<Submission> submissions, string userId, Guid? groupId) =>
            groupId is { } group
                ? submissions.Where(s => s.GroupId == group)
                : submissions.Where(s => s.UserId == userId && s.GroupId == null);

        /// <summary>
        /// Ours, in PostgreSQL's one flat advisory-lock namespace. "AJSB", and
        /// carrying no meaning beyond being ours — the same reasoning as
        /// <c>Database/Schema.cs</c>'s migration lock.
        /// </summary>
        private const int LockNamespace = 0x414A_5342;

        /// <summary>
        /// Holds this contestant's allowance for the rest of the transaction, so
        /// the count and the insert cannot be interleaved.
        /// <para>
        /// <b>The ceiling was a read-then-insert until 2026-08-31.</b> Two
        /// submissions sent at once both counted the same number, both passed
        /// <c>used &gt;= limit</c>, and both were written — a scoring-integrity
        /// defect reachable with two parallel requests wherever a limit is set.
        /// </para>
        /// <para>
        /// <b>Blocking, not <c>pg_try_</c>, and that is the opposite of the sweeps.</b>
        /// <c>FileCollector</c> and <c>StorageMigrator</c> skip when somebody else
        /// holds the lock, which is right because a sweep is idempotent and the
        /// next one is a day away. Here the loser must wait and then be
        /// <i>correctly refused</i>: skipping the wait is exactly the bug.
        /// </para>
        /// <para>
        /// <b>Transaction-scoped, so it needs no unlock and no held connection.</b>
        /// It does require an explicit transaction — outside one, every statement
        /// is its own and the lock would be released before the count was read.
        /// </para>
        /// <para>
        /// The key is the contestant, which is the group when there is one: a
        /// group spends one allowance, so its members must take one lock.
        /// Hashed rather than <c>GetHashCode</c>, which is randomised per process
        /// and would have two Server instances taking different locks.
        /// </para>
        /// </summary>
        public static Task LockAsync(
            ApplicationDbContext context,
            string userId,
            Guid? groupId,
            Guid assignmentId,
            CancellationToken ct)
        {
            var key = BitConverter.ToInt32(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"{groupId?.ToString() ?? userId}|{assignmentId}")));

            return context.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock({LockNamespace}, {key})", ct);
        }
    }
}

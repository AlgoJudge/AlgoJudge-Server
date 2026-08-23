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
    }
}

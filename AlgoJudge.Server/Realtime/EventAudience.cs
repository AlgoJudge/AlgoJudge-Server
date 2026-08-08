using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Realtime
{
    /// <summary>
    /// Who may hear about a thing.
    /// <para>
    /// The fan-out runs through <b>the same rule as a fetch</b>: an event names
    /// data, and it goes only to somebody a request for that data would have been
    /// answered for. A socket that skipped this would be a way around the
    /// permission the endpoint applies.
    /// </para>
    /// <para>
    /// One implementation on purpose. There were three — in
    /// <c>SeriesScheduler</c>, in <c>ManagerReadService</c> and inside
    /// <c>ResultsService.BroadcastAsync</c> — and they had already drifted: two of
    /// them read only the grants belonging to the activity, so an administrator
    /// holding a <b>system</b> grant and nothing else was left out of events they
    /// are entitled to. The effective set is the union of a user's system grant
    /// and their grant in the activity, which is what
    /// <c>PermissionService</c> resolves for a request.
    /// </para>
    /// </summary>
    public interface IEventAudience
    {
        /// <summary>
        /// Everybody who may exercise <paramref name="permission"/> in this
        /// activity, by a grant in it or by one held across the installation.
        /// </summary>
        Task<IReadOnlyList<string>> InActivityAsync(
            Guid activityId, string permission, CancellationToken ct);

        /// <summary>
        /// Everybody who may exercise it <b>anywhere</b>.
        /// <para>
        /// For the surfaces that are not an activity's: the problem library, the
        /// permission templates, the Runners. A manager of one activity sees the
        /// library, so the audience cannot be narrowed to an activity that does
        /// not exist for these.
        /// </para>
        /// </summary>
        Task<IReadOnlyList<string>> AnywhereAsync(string permission, CancellationToken ct);

        /// <summary>
        /// Everybody enrolled in the activity, whatever they hold.
        /// <para>
        /// For what an activity publishes to its members rather than to its
        /// staff — a question answered, an announcement. Membership <b>is</b> the
        /// grant, so this is every active grant in it.
        /// </para>
        /// </summary>
        Task<IReadOnlyList<string>> MembersOfAsync(Guid activityId, CancellationToken ct);
    }

    public class EventAudience(ApplicationDbContext context) : IEventAudience
    {
        public Task<IReadOnlyList<string>> InActivityAsync(
            Guid activityId, string permission, CancellationToken ct) =>
            HoldersAsync(g => g.ActivityId == activityId || g.ActivityId == null, permission, ct);

        public Task<IReadOnlyList<string>> AnywhereAsync(string permission, CancellationToken ct) =>
            HoldersAsync(_ => true, permission, ct);

        public async Task<IReadOnlyList<string>> MembersOfAsync(Guid activityId, CancellationToken ct) =>
            await context.Grants
                .AsNoTracking()
                .Where(g => g.ActivityId == activityId && g.State == GrantState.Active)
                .Select(g => g.UserId)
                .Distinct()
                .ToListAsync(ct);

        private async Task<IReadOnlyList<string>> HoldersAsync(
            System.Linq.Expressions.Expression<Func<Grant, bool>> scope,
            string permission,
            CancellationToken ct)
        {
            var grants = await context.Grants
                .AsNoTracking()
                .Where(g => g.State == GrantState.Active)
                .Where(scope)
                .Select(g => new { g.UserId, g.Permissions })
                .ToListAsync(ct);

            var holders = new HashSet<string>(StringComparer.Ordinal);

            foreach (var grant in grants)
            {
                // A grant whose permissions will not parse is a grant nobody can
                // be judged by. Skipped rather than thrown on: one bad row must
                // not stop everybody else being told.
                List<string> keys;
                try
                {
                    keys = JsonSerializer.Deserialize<List<string>>(grant.Permissions) ?? [];
                }
                catch (JsonException)
                {
                    continue;
                }

                // The administrator bypass is checked first and at every scope,
                // exactly as `PermissionService` checks it.
                if (keys.Contains(Permissions.SystemAdministrator) || keys.Contains(permission))
                {
                    holders.Add(grant.UserId);
                }
            }

            return [.. holders];
        }
    }
}

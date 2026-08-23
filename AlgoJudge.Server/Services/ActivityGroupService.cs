using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IActivityGroupService
    {
        Task<IReadOnlyList<ActivityGroupDto>> ListAsync(string activityIdOrSlug, CancellationToken ct);

        Task<ActivityGroupDto> CreateAsync(
            string activityIdOrSlug, ActivityGroupInputDto input, CancellationToken ct);

        Task<ActivityGroupDto> UpdateAsync(
            string activityIdOrSlug, Guid groupId, ActivityGroupInputDto input, CancellationToken ct);

        Task DeleteAsync(string activityIdOrSlug, Guid groupId, CancellationToken ct);

        /// <summary>Moves somebody into a group, or takes them out of every one.</summary>
        Task<GrantDto> AssignAsync(
            string activityIdOrSlug, string userId, GrantGroupInputDto input, CancellationToken ct);
    }

    /// <summary>
    /// Who competes as whom, inside one activity.
    /// <para>
    /// All of it answers to <c>activity:update</c>: a group is part of how an
    /// activity is set up, like its rounds and its limits, and whoever may
    /// configure the contest may decide who competes together in it.
    /// </para>
    /// </summary>
    public class ActivityGroupService(
        ApplicationDbContext context,
        IPermissionService permissions,
        IActivityService activities
    ) : IActivityGroupService
    {
        public async Task<IReadOnlyList<ActivityGroupDto>> ListAsync(
            string activityIdOrSlug, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            var groups = await context.ActivityGroups
                .AsNoTracking()
                .Where(g => g.ActivityId == activity.Id)
                .OrderBy(g => g.Name)
                .ToListAsync(ct);

            var ids = groups.Select(g => g.Id).ToList();

            // Counted in two queries rather than as two correlated subqueries per
            // row: a group roster is small and this screen is a manager's, not a
            // participant's.
            var members = await context.Grants.AsNoTracking()
                .Where(g => g.GroupId != null && ids.Contains(g.GroupId!.Value))
                .GroupBy(g => g.GroupId!.Value)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

            var sent = await context.Submissions.AsNoTracking()
                .Where(s => s.GroupId != null && ids.Contains(s.GroupId!.Value))
                .GroupBy(s => s.GroupId!.Value)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

            return groups.Select(g => Project(g, members, sent)).ToList();
        }

        public async Task<ActivityGroupDto> CreateAsync(
            string activityIdOrSlug, ActivityGroupInputDto input, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            var name = Named(input.Name);
            await RefuseDuplicateAsync(activity.Id, name, null, ct);

            var group = new ActivityGroup
            {
                ActivityId = activity.Id,
                Name = name,
                Description = Trimmed(input.Description),
                IsSystem = input.IsSystem,
            };
            context.ActivityGroups.Add(group);
            await context.SaveChangesAsync(ct);

            return Project(group, EmptyCounts, EmptyCounts);
        }

        public async Task<ActivityGroupDto> UpdateAsync(
            string activityIdOrSlug, Guid groupId, ActivityGroupInputDto input, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            var group = await FindAsync(activity.Id, groupId, ct);
            var name = Named(input.Name);
            await RefuseDuplicateAsync(activity.Id, name, groupId, ct);

            group.Name = name;
            group.Description = Trimmed(input.Description);
            // **Changeable after the fact, and it changes only what is shown.**
            // Marking a running group as system takes it out of the ranking and
            // leaves its submissions where they are; unmarking puts it back.
            group.IsSystem = input.IsSystem;
            await context.SaveChangesAsync(ct);

            return await OneAsync(group, ct);
        }

        /// <summary>
        /// Removes a group nobody has submitted under.
        /// <para>
        /// <b>A group with submissions is refused rather than removed.</b> The
        /// group stamped on a submission is the record of what competed, and
        /// deleting the row would make every one of them say it was sent by
        /// nobody — the database refuses it too, and this is the message that
        /// arrives before the constraint does.
        /// </para>
        /// <para>
        /// The way to retire one is to mark it system: it leaves the ranking and
        /// its history stays readable.
        /// </para>
        /// </summary>
        public async Task DeleteAsync(string activityIdOrSlug, Guid groupId, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            var group = await FindAsync(activity.Id, groupId, ct);

            var sent = await context.Submissions.CountAsync(s => s.GroupId == groupId, ct);
            if (sent > 0)
            {
                throw new ConflictException(
                    $"This group has sent {sent} submission(s) and is part of their record. "
                    + "Mark it as a system group to take it out of the ranking instead.",
                    "group.hasSubmissions");
            }

            context.ActivityGroups.Remove(group);
            await context.SaveChangesAsync(ct);
        }

        public async Task<GrantDto> AssignAsync(
            string activityIdOrSlug, string userId, GrantGroupInputDto input, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            var grant = await context.Grants
                .Include(g => g.User)
                .Include(g => g.Activity)
                .Include(g => g.SourceProvider)
                .FirstOrDefaultAsync(g => g.ActivityId == activity.Id && g.UserId == userId, ct)
                ?? throw new NotFoundException("Grant");

            if (input.GroupId is { Length: > 0 } declared)
            {
                if (!Guid.TryParse(declared, out var groupId)) throw new NotFoundException("Group");
                // Resolved inside this activity, so a group id from another
                // contest is a 404 rather than a cross-activity assignment.
                grant.Group = await FindAsync(activity.Id, groupId, ct);
                grant.GroupId = groupId;
            }
            else
            {
                grant.GroupId = null;
                grant.Group = null;
            }

            await context.SaveChangesAsync(ct);
            return GrantService.Projected(grant);
        }

        // ── shared ──────────────────────────────────────────────────────────

        private static readonly IReadOnlyDictionary<Guid, int> EmptyCounts =
            new Dictionary<Guid, int>();

        private async Task<ActivityGroup> FindAsync(Guid activityId, Guid groupId, CancellationToken ct) =>
            await context.ActivityGroups
                .FirstOrDefaultAsync(g => g.Id == groupId && g.ActivityId == activityId, ct)
            ?? throw new NotFoundException("Group");

        /// <summary>
        /// Two rows in one ranking may not carry one name. Caught here so the
        /// answer names the group rather than the index.
        /// </summary>
        private async Task RefuseDuplicateAsync(
            Guid activityId, string name, Guid? except, CancellationToken ct)
        {
            var taken = await context.ActivityGroups.AnyAsync(
                g => g.ActivityId == activityId && g.Name == name && g.Id != except, ct);
            if (taken)
            {
                throw new ConflictException(
                    $"Another group in this activity is already called '{name}'", "group.name.taken");
            }
        }

        private static string Named(string declared) =>
            Trimmed(declared) ?? throw new ValidationException("A group needs a name", "group.name");

        private static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private async Task<ActivityGroupDto> OneAsync(ActivityGroup group, CancellationToken ct)
        {
            var members = await context.Grants.CountAsync(g => g.GroupId == group.Id, ct);
            var sent = await context.Submissions.CountAsync(s => s.GroupId == group.Id, ct);
            return Project(
                group,
                new Dictionary<Guid, int> { [group.Id] = members },
                new Dictionary<Guid, int> { [group.Id] = sent });
        }

        private static ActivityGroupDto Project(
            ActivityGroup group,
            IReadOnlyDictionary<Guid, int> members,
            IReadOnlyDictionary<Guid, int> sent) => new()
            {
                Id = Wire.Id(group.Id),
                ActivityId = Wire.Id(group.ActivityId),
                Name = group.Name,
                Description = group.Description,
                IsSystem = group.IsSystem,
                MemberCount = members.TryGetValue(group.Id, out var m) ? m : 0,
                SubmissionCount = sent.TryGetValue(group.Id, out var s) ? s : 0,
                CreatedAt = Wire.At(group.CreatedAt),
            };
    }
}

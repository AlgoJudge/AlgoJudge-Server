using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>One course link, as a manager needs to see it.</summary>
    public record PlacementView
    {
        public required Guid Id { get; init; }
        public required Guid PlatformId { get; init; }
        public required string PlatformName { get; init; }
        public required string ContextTitle { get; init; }
        public required string ContextId { get; init; }
        public required Guid ActivityId { get; init; }
        public required string ActivitySlug { get; init; }
        public required string ActivityName { get; init; }

        /// <summary>
        /// Whether this activity is reached from more than one course at all.
        /// Carried separately from the acknowledgement so a screen can stay quiet
        /// about the ordinary case rather than asking about something that is not
        /// shared.
        /// </summary>
        public required bool Shared { get; init; }
        public required bool SharingAcknowledged { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    public interface IPlacementService
    {
        Task<IReadOnlyList<PlacementView>> ListAsync(Guid? activityId, CancellationToken ct);

        /// <summary>
        /// Accepts that this activity is reached from more than one course.
        /// <para>
        /// The other half of the decision of 2026-08-13. A second placement is
        /// allowed and is <b>not silent</b>: the launch refuses with
        /// <c>sharingNotAcknowledged</c> until somebody says yes, and this is the
        /// saying. Without it the refusal is a dead end — the flag exists, the
        /// launch reads it, and nothing can ever set it.
        /// </para>
        /// <para>
        /// Not reversible through this call, and deliberately so: withdrawing
        /// consent would leave a gradebook holding scores from an activity that
        /// no longer admits it feeds two. Detaching the placement is the honest
        /// way back, and that is the platform's own business.
        /// </para>
        /// </summary>
        Task<PlacementView> AcknowledgeSharingAsync(Guid id, CancellationToken ct);
    }

    public class PlacementService(
        LtiDbContext db,
        ApplicationDbContext core,
        IPermissionService permissions
    ) : IPlacementService
    {
        public async Task<IReadOnlyList<PlacementView>> ListAsync(
            Guid? activityId, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var links = await db.ResourceLinks.AsNoTracking()
                .Where(l => activityId == null || l.ActivityId == activityId)
                .OrderByDescending(l => l.CreatedAt)
                .ToListAsync(ct);

            return await ProjectAsync(links, ct);
        }

        public async Task<PlacementView> AcknowledgeSharingAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var link = await db.ResourceLinks.FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new NotFoundException("Placement");

            link.SharingAcknowledged = true;
            await db.SaveChangesAsync(ct);

            return (await ProjectAsync([link], ct))[0];
        }

        /// <summary>
        /// Names, fetched in two queries rather than per row. The platform lives
        /// in this module's context and the activity in the core's, so they
        /// cannot be joined — which is the cost of the module boundary, paid here
        /// where it is one extra read rather than in the launch path.
        /// </summary>
        private async Task<IReadOnlyList<PlacementView>> ProjectAsync(
            IReadOnlyList<ResourceLink> links, CancellationToken ct)
        {
            if (links.Count == 0) return [];

            var platformIds = links.Select(l => l.PlatformId).Distinct().ToList();
            var platforms = await db.Platforms.AsNoTracking()
                .Where(p => platformIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);

            var activityIds = links.Select(l => l.ActivityId).Distinct().ToList();
            var activities = await core.Activities.AsNoTracking()
                .Where(a => activityIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Slug, a.Name })
                .ToDictionaryAsync(a => a.Id, a => a, ct);

            // How many courses reach each activity, counted across every
            // placement rather than only the ones being projected.
            var counts = await db.ResourceLinks.AsNoTracking()
                .Where(l => activityIds.Contains(l.ActivityId))
                .GroupBy(l => l.ActivityId)
                .Select(g => new { ActivityId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.ActivityId, g => g.Count, ct);

            return links.Select(l => new PlacementView
            {
                Id = l.Id,
                PlatformId = l.PlatformId,
                PlatformName = platforms.GetValueOrDefault(l.PlatformId, ""),
                ContextTitle = l.ContextTitle ?? "",
                ContextId = l.ContextId ?? "",
                ActivityId = l.ActivityId,
                ActivitySlug = activities.GetValueOrDefault(l.ActivityId)?.Slug ?? "",
                ActivityName = activities.GetValueOrDefault(l.ActivityId)?.Name ?? "",
                Shared = counts.GetValueOrDefault(l.ActivityId, 1) > 1,
                SharingAcknowledged = l.SharingAcknowledged,
                CreatedAt = l.CreatedAt,
            }).ToList();
        }
    }
}

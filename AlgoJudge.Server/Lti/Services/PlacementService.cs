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

        /// <summary>
        /// The placement this one appears to be a copy of, if the platform said
        /// the course was copied from one we already know.
        ///
        /// <para>
        /// <b>A hint, never a conclusion.</b> The platform tells us which course
        /// this one was copied from; it does not tell us which activity in the
        /// copy corresponds to which in the original, because no version of
        /// Moodle carries a resource link history - measured 2026-08-15. With
        /// two AlgoJudge activities in the course somebody copied, this points at
        /// a course and a person decides the rest.
        /// </para>
        /// </summary>
        public Guid? LooksLikeCopyOf { get; init; }

        /// <summary>The course that placement is in, for the screen to name.</summary>
        public string? CopiedFromContext { get; init; }
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

        /// <summary>
        /// Gives this placement an activity of its own, copied from the one it
        /// currently points at, and points it there.
        ///
        /// <para>
        /// <b>The other answer to a copied course.</b> Accepting the sharing puts
        /// two cohorts into one activity, which is right when one offering runs
        /// in two courses and wrong when this year was copied from last year:
        /// last year's results would sit beside this year's in one table. This is
        /// the second case, and it is one act rather than two so that a placement
        /// cannot be left pointing at the wrong activity in between.
        /// </para>
        /// </summary>
        Task<PlacementView> CopyActivityAsync(
            Guid id, string slug, DateTime startsAt, CancellationToken ct);
    }

    public class PlacementService(
        LtiDbContext db,
        ApplicationDbContext core,
        IActivityService activities,
        IPermissionService permissions
    ) : IPlacementService
    {
        public async Task<PlacementView> CopyActivityAsync(
            Guid id, string slug, DateTime startsAt, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var link = await db.ResourceLinks.FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new NotFoundException("Placement");

            // The copy is made through the ordinary service, so it arrives
            // unpublished and with its dates moved like every other copy - and so
            // the rights over the activity being copied are checked there, once.
            var copy = await activities.DuplicateAsync(link.ActivityId, slug, startsAt, ct);

            link.ActivityId = Guid.Parse(copy.Id);
            // **Nothing is shared any more.** This placement is the only one
            // pointing at the copy, so the question the refusal was asking has
            // been answered by making it not apply.
            link.SharingAcknowledged = true;
            await db.SaveChangesAsync(ct);

            return (await ProjectAsync([link], ct)).Single();
        }

        public async Task<IReadOnlyList<PlacementView>> ListAsync(
            Guid? activityId, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var links = await db.ResourceLinks.AsNoTracking()
                .Where(l => activityId == null || l.ActivityId == activityId)
                .OrderByDescending(l => l.CreatedAt)
                .ToListAsync(ct);

            var projected = await ProjectAsync(links, ct);

            // Every placement of the same activity on the same platform, so a
            // history naming one of their courses can be matched to it.
            var byContext = links
                .GroupBy(l => (l.PlatformId, l.ActivityId, l.ContextId))
                .ToDictionary(g => g.Key, g => g.OrderBy(l => l.CreatedAt).First());

            return projected.Select(view =>
            {
                var link = links.First(l => l.Id == view.Id);
                var ancestor = Ancestors(link.ContextHistory)
                    .Select(context => byContext.TryGetValue(
                        (link.PlatformId, link.ActivityId, context), out var found) ? found : null)
                    .FirstOrDefault(found => found is not null && found.Id != link.Id);

                if (ancestor is null) return view;

                return view with
                {
                    LooksLikeCopyOf = ancestor.Id,
                    CopiedFromContext = ancestor.ContextTitle,
                };
            }).ToList();
        }

        /// <summary>
        /// The courses this one was copied from, newest first.
        ///
        /// <para>
        /// <b>A list, not a value.</b> Moodle sends `3,2` for a copy of a copy -
        /// measured on 5.2 (2026-08-15) - so a reader comparing the whole string
        /// works for the first copy of a course and stops working for the second,
        /// which is the ordinary case a year later.
        /// </para>
        /// </summary>
        private static IEnumerable<string> Ancestors(string? history) =>
            (history ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

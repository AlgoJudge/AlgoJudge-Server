using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti.Data;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    public interface IResourceLinkService
    {
        /// <summary>
        /// The placement this launch belongs to, creating it on first sight.
        /// Throws <see cref="LtiLaunchException"/> where the launch names no
        /// activity, or names one placed elsewhere without anybody accepting it.
        /// </summary>
        Task<ResourceLink> ResolveAsync(LaunchedMessage launch, CancellationToken ct);
    }

    /// <summary>
    /// Binding a course's activity link to an AlgoJudge activity.
    /// <para>
    /// The link names the activity <b>by its slug</b>, from a custom parameter
    /// the teacher typed when placing it (§5.1). A slug rather than an id
    /// because it is the stable, human-readable alias already used in URLs —
    /// which is what makes a course copied into next year carry a parameter that
    /// still means something.
    /// </para>
    /// </summary>
    public class ResourceLinkService(LtiDbContext db, ApplicationDbContext core) : IResourceLinkService
    {
        public async Task<ResourceLink> ResolveAsync(LaunchedMessage launch, CancellationToken ct)
        {
            var existing = await db.ResourceLinks.FirstOrDefaultAsync(
                l => l.PlatformId == launch.Platform.Id
                     && l.PlatformResourceLinkId == launch.ResourceLinkId, ct);

            if (existing is not null)
            {
                // A second placement waiting on somebody's decision does not
                // launch. Anything else would be one activity quietly feeding two
                // gradebooks while a manager believed it fed one.
                if (!existing.SharingAcknowledged && await SharedAsync(existing, ct))
                {
                    throw new LtiLaunchException(LtiLaunchException.SharingNotAcknowledged,
                        "This activity is placed in more than one course and nobody has accepted that yet");
                }

                // Recorded on every launch rather than once: a course copied
                // between two launches changes it, and milestone 4 reads it.
                var changed = false;
                if (launch.ContextHistory is not null && existing.ContextHistory != launch.ContextHistory)
                {
                    existing.ContextHistory = launch.ContextHistory;
                    changed = true;
                }

                // Refreshed on every launch too. A tool re-registered with the
                // AGS scopes starts carrying this, and a placement made before
                // that would otherwise never learn where the gradebook is.
                var endpoint = AgsEndpoint.Parse(launch.AgsEndpointJson)?.LineItems;
                if (endpoint is not null && existing.AgsLineItemsUrl != endpoint)
                {
                    existing.AgsLineItemsUrl = endpoint;
                    changed = true;
                }

                var roster = NrpsEndpoint.Parse(launch.NrpsJson)?.ContextMemberships;
                if (roster is not null && existing.NrpsMembershipsUrl != roster)
                {
                    existing.NrpsMembershipsUrl = roster;
                    changed = true;
                }

                if (changed) await db.SaveChangesAsync(ct);

                return existing;
            }

            if (string.IsNullOrWhiteSpace(launch.ActivitySlug))
            {
                // The commonest configuration mistake there is, and it deserves
                // its own answer: the teacher placed the activity and left the
                // custom parameter out.
                throw new LtiLaunchException(LtiLaunchException.NoActivity,
                    "The launch named no activity. The placement needs a custom parameter `activity=<slug>`");
            }

            var slug = launch.ActivitySlug.Trim();
            var activity = await core.Activities.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Slug == slug, ct)
                ?? throw new LtiLaunchException(LtiLaunchException.NoActivity,
                    $"No activity has the slug \"{slug}\"");

            // **A second placement is allowed and is not silent** — decided
            // 2026-08-13. A shared problem set across two groups is real, so this
            // is not refused; what it is not allowed to do is happen unnoticed,
            // because one activity then reaches two gradebooks and §7's enrolment
            // has two rosters.
            var alreadyPlaced = await db.ResourceLinks.AnyAsync(l => l.ActivityId == activity.Id, ct);

            var link = new ResourceLink
            {
                PlatformId = launch.Platform.Id,
                PlatformResourceLinkId = launch.ResourceLinkId,
                ContextId = launch.ContextId,
                ContextTitle = launch.ContextTitle,
                ContextHistory = launch.ContextHistory,
                ActivityId = activity.Id,
                AgsLineItemsUrl = AgsEndpoint.Parse(launch.AgsEndpointJson)?.LineItems,
                NrpsMembershipsUrl = NrpsEndpoint.Parse(launch.NrpsJson)?.ContextMemberships,
                // True on the first placement, because there is nothing to
                // accept. False on a later one until a manager says so.
                SharingAcknowledged = !alreadyPlaced,
            };

            db.ResourceLinks.Add(link);
            await db.SaveChangesAsync(ct);

            if (alreadyPlaced)
            {
                throw new LtiLaunchException(LtiLaunchException.SharingNotAcknowledged,
                    "This activity is already placed in another course. A manager has to accept that before it launches here");
            }

            return link;
        }

        private async Task<bool> SharedAsync(ResourceLink link, CancellationToken ct) =>
            await db.ResourceLinks.CountAsync(l => l.ActivityId == link.ActivityId, ct) > 1;
    }
}

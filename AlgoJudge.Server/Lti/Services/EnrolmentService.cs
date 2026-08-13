using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Lti.Data;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    public interface ILtiEnrolmentService
    {
        /// <summary>
        /// Puts this person in this activity, at the strength the platform's
        /// roles point at. Never removes anything.
        /// </summary>
        Task EnrolAsync(
            ResourceLink link, Guid providerId, string userId,
            IReadOnlyList<string> roles, CancellationToken ct);
    }

    /// <summary>
    /// Membership from a launch.
    /// <para>
    /// <b>A launch creates a grant and never revokes one</b> (§7). Somebody
    /// unenrolled at the platform keeps what they did here; taking an activity
    /// away from a person mid-course because a roster changed is a decision, and
    /// a decision needs somebody to make it.
    /// </para>
    /// <para>
    /// The row is written straight to <c>Grants</c> rather than through
    /// <c>IGrantService</c>, and that is not a shortcut: <c>GrantInputDto</c>
    /// deliberately carries no provider field, because that endpoint writes the
    /// <i>manual</i> contribution and only that one. This one is a managed
    /// contribution — sourced by the platform, rewritten from its roles — which
    /// is the same shape <c>FederatedSignInService</c> already writes at system
    /// scope for an OIDC provider.
    /// </para>
    /// </summary>
    public class EnrolmentService(ApplicationDbContext core) : ILtiEnrolmentService
    {
        public async Task EnrolAsync(
            ResourceLink link, Guid providerId, string userId,
            IReadOnlyList<string> roles, CancellationToken ct)
        {
            // Passed in rather than navigated to: the platform and the provider
            // row live in two different contexts, so there is no join to make
            // here — and that separation is what keeps this module deletable.
            var platform = await core.IdentityProviders.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == providerId, ct);

            // The platform's provider row is what makes this grant attributable.
            // Without it the grant would look manual, and "who put this person in
            // the course" would have no answer.
            if (platform is null)
            {
                return;
            }

            var template = LtiRoles.RunsTheCourse(roles) ? "manager" : "participant";

            var permissions = await core.PermissionTemplates.AsNoTracking()
                .Where(t => t.Name == template)
                .Select(t => t.Permissions)
                .FirstOrDefaultAsync(ct);

            if (permissions is null)
            {
                return;
            }

            var ordered = (JsonSerializer.Deserialize<List<string>>(permissions) ?? [])
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            var json = JsonSerializer.Serialize(ordered);

            var grant = await core.Grants.FirstOrDefaultAsync(
                g => g.UserId == userId
                     && g.ActivityId == link.ActivityId
                     && g.SourceProviderId == platform.Id, ct);

            if (grant is null)
            {
                core.Grants.Add(new Grant
                {
                    UserId = userId,
                    ActivityId = link.ActivityId,
                    SourceProviderId = platform.Id,
                    Permissions = json,
                    // Computed here, never taken from anywhere else: a grant
                    // carrying any permission an ordinary participant does not
                    // hold is systemic, and a jury member counted among the
                    // competitors is a bug rather than a preference.
                    IsSystem = Permissions.IsStaff(ordered),
                    CreatedFromTemplate = template,
                    // **No override.** The flag means "this grant is
                    // authoritative inside this activity, and system
                    // contributions do not reach it", which is a demotion
                    // somebody has to choose. A launch demoting a system manager
                    // because Moodle called them a student would be the platform
                    // deciding something about this installation.
                    OverrideSystem = false,
                });
            }
            else if (grant.Permissions != json)
            {
                // Rewritten from the platform's roles, the same way a provider's
                // system contribution is rewritten at every sign-in. A teacher
                // who became a student in Moodle is a student here at their next
                // launch — within this contribution, which adds to whatever else
                // they hold rather than replacing it.
                grant.Permissions = json;
                grant.IsSystem = Permissions.IsStaff(ordered);
                grant.CreatedFromTemplate = template;
            }

            await core.SaveChangesAsync(ct);
        }
    }
}

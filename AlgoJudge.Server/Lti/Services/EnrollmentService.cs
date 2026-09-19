using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    public interface ILtiEnrollmentService
    {
        /// <summary>
        /// Puts this person in this activity, with the roles the platform's
        /// rules name. <b>Adds and never removes.</b>
        /// </summary>
        Task<EnrollmentOutcome> EnrollAsync(
            ResourceLink link, Guid providerId, string userId,
            IReadOnlyList<string> roles, CancellationToken ct);

        /// <summary>
        /// What a launch's roles would grant in this activity, before anybody is
        /// signed in. Read by the launch to decide whether an unpublished
        /// activity may be entered.
        /// </summary>
        Task<ResolvedRoles> PlanAsync(
            Guid activityId, Guid providerId, IReadOnlyList<string> roles, CancellationToken ct);
    }

    /// <summary>
    /// Membership from a launch.
    /// <para>
    /// <b>A launch adds roles and never takes one away</b> (§7, amended
    /// 2026-09-19). Somebody demoted at the platform keeps what a manager gave
    /// them here; taking something away from a person mid-course is a decision,
    /// and a decision needs somebody to make it. The platform's word is enough
    /// to add, because that is what it is authoritative about: who is in the
    /// course.
    /// </para>
    /// <para>
    /// <b>A role a manager removed is not added back.</b> The removal is kept on
    /// the link, and this skips it — otherwise a correction would last until
    /// that student's next launch, which is a change that silently reverts.
    /// </para>
    /// <para>
    /// The grant is written through <c>IGrantService.AddRolesAsync</c> rather
    /// than through the context here: one grant per person per activity, one
    /// place that derives the staff flag, and one place that announces the
    /// change. Writing it here is how this module came to own a row the panel
    /// could not then edit.
    /// </para>
    /// </summary>
    public class EnrollmentService(
        ApplicationDbContext core,
        IGrantService grants,
        IPlatformRoleRules rules
    ) : ILtiEnrollmentService
    {
        public Task<ResolvedRoles> PlanAsync(
            Guid activityId, Guid providerId, IReadOnlyList<string> roles, CancellationToken ct) =>
            rules.ResolveAsync(providerId, activityId, roles, ct);

        public async Task<EnrollmentOutcome> EnrollAsync(
            ResourceLink link, Guid providerId, string userId,
            IReadOnlyList<string> roles, CancellationToken ct)
        {
            // Passed in rather than navigated to: the platform and the provider
            // row live in two different contexts, so there is no join to make
            // here — and that separation is what keeps this module deletable.
            //
            // The platform's provider row is what makes these roles
            // attributable. Without it a launch would leave roles nobody could
            // explain the source of.
            var platform = await core.IdentityProviders
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == providerId, ct);

            if (platform is null) return EnrollmentOutcome.Unchanged;

            var planned = await rules.ResolveAsync(providerId, link.ActivityId, roles, ct);
            if (planned.RoleIds.Count == 0) return EnrollmentOutcome.Unchanged;

            return await grants.AddRolesAsync(
                userId, link.ActivityId, planned.RoleIds, platform.Id, ct);
        }
    }
}

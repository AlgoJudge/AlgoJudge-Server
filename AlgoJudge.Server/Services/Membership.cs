using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Whether somebody is <b>in</b> an activity.
    /// <para>
    /// <b>Membership is not a permission</b>, and the difference is the whole of
    /// this file. A permission answers "may you do this kind of thing", and a
    /// system grant answers it in every activity at once, because that is what
    /// system scope means. Being in a course is a different question, and the
    /// permission model does not answer it: there is no membership table, so the
    /// grant row <i>is</i> the membership.
    /// </para>
    /// <para>
    /// <c>SubmissionService</c> asked this and nothing else did. An installation
    /// that maps an identity provider's group to the participant role — a
    /// contribution <c>FederatedSignInService</c> writes at system scope, with no
    /// activity — therefore handed every such account a reader's view of every
    /// activity on the installation: the board, the rounds, the statements and
    /// the question queue. Found by the authorization audit of 2026-09-09.
    /// </para>
    /// </summary>
    public static class Membership
    {
        /// <summary>An active grant of this person in this activity.</summary>
        public static Task<bool> HasAsync(
            ApplicationDbContext context, Guid activityId, string userId, CancellationToken ct) =>
            context.Grants
                .AsNoTracking()
                .AnyAsync(g => g.ActivityId == activityId
                    && g.UserId == userId
                    && g.State == GrantState.Active, ct);

        /// <summary>
        /// Membership, or staff standing in this activity.
        /// <para>
        /// <b>Staff are not members and must not have to be.</b> Somebody running
        /// a course reads its board without competing in it, and an
        /// administrator is staff everywhere by the bypass. The catalog already
        /// draws that line — <c>Permissions.IsStaff</c>, which is "seeing or
        /// changing other people's work" — so this asks it rather than naming a
        /// key per call site and drifting.
        /// </para>
        /// <para>
        /// The audience of the refusal is therefore exactly the hole: somebody
        /// holding a participant's keys from a system grant, in an activity they
        /// have not joined.
        /// </para>
        /// <para>
        /// Stricter than this in one place on purpose: <b>submitting</b> requires
        /// membership with no staff escape, because an administrator who wants to
        /// compete takes a grant rather than a bypass.
        /// </para>
        /// </summary>
        public static async Task RequireAsync(
            ApplicationDbContext context, IPermissionService permissions,
            Guid activityId, string userId, CancellationToken ct)
        {
            if (await HasAsync(context, activityId, userId, ct)) return;

            var here = await permissions.EffectiveAsync(activityId, ct);
            if (Authorization.Permissions.IsStaff(here)) return;

            // `ForbiddenActionException` rather than `AccessDeniedException`: the
            // caller does hold the permission, and what they lack is membership.
            // `enrollment.required` is the code `SubmissionService` already
            // refuses in and the Client turns into "join first".
            throw new ForbiddenActionException(
                "Only somebody enrolled in this activity may read it", "enrollment.required");
        }
    }
}

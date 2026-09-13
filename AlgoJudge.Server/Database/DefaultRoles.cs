using System.Text.Json;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Database
{
    /// <summary>
    /// Which role an enrolment hands out, for the paths that enrol somebody
    /// without anybody choosing.
    /// <para>
    /// Self-enrolment, the creator of an activity, a bulk of temporary accounts
    /// and an LTI launch all used to serialise a copy of a compiled-in list.
    /// They now link, which is what makes an activity's own role reach anybody:
    /// a manager sets it once here and every later enrolment carries it.
    /// </para>
    /// <para>
    /// <b>Null is a real answer and callers must handle it.</b> A database with
    /// no shipped roles — a test that never ran the seeder — gets a grant holding
    /// its own copy instead, which is what these paths did before. Enrolling
    /// somebody with no permissions at all would be worse than either.
    /// </para>
    /// </summary>
    public static class DefaultRoles
    {
        public const string Participant = "participant";
        public const string Manager = "manager";
        public const string Admin = "admin";

        /// <summary>One of the shipped roles, by name. Global scope only.</summary>
        public static Task<Role?> GlobalAsync(
            ApplicationDbContext context, string name, CancellationToken ct) =>
            context.PermissionRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ActivityId == null && r.Name == name, ct);

        /// <summary>
        /// What somebody joining this activity is given: the activity's own
        /// choice, or the shipped role of that kind.
        /// </summary>
        /// <param name="runsIt">
        /// Whether they are joining to run the activity rather than to take part
        /// in it — an LTI instructor, or whoever created it.
        /// </param>
        public static async Task<Role?> ForEnrolmentAsync(
            ApplicationDbContext context, Guid activityId, bool runsIt, CancellationToken ct)
        {
            var activity = await context.Activities
                .AsNoTracking()
                .Where(a => a.Id == activityId)
                .Select(a => new { a.ParticipantRoleId, a.ManagerRoleId })
                .FirstOrDefaultAsync(ct);

            var chosen = runsIt ? activity?.ManagerRoleId : activity?.ParticipantRoleId;
            if (chosen is { } id)
            {
                var picked = await context.PermissionRoles
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == id, ct);
                if (picked is not null) return picked;
            }

            return await GlobalAsync(context, runsIt ? Manager : Participant, ct);
        }

        /// <summary>
        /// Points a grant at a role, or fills it in from the compiled-in list
        /// where that role is missing.
        /// <para>
        /// The fallback is what these paths did before roles existed, and it is
        /// kept for one case only: a database whose roles have not been seeded.
        /// Enrolling somebody into an activity with no permissions at all would
        /// look like a membership and behave like a lockout.
        /// </para>
        /// </summary>
        public static void Carry(
            Grant grant, Role? role, IReadOnlyList<string> fallback, string? name)
        {
            if (role is not null)
            {
                grant.RoleId = role.Id;
                grant.Permissions = "[]";
                return;
            }

            grant.Permissions = JsonSerializer.Serialize(fallback);
            grant.CopiedFromRoleName = name;
        }
    }
}

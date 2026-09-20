using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Database
{
    /// <summary>
    /// Which roles an enrollment hands out, for the paths that enroll somebody
    /// without anybody choosing.
    /// <para>
    /// Self-enrollment, the creator of an activity, a bulk of temporary accounts
    /// and an LTI rule aimed at a slot all ask here. An activity may name its
    /// own; where it names none, the shipped role of that kind answers.
    /// </para>
    /// <para>
    /// <b>The shipped roles are found by <see cref="Role.BuiltInKey"/>, never by
    /// name.</b> By name, renaming the <c>participant</c> role — which nothing
    /// refused — made every one of these paths find nothing: a launch landed,
    /// wrote no grant, and said so to nobody.
    /// </para>
    /// </summary>
    public static class DefaultRoles
    {
        public const string Participant = "participant";
        public const string Manager = "manager";
        public const string Admin = "admin";

        /// <summary>One of the shipped roles, by its key. Installation scope only.</summary>
        public static Task<Role?> BuiltInAsync(
            ApplicationDbContext context, string builtInKey, CancellationToken ct) =>
            context.PermissionRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.BuiltInKey == builtInKey, ct);

        /// <summary>
        /// What somebody joining this activity is given: the activity's own
        /// choice, or the shipped role of that kind.
        /// <para>
        /// <b>Empty is a real answer and callers must handle it.</b> A database
        /// with no shipped roles — a test that never ran the seeder — gets
        /// nothing, and enrolling somebody with no permissions is better than
        /// pretending a role exists.
        /// </para>
        /// </summary>
        /// <param name="runsIt">
        /// Whether they are joining to run the activity rather than to take part
        /// in it — an LTI instructor, or whoever created it.
        /// </param>
        public static Task<IReadOnlyList<Role>> ForEnrollmentAsync(
            ApplicationDbContext context, Guid activityId, bool runsIt, CancellationToken ct) =>
            InSlotAsync(
                context,
                activityId,
                runsIt ? EnrollmentSlot.Managers : EnrollmentSlot.Participants,
                ct);

        /// <summary>
        /// The roles in one of an activity's slots, or the shipped role of that
        /// kind where the slot is empty.
        /// </summary>
        public static async Task<IReadOnlyList<Role>> InSlotAsync(
            ApplicationDbContext context, Guid activityId, EnrollmentSlot slot, CancellationToken ct)
        {
            var chosen = await context.ActivityEnrollmentRoles
                .AsNoTracking()
                .Where(r => r.ActivityId == activityId && r.Slot == slot)
                .Select(r => r.Role!)
                .ToListAsync(ct);

            if (chosen.Count > 0) return chosen;

            var shipped = await BuiltInAsync(
                context, slot == EnrollmentSlot.Managers ? Manager : Participant, ct);
            return shipped is null ? [] : [shipped];
        }

        /// <summary>
        /// Points a grant at these roles. A grant that links none holds whatever
        /// its own entries say, which for an enrollment is nothing at all — so a
        /// caller that gets an empty list has an installation with no shipped
        /// roles and should say so rather than write a membership that confers
        /// nothing.
        /// </summary>
        /// <param name="context">
        /// The link is added to the set as well as to the grant. A dependent
        /// discovered through a navigation is <b>Modified</b> when its key is
        /// already set, and every id here is assigned in the constructor — so a
        /// link added only through the collection is written as an update to a
        /// row that does not exist, and the save fails with a concurrency
        /// conflict on a grant nobody else touched.
        /// </param>
        public static void Carry(
            ApplicationDbContext context, Grant grant, IReadOnlyList<Role> roles, DateTime at)
        {
            foreach (var role in roles)
            {
                if (grant.Roles.Any(r => r.RoleId == role.Id)) continue;

                var link = new GrantRole { GrantId = grant.Id, RoleId = role.Id, AddedAt = at };
                grant.Roles.Add(link);
                context.GrantRoles.Add(link);
            }
        }
    }
}

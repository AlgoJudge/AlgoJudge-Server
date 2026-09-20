using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>Which of an activity's two enrollment sets a role belongs to.</summary>
    public enum EnrollmentSlot
    {
        /// <summary>What somebody joining to take part is given.</summary>
        Participants = 0,

        /// <summary>What somebody joining to run the activity is given.</summary>
        Managers = 1,
    }

    /// <summary>
    /// A role an activity enrolls into, in one of its two slots.
    /// <para>
    /// A table rather than two columns because a slot holds <b>several</b>
    /// roles: an activity may want its participants to carry the shipped role
    /// and its own "may print" role at once. An empty slot means the shipped
    /// role of that kind, which is what every activity that has never been
    /// configured wants.
    /// </para>
    /// <para>
    /// Read by self-enrollment, by the grant an activity's creator is given, by
    /// bulk temporary accounts, and by an LTI rule whose target is a slot rather
    /// than a named role.
    /// </para>
    /// </summary>
    public class ActivityEnrollmentRole
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ActivityId { get; set; }
        public Activity? Activity { get; set; }

        public EnrollmentSlot Slot { get; set; }

        public Guid RoleId { get; set; }
        public Role? Role { get; set; }
    }
}

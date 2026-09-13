using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A named permission set a <see cref="Grant"/> points at.
    /// <para>
    /// <b>A grant holds the link, not a copy</b>, so editing a role changes what
    /// everyone linked to it may do, at once. That is the point: correcting what
    /// a manager may do used to be a bulk update across grants that nothing
    /// reminded anybody to run, and a permission added by an upgrade reached
    /// nobody already enrolled.
    /// </para>
    /// <para>
    /// The cost is that a role fails <b>open</b>: one edit widens many people
    /// silently. Four things answer that rather than a comment — a role belongs
    /// either to the installation or to one activity, so a manager's reach ends
    /// at their own group; nobody may put into a role a permission they do not
    /// themselves hold; <see cref="Grant.IsSystem"/> is recomputed for every
    /// linked grant on every edit, so an edit cannot leave a participant counted
    /// as a competitor while holding staff keys; and the panel says how many
    /// grants an edit reaches before it is saved.
    /// </para>
    /// <para>
    /// A grant may still hold its own permissions instead, or as well — which is
    /// what every hand-made set does, and what the ones made before roles
    /// existed kept.
    /// </para>
    /// <para>
    /// The permission vocabulary itself lives in
    /// <see cref="Authorization.Permissions"/>, not here: it is what the Server
    /// enforces, and a second copy beside the enforcement is a second copy that
    /// can disagree with it.
    /// </para>
    /// </summary>
    public class Role
    {
        public Guid Id { get; set; } = Uuid.New();

        public required string Name { get; set; }

        public string? Description { get; set; }

        /// <summary>
        /// Null for a role the whole installation shares; otherwise the activity
        /// that owns it.
        /// <para>
        /// An activity's role may only be linked from a grant on that activity,
        /// and is edited by whoever holds <c>role:manage</c> there. It is how a
        /// manager changes what their own participants may do without rewriting
        /// what every participant in the installation may do — the difference
        /// between a correction and an accident.
        /// </para>
        /// <para>
        /// It may not carry <c>system:administrator</c>, for the same reason an
        /// activity grant may not: the key is global, and reaching it from
        /// inside one activity would be a way out of that activity.
        /// </para>
        /// </summary>
        public Guid? ActivityId { get; set; }
        public Activity? Activity { get; set; }

        /// <summary>
        /// A <c>jsonb</c> array of permission strings in the form
        /// <c>problem:read:all</c>. Strings rather than columns: an installation
        /// can invent a role, and a schema that enumerated permissions in
        /// columns could not express one that did not exist when the migration
        /// was written.
        /// </summary>
        public string Permissions { get; set; } = "[]";

        /// <summary>One of the three shipped. Marked so deleting one can be refused.</summary>
        public bool IsBuiltIn { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

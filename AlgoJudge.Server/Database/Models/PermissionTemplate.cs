using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A named permission set used to fill in a <see cref="Grant"/>.
    /// <para>
    /// Three ship with the installation — participant, manager and administrator
    /// — and an administrator may add more. Choosing one **copies** its
    /// permissions into the grant; nothing points back here afterwards, so
    /// editing a template later touches nobody who already used it.
    /// </para>
    /// <para>
    /// That is the whole reason this is a template rather than a role: a role
    /// that grants keep pointing at is an object somebody has to keep correct for
    /// the lifetime of the installation. The cost is that a correction has to be
    /// applied to existing grants by hand — a single update, since permissions
    /// are plain strings, but not an automatic one.
    /// </para>
    /// </summary>
    public class PermissionTemplate
    {
        public Guid Id { get; set; } = Uuid.New();

        public required string Name { get; set; }

        public string? Description { get; set; }

        /// <summary>
        /// A <c>jsonb</c> array of permission strings in the form
        /// <c>problem:read:all@activity</c>. Strings rather than columns: an
        /// installation can invent a template, and a schema that enumerated
        /// permissions in columns could not express one that did not exist when
        /// the migration was written.
        /// </summary>
        public string Permissions { get; set; } = "[]";

        /// <summary>One of the three shipped. Marked so deleting one can be refused.</summary>
        public bool IsBuiltIn { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Permission strings the Server itself has to recognise.</summary>
    public static class Permissions
    {
        /// <summary>
        /// Bypasses every check, at every scope — a system-scope grant holding
        /// this has full rights inside every activity.
        /// <para>
        /// A permission rather than a flag on a grant, so it needs no column of
        /// its own. Because a grant is a flat set, an administrator cannot be
        /// partially crippled: either they hold this or they do not.
        /// </para>
        /// </summary>
        public const string SystemAdministrator = "system:administrator";
    }
}

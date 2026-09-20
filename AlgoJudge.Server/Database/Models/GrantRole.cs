using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// One role a <see cref="Grant"/> links. A grant may link several, and what
    /// its holder may do is the union of them and the grant's own entries.
    /// <para>
    /// Several rather than one because the two paths that decide what somebody
    /// holds both speak in sets: a claim may match several mapping rules, and an
    /// LTI launch may carry several roles. A single link could express neither,
    /// which is why a provider's contribution used to be a copy that went stale
    /// between sign-ins.
    /// </para>
    /// <para>
    /// Nothing subtracts here either. A union is commutative, so there is no
    /// ordering to decide and no precedence to explain; "where did this right
    /// come from" is answered by the rows, not by a rule.
    /// </para>
    /// </summary>
    public class GrantRole
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid GrantId { get; set; }
        public Grant? Grant { get; set; }

        public Guid RoleId { get; set; }
        public Role? Role { get; set; }

        /// <summary>
        /// Who put this role here: null for a person, otherwise the provider or
        /// the platform that asserted it.
        /// <para>
        /// It sits on the link rather than on the grant because one grant may
        /// now hold roles from two sources at once — a manager's own decision
        /// beside what a course asserted — and the panel has to be able to say
        /// which is which.
        /// </para>
        /// </summary>
        public Guid? SourceProviderId { get; set; }
        public IdentityProvider? SourceProvider { get; set; }

        public DateTime AddedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When a person took this role away, or null while it applies.
        /// <para>
        /// <b>A tombstone rather than a deleted row</b>, and it exists for one
        /// case: an LTI launch adds the roles a platform's rules name and never
        /// removes one, so without this a manager's correction would come back
        /// at the student's next launch. A change that silently reverts is a
        /// change nobody can trust, and the column is what that costs.
        /// </para>
        /// <para>
        /// Granting the role again by hand clears it: the mark says "somebody
        /// decided against this", and re-adding it is that decision reversed.
        /// A dismissed link never blocks deleting a role — it is a record of
        /// something that is not held.
        /// </para>
        /// </summary>
        public DateTime? DismissedAt { get; set; }
    }
}

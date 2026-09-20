using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    public enum GrantState
    {
        /// <summary>Offered, not yet accepted. The user is not in the activity yet.</summary>
        Invited = 0,
        Active = 1,
    }

    /// <summary>
    /// What a user may do within a scope — and, for an activity, <b>is</b> the
    /// membership.
    /// <para>
    /// Adding someone to an activity and giving them rights in it are one act, so
    /// there is no membership table beside this one. Two tables that both answer
    /// "is this person in this activity" can disagree; one cannot.
    /// </para>
    /// <para>
    /// This is not an access control list. An ACL hangs off a resource and lists
    /// who may touch that one thing; a grant hangs off a user and says what they
    /// may do within a scope. Nothing here is attached to a problem or a
    /// submission.
    /// </para>
    /// </summary>
    public class Grant
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// Deletion is anonymization, so this stays resolvable after the account
        /// it names has been emptied and a past participant keeps their place in
        /// an activity's history.
        /// </summary>
        public required string UserId { get; set; }
        public User? User { get; set; }

        /// <summary>Null for a system-scope grant, which applies in every activity.</summary>
        public Guid? ActivityId { get; set; }
        public Activity? Activity { get; set; }

        /// <summary>
        /// Where this contribution came from: <b>null is the manual one</b>, set
        /// by a person; anything else names the identity provider that asserted
        /// it.
        /// <para>
        /// At system scope a user holds one contribution per source, and their
        /// permissions there are the <b>union</b> of all of them. This is the one
        /// place the model is additive, and it is why the row is no longer unique
        /// on the user alone.
        /// </para>
        /// <para>
        /// A managed contribution is <b>rewritten from its provider's mapping at
        /// every sign-in and is not editable by hand</b>. Editing one would last
        /// until that person next signed in, which is worse than refusing:
        /// a change that silently reverts is a change nobody can trust.
        /// </para>
        /// <para>
        /// <b>Null at activity scope, always</b>, and a database constraint says
        /// so. An activity has one grant per person whoever wrote it, so a
        /// source on the row would claim the whole membership for one platform;
        /// what a platform asserted is recorded on the roles it added instead —
        /// <see cref="GrantRole.SourceProviderId"/>.
        /// </para>
        /// </summary>
        public Guid? SourceProviderId { get; set; }
        public IdentityProvider? SourceProvider { get; set; }

        /// <summary>
        /// This activity grant is <b>authoritative inside its activity</b>, and
        /// system contributions do not reach it. Meaningless at system scope.
        /// <para>
        /// A flag rather than the mere presence of an activity grant: a system
        /// manager added to a course so that they can see it should not be
        /// demoted by the act of being added. A demotion has to be somebody's
        /// decision, and a decision needs a field.
        /// </para>
        /// <para>
        /// <b>Not even <c>system:administrator</c> bypasses it</b> — but only its
        /// holder may have set it on an administrator's grant, so an
        /// administrator's rights still cannot be trimmed from below. It is how
        /// "a manager everywhere, except in this contest where I compete" is
        /// expressed, and it replaces the <c>deny</c> list that used to say it.
        /// </para>
        /// <para>
        /// It can strand its holder: inside that activity they are whatever the
        /// grant says — typically a participant, holding no <c>grant:update</c> —
        /// so clearing it needs another manager of that activity or a system
        /// administrator.
        /// </para>
        /// </summary>
        public bool OverrideSystem { get; set; }

        /// <summary>
        /// The roles this grant links. Empty for a grant that holds its own set
        /// alone, which is what a hand-made one may still be.
        /// <para>
        /// <b>Links, not copies</b>: editing a role changes what everybody
        /// linked to it may do, without anything touching these rows. Several,
        /// because both paths that write a grant speak in sets — a claim may
        /// match several mapping rules, and a launch may carry several roles.
        /// </para>
        /// <para>
        /// A link marked <see cref="GrantRole.DismissedAt"/> is not held; it
        /// records that somebody took that role away, so an LTI launch does not
        /// put it back.
        /// </para>
        /// </summary>
        public ICollection<GrantRole> Roles { get; set; } = new List<GrantRole>();

        /// <summary>
        /// This grant's own permissions, as a <c>jsonb</c> array of strings.
        /// <para>
        /// With roles linked these are <b>additions</b> to them, and what
        /// somebody holds is the union of all of it: giving one person one extra
        /// key does not cut them off from a role's corrections. With no role
        /// linked this is the whole set.
        /// </para>
        /// <para>
        /// Nothing subtracts. "A manager without the right to update something"
        /// is a role of their own, or their own set, and never a delta against
        /// somebody else's.
        /// </para>
        /// </summary>
        public string Permissions { get; set; } = "[]";

        /// <summary>
        /// A membership that runs the activity rather than takes part in it.
        /// <para>
        /// <b>Derived on every write</b>, never accepted from the caller: a grant
        /// carrying any permission an ordinary participant does not hold is
        /// systemic, always. A jury member counted among the competitors is a
        /// bug, not a preference — so this is what excludes them from the
        /// participant count and from the results feed.
        /// </para>
        /// <para>
        /// <c>IsSystem = StaffByHand || IsStaff(effective)</c>, and it is
        /// recomputed in both directions: a role edit that made a set systemic
        /// raises it, and undoing that edit lowers it again. What a person
        /// decided is in <see cref="StaffByHand"/>, which is why the two can no
        /// longer be confused for each other.
        /// </para>
        /// </summary>
        public bool IsSystem { get; set; }

        /// <summary>
        /// Somebody's decision that this membership is staff whatever its
        /// permissions say — a jury member holding nothing but a participant's
        /// keys.
        /// <para>
        /// It used to live inside <see cref="IsSystem"/>, which is why that flag
        /// could be raised by a role edit and never lowered: the column held two
        /// things and the recompute could not tell them apart. Split, so a
        /// correction to a role is as undoable as it was reversible.
        /// </para>
        /// </summary>
        public bool StaffByHand { get; set; }

        /// <summary>
        /// The group this person competes as in this activity, or null for
        /// somebody competing as themselves.
        /// <para>
        /// <b>Here because a grant is the assignment to an activity</b>, which is
        /// where the owner put it: being in a group is a fact about taking part
        /// in this contest, not a property of the account. And because the table
        /// already holds one grant per user per activity, this field <i>is</i>
        /// the rule "at most one group" — there is no second constraint to keep
        /// in step with it.
        /// </para>
        /// <para>
        /// A manager may change it at any time. What that does <b>not</b> do is
        /// move work already sent: a submission stamps its group when it is made
        /// and keeps it, so a move changes what happens next and nothing that
        /// already happened.
        /// </para>
        /// </summary>
        public Guid? GroupId { get; set; }
        public ActivityGroup? Group { get; set; }

        public GrantState State { get; set; } = GrantState.Active;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Who issued it. Nobody may grant a permission they do not hold.</summary>
        public string? GrantedByUserId { get; set; }
    }
}

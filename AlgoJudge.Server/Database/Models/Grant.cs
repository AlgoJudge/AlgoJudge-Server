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
        /// Deletion is anonymisation, so this stays resolvable after the account
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
        /// Null at activity scope, always — mapping into an activity belongs to
        /// the LTI work, whose purpose is to mirror a course binding, and
        /// building it before those requirements exist would build the wrong
        /// mechanism.
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
        /// This user's own permissions, as a <c>jsonb</c> array of strings. Filled
        /// in from a <see cref="PermissionTemplate"/> and then editable: "a
        /// manager with the right to update something taken away" is this set
        /// with that entry removed, not a second role layered over a first.
        /// </summary>
        public string Permissions { get; set; } = "[]";

        /// <summary>
        /// A membership that runs the activity rather than takes part in it.
        /// <para>
        /// <b>Computed by the Server on every write</b>, never accepted from the
        /// caller: a grant carrying any permission an ordinary participant does
        /// not hold is systemic, always. A jury member counted among the
        /// competitors is a bug, not a preference — so this is what excludes them
        /// from the participant count and from the results feed.
        /// </para>
        /// </summary>
        public bool IsSystem { get; set; }

        /// <summary>
        /// Which template it was created from. Informational, for the interface —
        /// <b>not</b> a reference: once the set has been edited, the name
        /// describes where it started, not what it is.
        /// </summary>
        public string? CreatedFromTemplate { get; set; }

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

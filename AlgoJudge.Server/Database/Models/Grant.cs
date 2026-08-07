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

        public GrantState State { get; set; } = GrantState.Active;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Who issued it. Nobody may grant a permission they do not hold.</summary>
        public string? GrantedByUserId { get; set; }
    }
}

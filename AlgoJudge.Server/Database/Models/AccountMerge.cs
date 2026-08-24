namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// One account's work carried onto another, and enough to put it back.
    /// <para>
    /// <b>Row ids, not counts.</b> A count cannot tell a submission that moved
    /// from one the target already had, and an undo has to. The lists are small:
    /// what a contest account produces is tens of rows, not thousands.
    /// </para>
    /// <para>
    /// <b>Both accounts keep their rows.</b> Deletion in this product has always
    /// meant anonymising in place — `docs/specs/AUTHENTICATION.md`, "deletion is
    /// always anonymization" — because `Submission` and `Result` name a user id
    /// and it has to stay resolvable. A merge is no exception: the emptied
    /// account is anonymised when the undo window closes, and the rows that
    /// record what it once <i>did</i> still resolve to something.
    /// </para>
    /// </summary>
    public class AccountMerge
    {
        public Guid Id { get; set; } = Utils.Uuid.New();

        /// <summary>The account emptied, blocked from the moment of the merge.</summary>
        public required string SourceUserId { get; set; }

        public required string TargetUserId { get; set; }

        public DateTime MergedAt { get; set; } = DateTime.UtcNow;

        public required string MergedByUserId { get; set; }

        /// <summary>
        /// When the emptied account is anonymised, and with that the last moment
        /// an undo is offered. Until then the account is untouched under its
        /// block, which is what lets an undo give it back whole.
        /// </summary>
        public DateTime AnonymiseAfter { get; set; }

        public DateTime? SourceAnonymisedAt { get; set; }

        public DateTime? UndoneAt { get; set; }

        public string? UndoneByUserId { get; set; }

        /// <summary>
        /// What moved, as `{"submissions":["…"],"grants":[…],…}`.
        /// <para>
        /// Includes the grants that were <b>dropped</b> rather than moved,
        /// because a collision resolved in the target's favour is still
        /// something the source had and an undo owes back.
        /// </para>
        /// </summary>
        public required string Moved { get; set; }

        public User? Source { get; set; }
        public User? Target { get; set; }
    }
}

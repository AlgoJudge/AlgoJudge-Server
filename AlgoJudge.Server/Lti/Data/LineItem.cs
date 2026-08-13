using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// One gradebook column, for one assignment.
    /// <para>
    /// A line item is a <c>SeriesProblem</c> — the assignment, which is where
    /// <c>MaxPoints</c> lives — and not a problem and not an activity. That
    /// mapping needs no new number and no new interpretation from the Server:
    /// <c>MaxPoints</c> is already a point value and the Server already rescales
    /// <c>round(score / scoreMax × maxPoints)</c> wherever it reports one (§6.1).
    /// </para>
    /// </summary>
    public class LineItem
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ResourceLinkId { get; set; }
        public ResourceLink? ResourceLink { get; set; }

        /// <summary>The assignment this column grades.</summary>
        public Guid SeriesProblemId { get; set; }

        /// <summary>
        /// The URL the platform gave this line item when it was created. Every
        /// score goes to <c>{this}/scores</c> and every read to
        /// <c>{this}/results</c>.
        /// </summary>
        public required string PlatformUrl { get; set; }

        /// <summary>
        /// The maximum this column was created with.
        /// <para>
        /// Kept because <b>changing <c>SeriesProblem.MaxPoints</c> puts every
        /// score already posted on the wrong scale</b> (§6.4, item 4). Comparing
        /// this with the assignment's current value is how that is noticed at
        /// all; without it the gradebook is quietly wrong and nothing says so.
        /// </para>
        /// </summary>
        public double ScoreMaximum { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

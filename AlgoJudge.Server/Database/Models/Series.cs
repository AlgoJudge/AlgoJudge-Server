using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A group of problems inside an activity. Contest vocabulary calls it a
    /// round, a course calls it a week or a class; the label comes from the
    /// activity type renderer, not from here.
    /// </summary>
    public class Series
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ActivityId { get; set; }
        public Activity? Activity { get; set; }

        /// <summary>Unique within its activity.</summary>
        public required string Slug { get; set; }

        public required string Name { get; set; }

        /// <summary>
        /// Optional, so an untimed practice activity needs neither. Both are
        /// required for a series that opens and closes.
        /// </summary>
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public int Order { get; set; }

        /// <summary>
        /// Whether a participant may see how many problems a series holds before
        /// it opens. The problems themselves are never sent while it is closed.
        /// </summary>
        public bool RevealProblemCount { get; set; } = true;

        /// <summary>
        /// Ranking freeze. Between these two instants the Server withholds
        /// entries rather than the Client hiding them — the ranking is assembled
        /// in the Client, so anything sent is disclosed.
        /// </summary>
        public DateTime? RankingFreezeAt { get; set; }
        public DateTime? RankingRevealAt { get; set; }

        public ICollection<SeriesProblem> SeriesProblems { get; set; } = new List<SeriesProblem>();
    }
}

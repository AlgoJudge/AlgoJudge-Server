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
        /// The schedule. Optional, so an untimed practice activity needs neither;
        /// both are required for a series that opens and closes on the clock.
        /// <para>
        /// These no longer decide whether the series is running — see
        /// <see cref="IsOpen"/>. They are what the scheduler acts on and what a
        /// participant's countdown is drawn against.
        /// </para>
        /// </summary>
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public int Order { get; set; }

        /// <summary>
        /// Whether the series is running now — <b>stored, not derived</b>
        /// (decided 2026-08-08).
        /// <para>
        /// The scheduler owns every transition: it sets this when
        /// <see cref="StartDate"/> passes and clears it when
        /// <see cref="EndDate"/> does, and a manager pausing or resuming writes
        /// it directly. Nothing else may compute openness from the dates, or
        /// there would be two answers to one question.
        /// </para>
        /// <para>
        /// The duty this creates is reconciliation: every write that moves a date
        /// — <c>shift</c>, <c>pause</c>, <c>resume</c> — must set this in the
        /// same transaction, and the scheduler corrects any disagreement it finds
        /// at startup. See <c>SeriesScheduleService</c>.
        /// </para>
        /// </summary>
        public bool IsOpen { get; set; }

        /// <summary>
        /// Since when a manager has it stopped. Absent means it is not paused.
        /// A pause takes no submission and stops the countdown.
        /// </summary>
        public DateTime? PausedAt { get; set; }

        /// <summary>
        /// Whether the pause also took the statements away, which is the
        /// manager's decision at the moment of pausing. Expressed to the
        /// participant by <see cref="IsOpen"/> going false; kept here so
        /// resuming knows what it is undoing.
        /// </summary>
        public bool HideProblemsWhilePaused { get; set; }

        /// <summary>
        /// Whether a participant may see how many problems a series holds before
        /// it opens. The problems themselves are never sent while it is closed.
        /// </summary>
        public bool RevealProblemCount { get; set; } = true;

        /// <summary>
        /// Ranking freeze. Between these two instants the Server withholds
        /// outcomes rather than the Client hiding them — the ranking is assembled
        /// in the Client, so anything sent is disclosed.
        /// </summary>
        public DateTime? RankingFreezeAt { get; set; }
        public DateTime? RankingRevealAt { get; set; }

        /// <summary>
        /// When this round's standings may be seen at all. Absent <c>From</c>
        /// means the round's own start; absent <c>To</c> means for ever.
        /// <para>
        /// Per round rather than per activity: an organiser publishes the first
        /// round's board while the second is still being fought. Different from
        /// the freeze above — that hides late results within a board, this
        /// decides whether there is a board.
        /// </para>
        /// </summary>
        public DateTime? RankingVisibleFrom { get; set; }
        public DateTime? RankingVisibleTo { get; set; }

        /// <summary>
        /// What the scheduler has already announced.
        /// <para>
        /// Separate from the state they accompany, and that separation is the
        /// point: a marker answers "has this been announced", never "is this
        /// open". Keeping them apart makes announcing exactly-once a single
        /// conditional <c>UPDATE … WHERE marker IS NULL RETURNING id</c>, and
        /// makes a missed announcement recoverable without touching the state.
        /// </para>
        /// </summary>
        public DateTime? StartAnnouncedAt { get; set; }
        public DateTime? EndAnnouncedAt { get; set; }
        public DateTime? WindowAnnouncedAt { get; set; }
        public DateTime? UnfrozenAnnouncedAt { get; set; }

        /// <summary>
        /// Optimistic concurrency, so two Server instances cannot both open the
        /// same series and two managers cannot lose one another's shift.
        /// </summary>
        public uint Version { get; set; }

        public ICollection<SeriesProblem> SeriesProblems { get; set; } = new List<SeriesProblem>();
    }
}

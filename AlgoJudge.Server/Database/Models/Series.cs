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
        /// How much this series outranks everything else while it runs.
        /// <para>
        /// <b>A number, because the number is the meaning.</b> While this series
        /// is running, anything of a <i>lower</i> rank is locked for whoever is
        /// taking part in it — see <see cref="Services.SeriesLockdown"/>.
        /// Equal ranks survive together, which is what lets two contests share
        /// one room.
        /// </para>
        /// <para>
        /// <b>Visibility, never permission.</b> The permission model has no
        /// subtraction in it; grants answer <i>who may</i>, this answers
        /// <i>what is reachable while that runs</i>.
        /// <c>docs/specs/SERIES_LOCKDOWN.md</c>.
        /// </para>
        /// </summary>
        public int Importance { get; set; }

        /// <summary>
        /// How far <see cref="Importance"/> reaches: this activity, or every
        /// activity the reader takes part in.
        /// <para>
        /// A course marking one round an examination should not lock its
        /// students out of every other course on the installation, and a
        /// laboratory contest should. Both are wanted, so the manager chooses.
        /// </para>
        /// </summary>
        public SeriesImportanceScope ImportanceScope { get; set; } = SeriesImportanceScope.Activity;

        /// <summary>
        /// The address ranges this series may be reached from. Empty means
        /// anywhere.
        /// <para>
        /// <b>It grants and it takes away in one act</b>: a series with any rule
        /// is served to an address that matches and is <b>absent</b> for every
        /// other, whatever grant the reader holds.
        /// </para>
        /// </summary>
        public ICollection<SeriesAddressRule> AddressRules { get; set; } = new List<SeriesAddressRule>();

        /// <summary>
        /// The switch for this series alone. Off, it neither hides nor locks —
        /// and it <b>keeps its rules</b>, so turning it back on restores them.
        /// <para>
        /// A wrong list on the day of a contest locks out a whole cohort at
        /// once, so there has to be something to clear that is not "delete the
        /// configuration and rebuild it afterwards".
        /// <see cref="Instance.SeriesRestrictionsEnabled"/> is the same switch
        /// for the installation, for when nobody yet knows which series is at
        /// fault.
        /// </para>
        /// </summary>
        public bool RestrictionsEnabled { get; set; } = true;

        /// <summary>
        /// Which Runners judge this round's submissions, overriding
        /// <see cref="Activity.RunnerTags"/>.
        /// <para>
        /// <b>Null inherits</b>, and is what every round holds until somebody
        /// decides otherwise. A round pinned to somewhere else names it; a round
        /// that wants the general Runners while its course is pinned to a
        /// laboratory writes <c>default</c> out, which is why that tag is
        /// ordinary text anybody may type.
        /// </para>
        /// <para>
        /// So there are two states here and not three. An empty list would be a
        /// third way of writing one of them, and two spellings of one meaning is
        /// how a manager ends up unable to tell which they chose.
        /// </para>
        /// </summary>
        public List<string>? RunnerTags { get; set; }

        /// <summary>
        /// Optimistic concurrency, so two Server instances cannot both open the
        /// same series and two managers cannot lose one another's shift.
        /// </summary>
        public uint Version { get; set; }

        public ICollection<SeriesProblem> SeriesProblems { get; set; } = new List<SeriesProblem>();
    }
}

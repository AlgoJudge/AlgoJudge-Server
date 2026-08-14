using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Lti.Data
{
    /// <summary>
    /// One placement of an AlgoJudge activity inside a course.
    /// <para>
    /// This is what a teacher added to a Moodle course, and it is the object
    /// everything else in this module hangs off: line items belong to it, grades
    /// are reconciled per it, and its aggregation rule decides which of a
    /// person's attempts becomes the number in the gradebook.
    /// </para>
    /// </summary>
    public class ResourceLink
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid PlatformId { get; set; }
        public Platform? Platform { get; set; }

        /// <summary>
        /// The platform's own id for this placement, from the launch. Unique
        /// within a platform and nowhere else.
        /// </summary>
        public required string PlatformResourceLinkId { get; set; }

        /// <summary>The course. Maps to nothing that has to exist in AlgoJudge.</summary>
        public required string ContextId { get; set; }

        /// <summary>What the course is called, for a screen that has to say which one.</summary>
        public string? ContextTitle { get; set; }

        /// <summary>
        /// The courses this one was copied from, comma-separated as
        /// <c>$Context.id.history</c> delivers them.
        /// <para>
        /// Recorded from milestone 1 even though nothing reads it until milestone
        /// 4, because it arrives at launch and cannot be recovered afterwards. A
        /// course copied this September is a course whose history is gone by the
        /// time the rebinding feature exists.
        /// </para>
        /// <para>
        /// Measured 2026-08-13 — Moodle substitutes this in 4.5.13, 5.2.2 and
        /// 5.3dev. It does <b>not</b> substitute <c>$ResourceLink.id.history</c>
        /// in any of them, which is why there is no column for it: milestone 4
        /// rebinds at course granularity or asks.
        /// </para>
        /// </summary>
        public string? ContextHistory { get; set; }

        /// <summary>
        /// The activity this placement runs. Resolved <b>once</b>, from the slug
        /// in the launch's custom parameters — the slug is the stable,
        /// human-readable alias already used in URLs, which is what makes a
        /// course copy carry a working parameter rather than a dead id.
        /// </summary>
        public Guid ActivityId { get; set; }

        /// <summary>
        /// Optionally narrowed to one series within the activity. Null means the
        /// whole activity.
        /// </summary>
        public Guid? SeriesId { get; set; }

        /// <summary>
        /// Where this course's gradebook accepts new columns, from the launch's
        /// AGS <c>endpoint</c> claim.
        /// <para>
        /// Recorded on the link because that is the only place it arrives: it is
        /// per placement, and the reconciling worker runs long after the launch
        /// that carried it. Null means the platform offered no line-item
        /// container — a course with grading switched off, or a tool registered
        /// without the AGS scopes — and grade synchronisation then has nowhere to
        /// go, which is a state to report rather than an error to retry.
        /// </para>
        /// </summary>
        public string? AgsLineItemsUrl { get; set; }

        /// <summary>
        /// Which attempt becomes the grade.
        /// <para>
        /// <b>It lives here rather than on <c>Activity</c>, and that is a
        /// decision rather than a convenience</b> (§6.2). The Server deliberately
        /// does not know which attempt is authoritative — <c>rankingPolicy</c>
        /// was withdrawn and "which attempt counts" moved into the ranking
        /// renderer, per activity type. A gradebook column cannot ask a renderer,
        /// so the opinion has to exist somewhere; putting it in an optional
        /// module a deployment may not have keeps the core's silence intact.
        /// </para>
        /// <para>
        /// One value ships. A course needs the best attempt and nothing else has
        /// been asked for; adding a second is a migration and a screen, not a
        /// redesign.
        /// </para>
        /// </summary>
        public GradeAggregation Aggregation { get; set; } = GradeAggregation.BestScore;

        /// <summary>
        /// Whether a manager accepted that this activity is now reachable from
        /// more than one course.
        /// <para>
        /// Decided 2026-08-13: a second placement of the same activity is allowed
        /// — a shared problem set across two groups is real — but it is not
        /// allowed to happen quietly. One activity's grades then reach two
        /// gradebooks and §7's enrolment has two rosters, which is a thing to
        /// find out from a screen rather than from a gradebook that disagrees
        /// with itself.
        /// </para>
        /// <para>
        /// True on the first placement, because there is nothing to accept. False
        /// on a later one until somebody says so, and a launch into an
        /// unacknowledged second placement lands on a page explaining it.
        /// </para>
        /// </summary>
        public bool SharingAcknowledged { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<LineItem> LineItems { get; set; } = new List<LineItem>();
    }

    /// <summary>Which of a person's attempts becomes the gradebook number.</summary>
    public enum GradeAggregation
    {
        /// <summary>The highest score they reached. The only shipped value (§6.2).</summary>
        BestScore = 0,
    }
}

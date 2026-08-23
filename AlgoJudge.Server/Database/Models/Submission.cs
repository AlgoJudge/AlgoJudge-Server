using System.Net;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// What a participant sent. It belongs to a <see cref="SeriesProblem"/>
    /// rather than to a bare problem, because the languages and limits it was
    /// judged under are properties of the assignment.
    /// <para>
    /// A submission does not carry a verdict. Each attempt at evaluating it is an
    /// <see cref="EvaluationJob"/>, the full attempt history is retained, and a
    /// rejudge adds one rather than overwriting anything.
    /// </para>
    /// </summary>
    public class Submission
    {
        public Guid Id { get; set; } = Uuid.New();

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The submitting user. Deletion is anonymisation, so this identifier
        /// stays resolvable even after the account it names has been emptied.
        /// </summary>
        public required string UserId { get; set; }
        public User? User { get; set; }

        /// <summary>
        /// The group this was sent as, or null when it was sent by a person
        /// competing as themselves.
        /// <para>
        /// <b>Stamped by the Server from the submitter's grant, never taken from
        /// the request.</b> "If the user is in a group, sending as the group is
        /// compulsory" is a rule about what happens, not a default the form is
        /// asked to keep — deriving it here is what makes it unforgeable.
        /// </para>
        /// <para>
        /// <b>And never rewritten.</b> A manager may move somebody to another
        /// group mid-contest; this row keeps the group it was sent under, so a
        /// ranking from an hour ago still reconciles with the ranking now. The
        /// alternative — deriving the group through the grant at read time —
        /// would move points that were already scored.
        /// </para>
        /// </summary>
        public Guid? GroupId { get; set; }
        public ActivityGroup? Group { get; set; }

        public Guid SeriesProblemId { get; set; }
        public SeriesProblem? SeriesProblem { get; set; }

        /// <summary>
        /// What the participant declared beside the bytes — the language, and
        /// whatever else the problem type asks of them. <c>jsonb</c> and
        /// <b>opaque to the Server</b>, handed to the Runner unread.
        /// <para>
        /// <b>This was a <c>Language</c> column until 2026-08-22</b>, and the
        /// Server read it: it refused a language the activity did not list, and
        /// it chose a file extension for pasted source from a table of seven
        /// languages compiled into <c>ActivitiesController</c>. That table meant
        /// a <b>Server release for every new language</b>, against a guardrail
        /// saying a problem type must not need one. Both are gone; the allowed
        /// set lives in <see cref="SeriesProblem.Config"/> and the Runner refuses
        /// what the assignment excluded.
        /// </para>
        /// <para>Null means none; never <c>{}</c>.</para>
        /// </summary>
        public string? Props { get; set; }

        /// <summary>
        /// What was sent — the source, or the archive — under the name
        /// <c>source</c>. On the submission rather than on an attempt, because it
        /// is what somebody did once and every rejudge reads the same bytes.
        /// </summary>
        public ICollection<FileReference> Files { get; set; } = new List<FileReference>();

        public ICollection<EvaluationJob> Jobs { get; set; } = new List<EvaluationJob>();

        /// <summary>
        /// Where this arrived from, for a judge auditing a contest.
        /// <para>
        /// The question it exists to answer is <b>was this sent from outside the
        /// examination room</b>, which is containment in a network rather than
        /// equality with an address — so <c>inet</c>, and normalised before it
        /// gets here. See <see cref="Services.RequestOrigin"/> and
        /// <c>docs/specs/ORIGIN_METADATA.md</c>.
        /// </para>
        /// <para>
        /// <b>Here rather than reached through the session</b>, for two reasons
        /// that both matter. It rides <c>submission:read:all</c>, which is
        /// already scoped per activity, so a judge sees their own contest and no
        /// other — the session list answers to a system-scope permission. And it
        /// is evidence in a contest, so it outlives the session's own thirty-day
        /// window by design.
        /// </para>
        /// </summary>
        public IPAddress? IpAddress { get; set; }

        /// <summary>
        /// The browser session this came from, or null.
        /// <para>
        /// Null on the first authenticated request a brand-new browser makes,
        /// because the session cookie is minted <i>after</i> the response —
        /// <see cref="Realtime.SessionTrackingMiddleware"/>. In practice a dozen
        /// requests precede a submission, so this is documented rather than
        /// worked around.
        /// </para>
        /// </summary>
        public Guid? SessionId { get; set; }

        /// <summary>
        /// The name the browser gave itself. <b>Not evidence</b>: a page writes
        /// it, and a room of machines imaged from one disk reports one for all
        /// of them. It answers <i>the same browser, two accounts</i>.
        /// </summary>
        public Guid? DeviceId { get; set; }

        /// <summary>
        /// When a manager ruled that this does not count, or null. One column,
        /// so there is nothing to disagree with.
        /// <para>
        /// <b>It rules on a result and retracts nothing.</b> The verdict, the
        /// attempts, the files and the place in every list all stay — and so
        /// does the ceiling it spent, as <see cref="ActivityGroup.IsSystem"/>
        /// does one level up. Giving an attempt back means raising the limit.
        /// </para>
        /// <para>
        /// What it leaves: <see cref="Services.Scoring.BestOf"/>, the board and
        /// the socket in <see cref="Services.ResultsService"/>, and the gradebook
        /// in <c>Lti/Services/GradeSyncService</c>.
        /// <c>docs/specs/EXCLUDED_SUBMISSIONS.md</c>.
        /// </para>
        /// </summary>
        public DateTime? ExcludedAt { get; set; }

        /// <summary>
        /// Who ruled. No navigation, as <see cref="Grant.GrantedByUserId"/>:
        /// deletion here is anonymisation, and a foreign key would force a
        /// cascade decision this does not need.
        /// </summary>
        public string? ExcludedByUserId { get; set; }

        /// <summary>
        /// Why, in a manager's own words.
        /// <para>
        /// Not on the participant's screen, and not withheld from them either —
        /// it is writing about them, so their data export carries it. Cleared on
        /// erasure while <see cref="ExcludedAt"/> stays: the ruling is contest
        /// history, free text naming somebody is not.
        /// </para>
        /// </summary>
        public string? ExclusionReason { get; set; }
    }
}

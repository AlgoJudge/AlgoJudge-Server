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
    }
}

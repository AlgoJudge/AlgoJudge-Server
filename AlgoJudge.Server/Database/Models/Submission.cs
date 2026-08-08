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

        /// <summary>Declared by the participant; meaningful only to the problem type.</summary>
        public string? Language { get; set; }

        /// <summary>
        /// What was sent — the source, or the archive — under the name
        /// <c>source</c>. On the submission rather than on an attempt, because it
        /// is what somebody did once and every rejudge reads the same bytes.
        /// </summary>
        public ICollection<FileReference> Files { get; set; } = new List<FileReference>();

        public ICollection<EvaluationJob> Jobs { get; set; } = new List<EvaluationJob>();
    }
}

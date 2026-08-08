using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    /// <summary>
    /// A participant's question or a staff announcement — one entity told apart
    /// by <see cref="Kind"/>, because the two share a list, a scope and a read
    /// state and differ only in who may create one.
    /// <para>
    /// Scope narrows from the activity down: an activity-wide notice leaves both
    /// optional references null, a series notice sets <see cref="SeriesId"/>, and
    /// a question about one problem sets <see cref="SeriesProblemId"/>.
    /// <see cref="ActivityId"/> is always present, so authorisation and listing
    /// never have to walk the chain upwards to find out where a row belongs.
    /// </para>
    /// </summary>
    public class Question
    {
        public Guid Id { get; set; } = Uuid.New();

        public Guid ActivityId { get; set; }
        public Activity? Activity { get; set; }

        /// <summary>Set when the question concerns one series rather than the activity.</summary>
        public Guid? SeriesId { get; set; }
        public Series? Series { get; set; }

        /// <summary>Set when the question concerns one problem within a series.</summary>
        public Guid? SeriesProblemId { get; set; }
        public SeriesProblem? SeriesProblem { get; set; }

        public QuestionKind Kind { get; set; } = QuestionKind.Question;

        public required string Topic { get; set; }
        public required string Body { get; set; }

        public required string AuthorUserId { get; set; }
        public User? Author { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// A question is visible to its author and to staff until a manager
        /// publishes it, after which every participant sees it. An announcement is
        /// published by definition.
        /// </summary>
        public bool IsPublished { get; set; }

        public string? AnswerBody { get; set; }
        public string? AnswerAuthorUserId { get; set; }
        public DateTime? AnsweredAt { get; set; }

        public ICollection<QuestionRead> Reads { get; set; } = new List<QuestionRead>();
    }

    /// <summary>
    /// Read state, per user and per row. It is a separate table rather than a
    /// flag because "read" is a property of the pair, not of the question.
    /// </summary>
    public class QuestionRead
    {
        public Guid QuestionId { get; set; }
        public Question? Question { get; set; }

        public required string UserId { get; set; }

        public DateTime ReadAt { get; set; } = DateTime.UtcNow;
    }
}

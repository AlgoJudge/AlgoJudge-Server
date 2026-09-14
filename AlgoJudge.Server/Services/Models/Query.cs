namespace AlgoJudge.Server.Services.Models
{
    /// <summary>
    /// One page of a collection, in the shape the Client's <c>Page&lt;T&gt;</c>
    /// expects.
    /// </summary>
    public class Page<T>
    {
        public required IReadOnlyList<T> Items { get; init; }
        public required int Total { get; init; }
        public required int PageNumber { get; init; }
        public required int PageSize { get; init; }
    }

    /// <summary>
    /// Paging, as every collection endpoint takes it.
    /// <para>
    /// A ceiling on <see cref="PageSize"/> rather than trust: an unbounded page
    /// size is a denial-of-service parameter, and one caller asking for a hundred
    /// thousand rows is indistinguishable from an attack.
    /// </para>
    /// </summary>
    public class PageQuery
    {
        public const int DefaultSize = 20;
        public const int MaxSize = 200;

        private int page = 1;
        private int pageSize = DefaultSize;

        /// <summary>One-based, as the Client sends it.</summary>
        public int Page
        {
            get => page;
            set => page = value < 1 ? 1 : value;
        }

        public int PageSize
        {
            get => pageSize;
            set => pageSize = value switch
            {
                < 1 => DefaultSize,
                > MaxSize => MaxSize,
                _ => value,
            };
        }

        /// <summary>
        /// How many rows to pass over, and it cannot be negative.
        /// <para>
        /// <b>Computed in <c>long</c> and clamped, because the multiplication
        /// overflows.</b> <see cref="PageSize"/> is bounded but
        /// <see cref="Page"/> has only a floor, so <c>?page=2147483647</c> wrapped
        /// <c>int</c> and produced a <b>negative</b> offset — which PostgreSQL
        /// refuses, so an absurd page number answered 500 rather than an empty
        /// page. It stays an <c>int</c> because <c>Queryable.Skip</c> takes one.
        /// </para>
        /// <para>
        /// Clamped rather than refused: a page past the end is not an error, it
        /// is a page with nothing on it, and that is what every page past the end
        /// already answered.
        /// </para>
        /// </summary>
        public int Skip => (int)Math.Min((long)(Page - 1) * PageSize, int.MaxValue);
    }

    /// <summary>
    /// What narrows the manager's submissions list.
    /// <para>
    /// A record rather than eight more positional parameters. Two of the pairs
    /// share a type and sit next to each other — rounds and assignments are both
    /// <c>IReadOnlyList&lt;Guid&gt;?</c>, people and verdicts both
    /// <c>IReadOnlyList&lt;string&gt;?</c> — and a transposed pair compiles
    /// silently and filters the wrong column.
    /// </para>
    /// <para>
    /// <b><see cref="ActivityId"/> is singular and stays singular.</b> It is the
    /// scope the permission is asked at, not a filter: <c>ListScopeAsync</c>
    /// takes it, and answering for several activities at once would be a new
    /// authorization shape rather than a wider question.
    /// </para>
    /// <para>
    /// Everywhere else <b>null is every and empty is nothing</b>, which is the
    /// rule <c>Filter</c> establishes on the way in.
    /// </para>
    /// </summary>
    public record SubmissionQuery
    {
        public Guid? ActivityId { get; init; }
        public IReadOnlyList<Guid>? SeriesIds { get; init; }
        public IReadOnlyList<Guid>? AssignmentIds { get; init; }
        public IReadOnlyList<string>? UserIds { get; init; }
        public IReadOnlyList<Database.Models.EvaluationJobState>? States { get; init; }
        public IReadOnlyList<string>? Verdicts { get; init; }
        public string? Search { get; init; }
    }
}

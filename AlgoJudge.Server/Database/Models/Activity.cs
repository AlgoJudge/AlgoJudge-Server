using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Database.Models
{
    public class Activity
    {
        public Guid Id { get; set; } = Uuid.New();

        /// <summary>
        /// Human-readable alias used in URLs, for example <c>AMMPZ-2019</c>.
        /// Unique per installation, case-insensitively, and immutable once set.
        /// It is not an identifier: nothing references an activity by slug.
        /// </summary>
        public required string Slug { get; set; }

        public required string Name { get; set; }

        /// <summary>Type discriminator, <c>name@version</c>. Never interpreted here.</summary>
        public required string Type { get; set; }

        /// <summary>
        /// Which ranking the Client renders, for example <c>icpc</c> or
        /// <c>points</c>. Deliberately separate from <see cref="Type"/>, and
        /// deliberately never branched on in this project — the moment the
        /// Server reads it, adding a ranking format becomes a Server change.
        /// </summary>
        public required string RankingType { get; set; }

        /// <summary>IANA zone the activity's clock is displayed in, e.g. <c>Europe/Warsaw</c>.</summary>
        public required string TimeZone { get; set; }

        /// <summary>
        /// Optional explicit bounds. When absent the activity spans its series:
        /// the earliest start and the latest end.
        /// </summary>
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        public bool HasRanking { get; set; } = true;
        public bool HasQuestions { get; set; } = true;
        public bool HasRules { get; set; } = true;

        public ScoreVisibility ScoreVisibility { get; set; } = ScoreVisibility.Everyone;
        public LogVisibility LogVisibility { get; set; } = LogVisibility.ManagersOnly;

        public JoinPolicy JoinPolicy { get; set; } = JoinPolicy.Closed;

        /// <summary>
        /// Ceiling the Server itself enforces on a participant upload. Per-problem
        /// limits live in <see cref="SeriesProblem.Config"/> and are opaque here;
        /// this one cannot be, because the Server is what rejects the request.
        /// </summary>
        public long MaxUploadBytes { get; set; } = 8 * 1024 * 1024;

        /// <summary>Submissions one participant may make per problem. Null means unlimited.</summary>
        public int? MaxSubmissionsPerProblem { get; set; }

        /// <summary>Rules text, in the same content format as a problem statement.</summary>
        public Guid? RulesFileId { get; set; }
        public File? RulesFile { get; set; }

        public ICollection<Series> Series { get; set; } = new List<Series>();
        public ICollection<Question> Questions { get; set; } = new List<Question>();

        /// <summary>
        /// Who is in this activity and what they may do. A grant is the
        /// membership, so this is the participant list as well as the permission
        /// list — there is no second table that could disagree with it.
        /// </summary>
        public ICollection<Grant> Grants { get; set; } = new List<Grant>();
    }
}

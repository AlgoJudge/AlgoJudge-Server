using AlgoJudge.Server.Database.Models;

namespace AlgoJudge.Server.Services.Models
{
    public class ActivityInfo
    {
        /// <summary>Serialised as a string; every identifier the API exposes is a UUID.</summary>
        public Guid Id { get; set; }
        public required string Slug { get; set; }
        public required string Name { get; set; }
        public required string Type { get; set; }
        public required string RankingType { get; set; }
        public required string TimeZone { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool HasRanking { get; set; }
        public bool HasQuestions { get; set; }
        public bool HasRules { get; set; }
        public JoinPolicy JoinPolicy { get; set; }
    }
}

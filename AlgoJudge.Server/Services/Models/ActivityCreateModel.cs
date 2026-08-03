namespace AlgoJudge.Server.Services.Models
{
    public class ActivityCreateModel
    {
        public required string Slug { get; set; }
        public required string Name { get; set; }
        public required string Type { get; set; }
        public required string RankingType { get; set; }
        public required string TimeZone { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }
}

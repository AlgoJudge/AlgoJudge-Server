using AlgoJudge.Server.Database.Models;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// What a number means on its way out.
    /// <para>
    /// The Runner marks against the package's own groups — its scale. What the
    /// problem is worth <b>in one round</b> is <see cref="SeriesProblem.MaxPoints"/>,
    /// and the Server rescales wherever it reports a number. The Runner's own
    /// document is never rescaled: it is the judge's arithmetic over its own
    /// tests, and rewriting somebody else's document is editing it.
    /// </para>
    /// </summary>
    public static class Scoring
    {
        /// <summary>
        /// The scale a Runner reports against when the package does not say
        /// otherwise. Matches `RUNNER_SCALE` in the Client's fixtures.
        /// </summary>
        public const double RunnerScale = 100d;

        public static double MaxPoints(SeriesProblem assignment) => assignment.MaxPoints ?? RunnerScale;

        /// <summary>
        /// `round(score / scoreMax × maxPoints)`.
        /// <para>
        /// Absent stays absent. A submission nobody has judged has no score, and
        /// zero is a different claim — a board must not score what it has not
        /// been told.
        /// </para>
        /// </summary>
        public static double? Rescale(double? score, double maxPoints, double scoreMax = RunnerScale)
        {
            if (score is null) return null;
            if (scoreMax <= 0) return null;
            return Math.Round(score.Value / scoreMax * maxPoints);
        }

        /// <summary>The newest job decides what a submission currently says.</summary>
        public static EvaluationJob? Current(Submission submission) =>
            submission.Jobs.OrderByDescending(j => j.Attempt).FirstOrDefault();

        /// <summary>
        /// The best score ever awarded across a person's submissions, on the
        /// Runner's scale.
        /// <para>
        /// The best, not the last: somebody who scores eighty and then breaks it
        /// keeps the eighty.
        /// </para>
        /// </summary>
        public static double? Best(IEnumerable<Submission> submissions)
        {
            double? best = null;
            foreach (var submission in submissions)
            {
                var score = Current(submission)?.Result?.Score;
                if (score is null) continue;
                best = best is null ? score : Math.Max(best.Value, score.Value);
            }
            return best;
        }

        /// <summary>
        /// The reader's own standing on one problem, computed on the Runner's
        /// scale so that an assignment's point value cannot turn a solve into a
        /// partial by rounding.
        /// </summary>
        public static string Status(IReadOnlyCollection<Submission> submissions, double? best)
        {
            if (submissions.Count == 0) return "untouched";
            if (best is null) return "attempted";
            if (best >= RunnerScale) return "solved";
            return best > 0 ? "partial" : "attempted";
        }
    }
}

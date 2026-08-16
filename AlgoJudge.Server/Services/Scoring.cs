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
    /// <para>
    /// <b>The unit that crosses this boundary is a fraction</b>, never a raw
    /// score. A Runner says what it awarded <i>and</i> what it awarded it out of;
    /// reading the first without the second means assuming a scale, and an
    /// assumption is exactly what went wrong here — every reader but the grade
    /// export took <see cref="RunnerScale"/> for granted, so a Runner reporting
    /// 1 out of 1 on a five-point assignment scored `round(1 / 100 × 5)` = 0,
    /// while the export sent 5. Two answers to one question, and both were shown
    /// to the same people.
    /// </para>
    /// </summary>
    public static class Scoring
    {
        /// <summary>
        /// The scale assumed of a result that does not say what it was marked out
        /// of. Matches `RUNNER_SCALE` in the Client's fixtures.
        /// <para>
        /// A fallback for old rows, not a convention to rely on: anything
        /// reporting today sends its own maximum.
        /// </para>
        /// </summary>
        public const double RunnerScale = 100d;

        public static double MaxPoints(SeriesProblem assignment) => assignment.MaxPoints ?? RunnerScale;

        /// <summary>
        /// What a result is worth, as a fraction of what it was marked out of.
        /// <para>
        /// Absent stays absent: a submission nobody has judged has no standing,
        /// and zero is a different claim.
        /// </para>
        /// </summary>
        public static double? Fraction(Result? result) => Fraction(result?.Score, result?.MaxScore);

        /// <summary>The same, for a score and its maximum held apart.</summary>
        public static double? Fraction(double? score, double? outOf)
        {
            if (score is null) return null;
            var scale = outOf ?? RunnerScale;
            if (scale <= 0) return null;
            return score.Value / scale;
        }

        /// <summary>
        /// `round(fraction × maxPoints)`.
        /// <para>
        /// Takes a fraction rather than a score so that the scale cannot be
        /// forgotten at the call site — which is the whole of the defect this
        /// signature replaces.
        /// </para>
        /// </summary>
        public static double? Rescale(double? fraction, double maxPoints)
        {
            if (fraction is null) return null;
            return Math.Round(fraction.Value * maxPoints);
        }

        /// <summary>The newest job decides what a submission currently says.</summary>
        public static EvaluationJob? Current(Submission submission) =>
            submission.Jobs.OrderByDescending(j => j.Attempt).FirstOrDefault();

        /// <summary>
        /// The best standing across a person's submissions, as a fraction.
        /// <para>
        /// The best, not the last: somebody who scores eighty per cent and then
        /// breaks it keeps the eighty. Compared as fractions, because two
        /// submissions to one problem may have been marked out of different
        /// maxima — a package republished with more tests, or a type that marks
        /// out of one.
        /// </para>
        /// </summary>
        public static double? Best(IEnumerable<Submission> submissions)
        {
            double? best = null;
            foreach (var submission in submissions)
            {
                var fraction = Fraction(Current(submission)?.Result);
                if (fraction is null) continue;
                best = best is null ? fraction : Math.Max(best.Value, fraction.Value);
            }
            return best;
        }

        /// <summary>
        /// The reader's own standing on one problem, decided on the fraction so
        /// that an assignment's point value cannot turn a solve into a partial by
        /// rounding — and so that a type marking out of one is not permanently
        /// "partial" for having awarded its whole scale.
        /// </summary>
        public static string Status(IReadOnlyCollection<Submission> submissions, double? best)
        {
            if (submissions.Count == 0) return "untouched";
            if (best is null) return "attempted";
            if (best >= 1d) return "solved";
            return best > 0 ? "partial" : "attempted";
        }
    }
}

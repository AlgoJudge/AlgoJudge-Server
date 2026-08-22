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

        /// <summary>
        /// The scale a number is reported on: the assignment's point value, or —
        /// where it states none — <b>what the result itself was marked out of</b>.
        /// <para>
        /// This was <c>assignment.MaxPoints ?? 100</c> until 2026-08-22, and that
        /// contradicted the column beside it in the same file:
        /// <see cref="SeriesProblem.MaxPoints"/> says "null keeps the Runner's
        /// own scale", and 100 is not the Runner's own scale, it is a percentage.
        /// A package marking out of 70 reported a full solve as <b>100 / 100</b>
        /// on every screen — the problem's own scoring, which the owner's rule
        /// says is fixed to the problem, silently replaced by a convention.
        /// </para>
        /// <para>
        /// Null where the assignment states no value <i>and</i> nothing has been
        /// judged: there is no scale yet, and inventing one would put a maximum
        /// beside a score that does not exist.
        /// </para>
        /// </summary>
        public static double? Scale(SeriesProblem assignment, double? outOf) =>
            assignment.MaxPoints ?? outOf;

        /// <summary>
        /// What one result is worth and what it is worth out of, together.
        /// <para>
        /// <b>Together, because apart is how they disagree.</b> A screen reading
        /// the score from one place and the maximum from another showed a raw
        /// attempt score beside a rescaled maximum — 70 out of 50 — and nothing
        /// in either expression looked wrong on its own.
        /// </para>
        /// </summary>
        public static (double? Score, double? MaxScore) Reported(SeriesProblem assignment, Result? result)
        {
            var scale = Scale(assignment, result?.MaxScore);
            return (Rescale(Fraction(result), scale ?? 0), result?.Score is null ? null : scale);
        }

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

        /// <summary>The same, where the scale may not be known yet.</summary>
        public static double? Rescale(double? fraction, double? maxPoints) =>
            maxPoints is null ? null : Rescale(fraction, maxPoints.Value);

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
        public static double? Best(IEnumerable<Submission> submissions) => BestOf(submissions).Fraction;

        /// <summary>
        /// The best standing <b>and the scale the result that produced it was
        /// marked on</b>.
        /// <para>
        /// The second half matters where the assignment states no point value:
        /// the number reported is then the package's own, and the package's own
        /// maximum is only knowable from a result. Two submissions to one problem
        /// may carry different maxima — a package republished with more tests —
        /// so it is the best one's maximum, not any of them.
        /// </para>
        /// </summary>
        public static (double? Fraction, double? OutOf) BestOf(IEnumerable<Submission> submissions)
        {
            double? best = null;
            double? outOf = null;

            foreach (var submission in submissions)
            {
                var result = Current(submission)?.Result;
                var fraction = Fraction(result);
                if (fraction is null) continue;
                if (best is null || fraction.Value > best.Value)
                {
                    best = fraction;
                    outOf = result?.MaxScore;
                }
            }
            return (best, outOf);
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

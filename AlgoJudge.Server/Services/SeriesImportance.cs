namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// How much a series outranks the rest while it runs.
    /// <para>
    /// <b>The wire value is the number, and that is deliberate.</b> The Client
    /// names each rank and prints the number beside the name, because the
    /// <i>ordering</i> is what this list means: a manager choosing "exam" has to
    /// see that it loses to the trial round of a real competition. A rank the
    /// Client has no word for shows as its number rather than breaking, so the
    /// two sides can only ever disagree about a label — never about an order.
    /// </para>
    /// <para>
    /// <b>Gaps of ten, on purpose.</b> A level inserted between two existing
    /// ones is a new constant, never a migration renumbering rows that are
    /// already stored.
    /// </para>
    /// </summary>
    public static class SeriesImportance
    {
        /// <summary>Hides nothing and is hidden by anything above it.</summary>
        public const int Normal = 0;

        public const int CoursePractice = 10;
        public const int CourseContest = 20;
        public const int Exam = 30;

        /// <summary>
        /// A trial round of a real competition. <b>Above <see cref="Exam"/></b>,
        /// by the owner's decision — a national final's warm-up displaces a
        /// midterm rather than the other way round.
        /// </summary>
        public const int OfficialPractice = 40;

        public const int OfficialContest = 50;

        /// <summary>
        /// What a manager may store. The Client offers these; this is what
        /// refuses anything else, so no row ever holds a rank nothing can name.
        /// </summary>
        public static readonly IReadOnlyList<int> Ranks =
            [Normal, CoursePractice, CourseContest, Exam, OfficialPractice, OfficialContest];

        public static bool IsKnown(int rank) => Ranks.Contains(rank);
    }
}

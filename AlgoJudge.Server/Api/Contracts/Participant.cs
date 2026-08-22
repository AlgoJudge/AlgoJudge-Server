namespace AlgoJudge.Server.Api.Contracts
{
    /// <summary>
    /// The participant-facing wire contract, mirroring
    /// `AlgoJudge-Client/src/api/ParticipantApi.ts`.
    /// </summary>

    public record ActivityDocumentRefDto
    {
        /// <summary>`welcome`, `home`, `rules`.</summary>
        public required string Kind { get; init; }
        public string? Language { get; init; }
        public string? Title { get; init; }
        public string? ValidFrom { get; init; }
        public required string FileId { get; init; }
        public required string Sha256 { get; init; }
        public required long SizeBytes { get; init; }
    }

    public record ActivityModulesDto
    {
        public required bool Questions { get; init; }
    }


    public record ActivityDto
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
        /// <summary>Type discriminator, `name@version`. Selects the layout renderer.</summary>
        public required string Type { get; init; }
        /// <summary>Selects the ranking renderer. Independent of `type`.</summary>
        public required string RankingType { get; init; }
        public required string TimeZone { get; init; }
        /// <summary>`upcoming` | `ongoing` | `finished`.</summary>
        public required string State { get; init; }
        /// <summary>`enrolled` | `invited` | `open`.</summary>
        public required string Membership { get; init; }
        /// <summary>`closed` | `password` | `open`.</summary>
        public required string JoinPolicy { get; init; }
        /// <summary>
        /// `everyone` | `participantOnly` | `managersOnly`. Also decides whether
        /// the ranking is offered at all — there is no second switch beside it.
        /// </summary>
        public required string ScoreVisibility { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        /// <summary>
        /// Carried so the screen can say <b>why</b> a finished round shows no
        /// problems, not so it can decide: the Server withholds them.
        /// </summary>
        public required bool HideEndedSeriesProblems { get; init; }
        /// <summary>
        /// Every document this activity currently publishes. Which documents
        /// exist is read from here and nowhere else.
        /// </summary>
        public required IReadOnlyList<ActivityDocumentRefDto> Documents { get; init; }
        public required ActivityModulesDto Modules { get; init; }
        public double? FinalScore { get; init; }
        public double? MaxScore { get; init; }
        /// <summary>
        /// Display metadata, opaque. Typed `{ key, value }[]` until 2026-08-22 —
        /// a shape the Server invented for a value it does not read.
        /// </summary>
        public object? Props { get; init; }
    }

    /// <summary>
    /// What the enrolment form collected. Both fields are conditional on the
    /// activity, and the Server decides — the Client sends what it collected.
    /// </summary>
    public record EnrolInputDto
    {
        public string? Password { get; init; }
        public bool? AcceptedRules { get; init; }
    }

    public record ProblemSummaryDto
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
        /// <summary>`untouched` | `attempted` | `partial` | `solved`. The reader's own.</summary>
        public required string Status { get; init; }
        /// <summary>Rescaled into the assignment's scale by the Server.</summary>
        public double? BestScore { get; init; }
        public double? MaxScore { get; init; }
        public required int Attempts { get; init; }
    }

    public record SeriesDto
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        /// <summary>
        /// Whether it is running now. <b>The Server sets it</b>; no screen decides
        /// this by looking at a date.
        /// </summary>
        public required bool IsOpen { get; init; }
        public string? PausedAt { get; init; }
        public string? RankingVisibleFrom { get; init; }
        public string? RankingVisibleTo { get; init; }
        /// <summary>
        /// How many problems the series holds, when the manager allows that to be
        /// shown before it opens. Absent means even the count is withheld.
        /// </summary>
        public int? ProblemCount { get; init; }
        /// <summary>
        /// <b>Absent</b> — not empty — before the series opens: a series that has
        /// not started does not disclose what it holds.
        /// </summary>
        public IReadOnlyList<ProblemSummaryDto>? Problems { get; init; }
    }

    public record ProblemSampleDto
    {
        public required string Input { get; init; }
        public required string Output { get; init; }
        public string? Explanation { get; init; }
    }

    public record AttachmentDto
    {
        public required string Name { get; init; }
        public required string MimeType { get; init; }
        public required long SizeBytes { get; init; }
        public required string Url { get; init; }
        public required string Sha256 { get; init; }
    }

    /// <summary>A field the submit form must render, declared by the problem type.</summary>
    public record SubmitFieldDto
    {
        /// <summary>`file` | `code`.</summary>
        public required string Kind { get; init; }
        public required string Name { get; init; }
        public required string Label { get; init; }
        public IReadOnlyList<string>? Accept { get; init; }
    }

    public record ProblemDetailDto
    {
        public required string Id { get; init; }
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public required string Type { get; init; }
        public required string SeriesId { get; init; }
        /// <summary>
        /// The statement as <b>references</b>: `content.md`, its translations, and
        /// a `content.pdf` where the problem ships one. Fetched from the file API.
        /// </summary>
        public required IReadOnlyList<StatementRefDto> Statements { get; init; }
        /// <summary>Everything scoped to participants. Well-known `content.*` files excluded.</summary>
        public required IReadOnlyList<AttachmentDto> Attachments { get; init; }
        // **`Limits` was here from the day this contract was written, and it
        // was never once filled.** `SERVER_CONTRACT.md` called it "declared and
        // unfillable" and was right: the limits live inside a document the
        // Server stores and does not read, so there was no value it could put
        // here without becoming a reader of a problem type's vocabulary.
        //
        // The badges a participant saw rendered against the Client's fake alone.
        // They come from `config` below now, which reaches this screen for
        // exactly this reason.
        public IReadOnlyList<ProblemSampleDto>? Samples { get; init; }
        public required string Status { get; init; }
        public double? BestScore { get; init; }
        public double? MaxScore { get; init; }
        public required int Attempts { get; init; }
        /// <summary>
        /// Anything that changes the verdict — the limits, the languages that may
        /// be submitted. <b>Reaches the participant deliberately</b>: none of it
        /// is secret, and it is what a problem page has to show. The package's own
        /// `config.yml` stays unpublished, because it names the checker.
        /// </summary>
        public object? Config { get; init; }
        /// <summary>What the submit form is drawn and validated from.</summary>
        public object? Spec { get; init; }
        /// <summary>Display only — captions, the languages written out for a header.</summary>
        public object? Props { get; init; }
        public required long MaxUploadBytes { get; init; }
        public required IReadOnlyList<SubmitFieldDto> SubmitFields { get; init; }
        /// <summary>Absent means unlimited.</summary>
        public int? SubmissionsLeft { get; init; }
    }

    public record SubmissionSummaryDto
    {
        public required string Id { get; init; }
        public required string ProblemId { get; init; }
        public required string ProblemSlug { get; init; }
        public required string ProblemName { get; init; }
        public required string SeriesId { get; init; }
        public required string SubmittedAt { get; init; }
        /// <summary>
        /// What the participant declared beside the bytes — the language among it.
        /// Opaque: the Client's renderer for the type reads it, the Server does not.
        /// </summary>
        public object? Props { get; init; }
        /// <summary>`queued` | `running` | `completed` | `failed` | `cancelled`.</summary>
        public required string State { get; init; }
        /// <summary>Rescaled into the assignment's scale. Absent while unjudged.</summary>
        public double? Score { get; init; }
        public double? MaxScore { get; init; }
        /// <summary>Short label from the Runner. Its meaning is the type's business.</summary>
        public string? Verdict { get; init; }
    }

    /// <summary>One attempt at evaluating a submission. A rejudge adds an attempt.</summary>
    public record EvaluationAttemptDto
    {
        public required string Id { get; init; }
        public required int Attempt { get; init; }
        public required string StartedAt { get; init; }
        public string? FinishedAt { get; init; }
        public required string State { get; init; }
        public string? Verdict { get; init; }
        public double? Score { get; init; }
        /// <summary>
        /// What the problem type wants shown beside <b>this</b> result — the
        /// toolchain that compiled it, a per-language note. Opaque, and the pair
        /// to the board's `extra`: the difference between them is the audience.
        /// </summary>
        public object? Props { get; init; }
        /// <summary>
        /// Carries only what the reader may see. An empty list means nothing was
        /// attached <b>or</b> nothing here is for this reader, and the screen has
        /// no business telling those apart.
        /// </summary>
        public required IReadOnlyList<SubmissionFileDto> Files { get; init; }
    }

    /* ── Results, from which every ranking is computed ───────────────────── */

    /// <summary>
    /// Somebody with a row on the board.
    /// <para>
    /// A <b>contestant</b>, not a user: an ICPC row is a team and three people
    /// submit for it. Who typed a particular solution is the submissions
    /// screen's business and is not disclosed here.
    /// </para>
    /// </summary>
    public record ContestantDto
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
    }

    /// <summary>A problem as a board's column: what it is called and what it is worth.</summary>
    public record ResultProblemDto
    {
        public required string Id { get; init; }
        /// <summary>Unique across the whole activity, which is why cells key on it.</summary>
        public required string Slug { get; init; }
        public required string Name { get; init; }
        public required double MaxPoints { get; init; }
    }

    /// <summary>
    /// One round the results cover. Carried even where nobody has attempted
    /// anything in it, because a board's columns are the problems that exist.
    /// </summary>
    public record ResultSeriesDto
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        /// <summary>What a penalty minute is counted from. Absent in an untimed activity.</summary>
        public string? StartDate { get; init; }
        /// <summary>
        /// The board is frozen right now: outcomes after the freeze arrive
        /// withheld. Renderers mark a frozen round's columns.
        /// </summary>
        public required bool Frozen { get; init; }
        public string? RevealAt { get; init; }
        public required IReadOnlyList<ResultProblemDto> Problems { get; init; }
    }

    /// <summary>
    /// One submission, reduced to what a board needs.
    /// <para>
    /// Deliberately not <see cref="SubmissionSummaryDto"/>: the verdict text, the
    /// language and the Runner's per-test document say more about somebody's
    /// solution than a scoreboard has any business publishing.
    /// </para>
    /// </summary>
    public record ContestantResultDto
    {
        public required string Id { get; init; }
        public required string ContestantId { get; init; }
        public required string SeriesId { get; init; }
        public required string ProblemId { get; init; }
        public required string ProblemSlug { get; init; }
        public required string SubmittedAt { get; init; }
        /// <summary>
        /// Absent while frozen, and while nothing has judged it yet. Absent is
        /// not zero: a board must not score what it has not been told.
        /// </summary>
        public double? Points { get; init; }
        public string? State { get; init; }
        /// <summary>
        /// Whatever else the problem type wants a board to have. Public by
        /// construction, bounded at 2 kB, and withheld under a freeze with
        /// everything else about the outcome.
        /// </summary>
        public object? Extra { get; init; }
        /// <summary>
        /// Its outcome is withheld: that it happened is all that is disclosed.
        /// Omitting the result instead would leave a board unable to tell "did
        /// not try" from "tried, and you may not know yet".
        /// </summary>
        public bool? Frozen { get; init; }
    }

    /// <summary>Everything a board is computed from. The Server sends no board.</summary>
    public record ActivityResultsDto
    {
        /// <summary>In the order the rounds run.</summary>
        public required IReadOnlyList<ResultSeriesDto> Series { get; init; }
        /// <summary>Everyone with a row, including those who have sent nothing.</summary>
        public required IReadOnlyList<ContestantDto> Contestants { get; init; }
        public required IReadOnlyList<ContestantResultDto> Results { get; init; }
        /// <summary>Which contestant the reader is, where they are one.</summary>
        public string? Me { get; init; }
    }

    /* ── Questions ───────────────────────────────────────────────────────── */

    public record QuestionAnswerDto
    {
        public required string Body { get; init; }
        public required string AuthorName { get; init; }
        public required string AnsweredAt { get; init; }
    }

    public record QuestionDto
    {
        public required string Id { get; init; }
        /// <summary>`question` | `announcement`.</summary>
        public required string Kind { get; init; }
        public required string Topic { get; init; }
        public required string Body { get; init; }
        public required string AuthorName { get; init; }
        public required string CreatedAt { get; init; }
        public string? SeriesId { get; init; }
        public string? SeriesName { get; init; }
        public string? ProblemId { get; init; }
        public string? ProblemSlug { get; init; }
        public string? ProblemName { get; init; }
        /// <summary>A question reaches every participant only once a manager publishes it.</summary>
        public required bool IsPublished { get; init; }
        public required bool IsRead { get; init; }
        public QuestionAnswerDto? Answer { get; init; }
    }

    /// <summary>
    /// A question is asked about <b>one</b> of three things: both absent is the
    /// activity at large, `seriesId` alone is a series, `problemId` is one
    /// problem — and its series is filled in from it.
    /// </summary>
    public record AskQuestionInputDto
    {
        public required string Topic { get; init; }
        public required string Body { get; init; }
        public string? ProblemId { get; init; }
        public string? SeriesId { get; init; }
    }

    public record SubmissionDetailDto : SubmissionSummaryDto
    {
        /// <summary>Needed here because the result renderer is chosen by the type.</summary>
        public required string ProblemType { get; init; }
        public required string AuthorName { get; init; }
        /// <summary>Newest first.</summary>
        public required IReadOnlyList<EvaluationAttemptDto> Attempts { get; init; }
        /// <summary>What was sent. On the submission, because every rejudge reads the same bytes.</summary>
        public required IReadOnlyList<SubmissionFileDto> Files { get; init; }
    }
}

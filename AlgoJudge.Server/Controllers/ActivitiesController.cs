using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// The participant's view of an activity.
    /// <para>
    /// The manager's view of the same three reads lives under <c>/manager</c>
    /// (<see cref="ManagerActivitiesController"/>). They were the same paths in
    /// the Client's contract and could not be: `Activity` and `ManagedActivity`
    /// are not supersets of one another, so one method and path would have had
    /// two response schemas.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("activities")]
    [Authorize]
    public class ActivitiesController(
        IActivityService activities,
        ISeriesService series,
        IProblemService problems,
        ISubmissionService submissions,
        IResultsService results,
        IQuestionService questions,
        IFileService files
    ) : ControllerBase
    {
        /// <summary>
        /// Every result the reader may see, from which a board is computed.
        /// <para>
        /// The Server sends <b>results, not a ranking</b>: which board they add
        /// up to is the activity's `rankingType`, and a Server computing an ICPC
        /// penalty would be encoding the semantics of one ranking type.
        /// </para>
        /// <para>
        /// What stays here is disclosure: the window decides whether there is an
        /// answer, `scoreVisibility` decides whose results are in it, and the
        /// freeze withholds outcomes. `seriesId` narrows to one round.
        /// </para>
        /// </summary>
        [HttpGet("{idOrSlug}/results")]
        [ProducesResponseType<ActivityResultsDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public Task<ActivityResultsDto> Results(
            string idOrSlug, [FromQuery] Guid? seriesId, CancellationToken ct) =>
            results.GetAsync(idOrSlug, seriesId, ct);

        /// <summary>
        /// Puts the signed-in reader into the activity themselves.
        /// <para>
        /// Answers with the activity as they now see it, so the page redraws from
        /// what came back. A wrong or missing password is refused here — the
        /// Client sends what the form collected and checks nothing.
        /// </para>
        /// </summary>
        [HttpPost("{idOrSlug}/enrolment")]
        [ProducesResponseType<ActivityDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public Task<ActivityDto> Enrol(
            string idOrSlug, [FromBody] EnrolInputDto input, CancellationToken ct) =>
            activities.EnrolAsync(idOrSlug, input, ct);

        [HttpGet("{idOrSlug}/questions")]
        [ProducesResponseType<PageDto<QuestionDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<QuestionDto>> Questions(
            string idOrSlug,
            [FromQuery] int page,
            [FromQuery] int pageSize,
            [FromQuery] string? search,
            [FromQuery] string? kind,
            [FromQuery] Guid? seriesId,
            [FromQuery] Guid? problemId,
            CancellationToken ct) =>
            questions.ListAsync(
                idOrSlug, new PageQuery { Page = page, PageSize = pageSize },
                search, kind, seriesId, problemId, ct);

        [HttpPost("{idOrSlug}/questions")]
        [ProducesResponseType<QuestionDto>(StatusCodes.Status201Created)]
        public async Task<ActionResult<QuestionDto>> Ask(
            string idOrSlug, [FromBody] AskQuestionInputDto input, CancellationToken ct)
        {
            var asked = await questions.AskAsync(idOrSlug, input, ct);
            return Created($"/api/v1/activities/{idOrSlug}/questions/{asked.Id}", asked);
        }

        [HttpPost("{idOrSlug}/questions/{questionId:guid}/read")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> MarkRead(string idOrSlug, Guid questionId, CancellationToken ct)
        {
            await questions.MarkReadAsync(idOrSlug, questionId, ct);
            return NoContent();
        }

        /// <summary>
        /// The activities this reader may see.
        /// <para>
        /// <b>The Server decides what that means.</b> One that is closed, or
        /// hidden from people not in it, is simply not in the answer — the Client
        /// never filters on `joinPolicy` or `unlisted` itself, because a rule the
        /// Client enforced would be a rule anybody could turn off.
        /// </para>
        /// </summary>
        [HttpGet]
        [ProducesResponseType<PageDto<ActivityDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<ActivityDto>> List(
            [FromQuery] int page, [FromQuery] int pageSize, [FromQuery] string? state, CancellationToken ct) =>
            activities.ListAsync(
                new PageQuery { Page = page, PageSize = pageSize },
                state?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                ct);

        /// <summary>Accepts an id or a slug, and answers for somebody not enrolled too.</summary>
        [HttpGet("{idOrSlug}")]
        [ProducesResponseType<ActivityDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public Task<ActivityDto> Get(string idOrSlug, CancellationToken ct) =>
            activities.GetAsync(idOrSlug, ct);

        [HttpGet("{idOrSlug}/series")]
        [ProducesResponseType<IReadOnlyList<SeriesDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<SeriesDto>> Series(string idOrSlug, CancellationToken ct) =>
            series.ListForParticipantAsync(idOrSlug, ct);

        [HttpGet("{idOrSlug}/problems/{problemSlug}")]
        [ProducesResponseType<ProblemDetailDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public Task<ProblemDetailDto> Problem(string idOrSlug, string problemSlug, CancellationToken ct) =>
            problems.GetForParticipantAsync(idOrSlug, problemSlug, ct);

        [HttpGet("{idOrSlug}/submissions")]
        [ProducesResponseType<PageDto<SubmissionSummaryDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<SubmissionSummaryDto>> Submissions(
            string idOrSlug, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken ct) =>
            submissions.ListAsync(idOrSlug, new PageQuery { Page = page, PageSize = pageSize }, ct);

        [HttpGet("{idOrSlug}/submissions/{submissionId:guid}")]
        [ProducesResponseType<SubmissionDetailDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public Task<SubmissionDetailDto> Submission(string idOrSlug, Guid submissionId, CancellationToken ct) =>
            submissions.GetAsync(idOrSlug, submissionId, ct);

        /// <summary>
        /// Sends a solution.
        /// <para>
        /// Multipart, because a submission may be a file — and the upload and the
        /// submission travel together, which is the one deliberate exception to
        /// the single file endpoint.
        /// </para>
        /// </summary>
        [HttpPost("{idOrSlug}/problems/{problemSlug}/submissions")]
        [ProducesResponseType<SubmissionSummaryDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        [Consumes("multipart/form-data")]
        // The file is optional: a submission may be pasted source instead.
        [Api.MultipartForm(File = "file", Fields = ["language", "code", "sha256"])]
        [RequestSizeLimit(UploadLimits.Submission)]
        [DisableFormValueModelBinding]
        public async Task<ActionResult<SubmissionSummaryDto>> Submit(
            string idOrSlug,
            string problemSlug,
            CancellationToken ct)
        {
            var upload = await MultipartUpload.ReadAsync(
                Request, UploadLimits.Submission,
                (content, _, _, token) => files.StageAsync(content, token), ct);

            var language = upload.Fields.TryGetValue("language", out var chosen) ? chosen : null;
            var code = upload.Fields.TryGetValue("code", out var pasted) ? pasted : null;

            // One of the two, never both: the form offers an editor or a file
            // field, and which it offers is the problem type's business.
            if (upload.File is null && code is null)
            {
                throw new ValidationException("Send a file or some source", "submission.empty");
            }

            // Pasted source is stored exactly like an uploaded file, so that one
            // path serves both and a submission is a submission afterwards.
            var staged = upload.File
                ?? await files.StageAsync(
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(code!)), ct);

            var name = upload.FileName is { Length: > 0 } uploaded
                ? uploaded
                : $"main.{Extension(language)}";

            try
            {
                var result = await submissions.SubmitAsync(
                    idOrSlug, problemSlug, language, staged, name, upload.Field("sha256"), ct);

                return Created($"/api/v1/activities/{idOrSlug}/submissions/{result.Id}", result);
            }
            catch
            {
                // The bytes are down before the rules are asked — a closed round,
                // a language this activity does not take, one submission too many.
                // Every one of those must leave nothing behind, and the collector
                // is a day too late to be the answer.
                await files.DiscardAsync(staged, ct);
                throw;
            }
        }

        /// <summary>
        /// A plausible file name for pasted source. Cosmetic — the Runner is told
        /// the language, and the extension is what a person downloading it reads.
        /// </summary>
        private static string Extension(string? language) => language switch
        {
            "cpp" or "c++" => "cpp",
            "c" => "c",
            "python" => "py",
            "java" => "java",
            "csharp" => "cs",
            "rust" => "rs",
            "go" => "go",
            _ => "txt",
        };
    }
}

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
        ISubmissionService submissions
    ) : ControllerBase
    {
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
        public async Task<ActionResult<SubmissionSummaryDto>> Submit(
            string idOrSlug,
            string problemSlug,
            [FromForm] string? language,
            [FromForm] string? code,
            [FromForm] IFormFile? file,
            [FromForm] string? sha256,
            CancellationToken ct)
        {
            // One of the two, never both: the form offers an editor or a file
            // field, and which it offers is the problem type's business.
            if (file is null && code is null)
            {
                throw new ValidationException("Send a file or some source", "submission.empty");
            }

            await using var content = file is not null
                ? file.OpenReadStream()
                : new MemoryStream(System.Text.Encoding.UTF8.GetBytes(code!));

            var name = file?.FileName ?? $"main.{Extension(language)}";
            var result = await submissions.SubmitAsync(
                idOrSlug, problemSlug, language, content, name, sha256 ?? "", ct);

            return Created($"/api/v1/activities/{idOrSlug}/submissions/{result.Id}", result);
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

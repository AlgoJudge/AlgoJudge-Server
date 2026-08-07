using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// The manager's reads of an activity.
    /// <para>
    /// Under <c>/manager</c> because the participant's reads own the plain paths
    /// and the two answer with different shapes. The prefix is not invented — the
    /// contract already had `GET /manager/activities` — this makes it a rule
    /// rather than an exception.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("manager/activities")]
    [Authorize]
    public class ManagerActivitiesController(
        IActivityService activities,
        ISeriesService series
    ) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<PageDto<ManagedActivityDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<ManagedActivityDto>> List(
            [FromQuery] int page,
            [FromQuery] int pageSize,
            [FromQuery] string? search,
            [FromQuery] bool includeArchived,
            CancellationToken ct) =>
            activities.ListManagedAsync(
                new PageQuery { Page = page, PageSize = pageSize }, search, includeArchived, ct);

        /// <summary>
        /// The picker's list: every activity this manager may attach to, without
        /// the counts or the settings. Its own path because the collection above
        /// now answers with the full model.
        /// </summary>
        [HttpGet("summary")]
        [ProducesResponseType<IReadOnlyList<ManagedActivitySummaryDto>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<ManagedActivitySummaryDto>> Summary(CancellationToken ct)
        {
            var page = await activities.ListManagedAsync(
                new PageQuery { Page = 1, PageSize = PageQuery.MaxSize }, null, false, ct);

            return page.Items
                .Select(a => new ManagedActivitySummaryDto { Id = a.Id, Slug = a.Slug, Name = a.Name })
                .ToList();
        }

        [HttpGet("{idOrSlug}")]
        [ProducesResponseType<ManagedActivityDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public Task<ManagedActivityDto> Get(string idOrSlug, CancellationToken ct) =>
            activities.GetManagedAsync(idOrSlug, ct);

        [HttpGet("{idOrSlug}/series")]
        [ProducesResponseType<IReadOnlyList<ManagedSeriesDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<ManagedSeriesDto>> Series(string idOrSlug, CancellationToken ct) =>
            series.ListManagedAsync(idOrSlug, ct);
    }

    /// <summary>
    /// Writing to an activity. These keep the plain paths: a participant has no
    /// `POST /activities`, so there is nothing to collide with.
    /// </summary>
    [ApiController]
    [Route("activities")]
    [Authorize]
    public class ActivityWritesController(
        IActivityService activities,
        ISeriesService series
    ) : ControllerBase
    {
        [HttpPost]
        [ProducesResponseType<ManagedActivityDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ManagedActivityDto>> Create(
            [FromBody] ActivityInputDto input, CancellationToken ct)
        {
            var created = await activities.CreateAsync(input, ct);
            return Created($"/api/v1/manager/activities/{created.Id}", created);
        }

        [HttpPost("{idOrSlug}/series")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ManagedSeriesDto>> CreateSeries(
            string idOrSlug, [FromBody] SeriesInputDto input, CancellationToken ct)
        {
            var created = await series.CreateAsync(idOrSlug, input, ct);
            return Created($"/api/v1/manager/activities/{idOrSlug}/series", created);
        }
    }

    /// <summary>Assignments: attaching a library problem to a round.</summary>
    [ApiController]
    [Route("series")]
    [Authorize]
    public class SeriesController(ISeriesService series) : ControllerBase
    {
        /// <summary>
        /// Attaching pins the library's current version (decided 2026-08-08), so
        /// publishing a correction does not change what a running round is judged
        /// against.
        /// </summary>
        [HttpPost("{seriesId:guid}/problems")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ManagedSeriesDto>> Attach(
            Guid seriesId, [FromBody] SeriesProblemInputDto input, CancellationToken ct)
        {
            var updated = await series.AttachProblemAsync(seriesId, input, ct);
            return Created($"/api/v1/series/{seriesId}/problems", updated);
        }
    }

    /// <summary>The problem library.</summary>
    [ApiController]
    [Route("problems")]
    [Authorize]
    public class ProblemsController(IProblemService problems) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<PageDto<ManagedProblemDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<ManagedProblemDto>> List(
            [FromQuery] int page,
            [FromQuery] int pageSize,
            [FromQuery] string? search,
            [FromQuery] bool mineOnly,
            [FromQuery] bool includeArchived,
            CancellationToken ct) =>
            problems.ListAsync(
                new PageQuery { Page = page, PageSize = pageSize }, search, mineOnly, includeArchived, ct);

        [HttpPost]
        [ProducesResponseType<ManagedProblemDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ManagedProblemDto>> Create(
            [FromBody] ProblemInputDto input, CancellationToken ct)
        {
            var created = await problems.CreateAsync(input, ct);
            return Created($"/api/v1/problems/{created.Id}", created);
        }

        /// <summary>
        /// Publishes a version, whole. Versions are append-only: an existing one
        /// takes no new file and no new package.
        /// </summary>
        [HttpPost("{problemId:guid}/versions")]
        [ProducesResponseType<ManagedProblemVersionDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<ManagedProblemVersionDto>> PublishVersion(
            Guid problemId, [FromBody] ProblemVersionInputDto input, CancellationToken ct)
        {
            var created = await problems.PublishVersionAsync(problemId, input, ct);
            return Created($"/api/v1/problems/{problemId}/versions/{created.Id}", created);
        }
    }
}

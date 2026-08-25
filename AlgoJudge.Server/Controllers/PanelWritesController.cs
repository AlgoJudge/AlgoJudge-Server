using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// Writing to an activity, its documents, and the order of its rounds.
    /// <para>
    /// These keep the plain <c>/activities</c> paths: a participant has no write
    /// there, so nothing collides with the manager's — unlike the three reads,
    /// which moved under <c>/manager</c>.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("activities")]
    [Authorize]
    public class ActivityAdminController(
        IManagerWriteService writes,
        IActivityService activities,
        IDocumentService documents,
        IManagerReadService panel,
        IPermissionService permissions
    ) : ControllerBase
    {
        [HttpPut("{idOrSlug}")]
        [ProducesResponseType<ManagedActivityDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<ManagedActivityDto> Update(
            string idOrSlug, [FromBody] ActivityInputDto input, CancellationToken ct) =>
            writes.UpdateActivityAsync(idOrSlug, input, ct);

        /// <summary>
        /// Archiving is how an activity ends: still readable, accepting nothing
        /// new. Deleting destroys submissions people may want to look back at,
        /// which is why it is a separate permission.
        /// </summary>
        [HttpPost("{idOrSlug}/archived")]
        [ProducesResponseType<ManagedActivityDto>(StatusCodes.Status200OK)]
        public Task<ManagedActivityDto> SetArchived(
            string idOrSlug, [FromBody] ArchivedInputDto input, CancellationToken ct) =>
            writes.SetActivityArchivedAsync(idOrSlug, input.Archived, ct);

        [HttpDelete("{idOrSlug}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(string idOrSlug, CancellationToken ct)
        {
            await writes.DeleteActivityAsync(idOrSlug, ct);
            return NoContent();
        }

        [HttpPost("{idOrSlug}/series/order")]
        [ProducesResponseType<IReadOnlyList<ManagedSeriesDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<ManagedSeriesDto>> ReorderSeries(
            string idOrSlug, [FromBody] OrderInputDto input, CancellationToken ct) =>
            writes.ReorderSeriesAsync(idOrSlug, input.OrderedIds, ct);

        [HttpPost("{idOrSlug}/documents/{kind}")]
        [ProducesResponseType<ManagedActivityDto>(StatusCodes.Status200OK)]
        public async Task<ManagedActivityDto> PublishDocument(
            string idOrSlug, string kind, [FromBody] PublishDocumentInputDto input, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);
            await documents.PublishAsync(FileOwnerKind.ActivityDocument, activity.Id, kind, input, ct);
            return await activities.GetManagedAsync(idOrSlug, ct);
        }

        [HttpDelete("{idOrSlug}/documents/{kind}")]
        [ProducesResponseType<ManagedActivityDto>(StatusCodes.Status200OK)]
        public async Task<ManagedActivityDto> UnpublishDocument(
            string idOrSlug, string kind, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);
            await documents.UnpublishAsync(FileOwnerKind.ActivityDocument, activity.Id, kind, ct);
            return await activities.GetManagedAsync(idOrSlug, ct);
        }

        [HttpGet("{idOrSlug}/documents/{kind}")]
        [ProducesResponseType<IReadOnlyList<ActivityDocumentRefDto>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<ActivityDocumentRefDto>> DocumentHistory(
            string idOrSlug, string kind, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            var history = await documents.HistoryAsync(FileOwnerKind.ActivityDocument, activity.Id, kind, ct);
            return history.Select(Projections.ActivityDocument).ToList();
        }

        [HttpPost("{idOrSlug}/announcements")]
        [ProducesResponseType<ManagedQuestionDto>(StatusCodes.Status201Created)]
        public async Task<ActionResult<ManagedQuestionDto>> Announce(
            string idOrSlug, [FromBody] AnnouncementInputDto input, CancellationToken ct)
        {
            var created = await panel.AnnounceAsync(idOrSlug, input, ct);
            return Created($"/api/v1/questions/{created.Id}", created);
        }
    }

    /// <summary>Rounds: their times, their pauses and the order of their problems.</summary>
    [ApiController]
    [Route("series")]
    [Authorize]
    public class SeriesAdminController(
        IManagerWriteService writes,
        IManagerReadService panel,
        ISeriesService series
    ) : ControllerBase
    {
        [HttpPut("{seriesId:guid}")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status200OK)]
        public Task<ManagedSeriesDto> Update(
            Guid seriesId, [FromBody] SeriesInputDto input, CancellationToken ct) =>
            writes.UpdateSeriesAsync(seriesId, input, ct);

        /// <summary>
        /// Copies a round, with the problems assigned to it, into this activity
        /// or another one. Nothing that happened travels and the copy is shut.
        /// </summary>
        [HttpPost("{seriesId:guid}/duplicate")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ManagedSeriesDto>> Duplicate(
            Guid seriesId, [FromBody] DuplicateSeriesDto input, CancellationToken ct)
        {
            var copy = await series.DuplicateAsync(
                seriesId, input.TargetActivityId, input.Slug ?? "", input.StartsAt, ct);
            return Created($"/api/v1/series/{copy.Id}", copy);
        }

        [HttpDelete("{seriesId:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(Guid seriesId, CancellationToken ct)
        {
            await writes.DeleteSeriesAsync(seriesId, ct);
            return NoContent();
        }

        /// <summary>
        /// A <b>delta</b>, not two dates: two managers reacting to the same
        /// delayed round would each read the old start, add ten minutes and write
        /// the same time — losing one of the two shifts.
        /// </summary>
        [HttpPost("{seriesId:guid}/shift")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status200OK)]
        public Task<ManagedSeriesDto> Shift(
            Guid seriesId, [FromBody] ShiftInputDto input, CancellationToken ct) =>
            writes.ShiftSeriesAsync(seriesId, input.Minutes, ct);

        [HttpPost("{seriesId:guid}/pause")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<ManagedSeriesDto> Pause(
            Guid seriesId, [FromBody] PauseInputDto input, CancellationToken ct) =>
            writes.PauseSeriesAsync(seriesId, input.HideProblems, ct);

        [HttpPost("{seriesId:guid}/resume")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<ManagedSeriesDto> Resume(
            Guid seriesId, [FromBody] ResumeInputDto input, CancellationToken ct) =>
            writes.ResumeSeriesAsync(seriesId, input.ExtendEnd, ct);

        [HttpPost("{seriesId:guid}/problems/order")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status200OK)]
        public Task<ManagedSeriesDto> ReorderProblems(
            Guid seriesId, [FromBody] OrderInputDto input, CancellationToken ct) =>
            writes.ReorderProblemsAsync(seriesId, input.OrderedIds, ct);

        [HttpPost("{seriesId:guid}/rejudge")]
        [ProducesResponseType<int>(StatusCodes.Status200OK)]
        public Task<int> Rejudge(Guid seriesId, CancellationToken ct) =>
            panel.RejudgeSeriesAsync(seriesId, ct);
    }

    /// <summary>One problem's attachment to one round.</summary>
    [ApiController]
    [Route("series-problems")]
    [Authorize]
    public class AssignmentsController(
        IManagerWriteService writes,
        IManagerReadService panel
    ) : ControllerBase
    {
        [HttpPut("{assignmentId:guid}")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status200OK)]
        public Task<ManagedSeriesDto> Update(
            Guid assignmentId, [FromBody] SeriesProblemInputDto input, CancellationToken ct) =>
            writes.UpdateAssignmentAsync(assignmentId, input, ct);

        /// <summary>
        /// Refused once anything has been submitted here: the submissions point
        /// at this assignment, and a standing computed from them would develop a
        /// hole.
        /// </summary>
        [HttpDelete("{assignmentId:guid}")]
        [ProducesResponseType<ManagedSeriesDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<ManagedSeriesDto> Detach(Guid assignmentId, CancellationToken ct) =>
            writes.DetachAsync(assignmentId, ct);

        [HttpPost("{assignmentId:guid}/rejudge")]
        [ProducesResponseType<int>(StatusCodes.Status200OK)]
        public Task<int> Rejudge(Guid assignmentId, CancellationToken ct) =>
            panel.RejudgeAssignmentAsync(assignmentId, ct);
    }

    /// <summary>The rest of the problem library.</summary>
    [ApiController]
    [Route("problems")]
    [Authorize]
    public class ProblemAdminController(IProblemService problems) : ControllerBase
    {
        [HttpGet("{id:guid}")]
        [ProducesResponseType<ManagedProblemDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public Task<ManagedProblemDto> Get(Guid id, CancellationToken ct) => problems.GetAsync(id, ct);

        [HttpPut("{id:guid}")]
        [ProducesResponseType<ManagedProblemDto>(StatusCodes.Status200OK)]
        public Task<ManagedProblemDto> Update(
            Guid id, [FromBody] ProblemInputDto input, CancellationToken ct) =>
            problems.UpdateAsync(id, input, ct);

        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await problems.DeleteAsync(id, ct);
            return NoContent();
        }

        /// <summary>
        /// Retires it: gone from the attach picker and taking no new versions,
        /// while every assignment already using it keeps working.
        /// </summary>
        [HttpPost("{id:guid}/archived")]
        [ProducesResponseType<ManagedProblemDto>(StatusCodes.Status200OK)]
        public Task<ManagedProblemDto> SetArchived(
            Guid id, [FromBody] ArchivedInputDto input, CancellationToken ct) =>
            problems.SetArchivedAsync(id, input.Archived, ct);

        /// <summary>Copies only the newest version, as version 1 of a new problem.</summary>
        [HttpPost("{id:guid}/duplicate")]
        [ProducesResponseType<ManagedProblemDto>(StatusCodes.Status201Created)]
        public async Task<ActionResult<ManagedProblemDto>> Duplicate(Guid id, CancellationToken ct)
        {
            var copy = await problems.DuplicateAsync(id, ct);
            return Created($"/api/v1/problems/{copy.Id}", copy);
        }

        [HttpPost("{id:guid}/visibility")]
        [ProducesResponseType<ManagedProblemDto>(StatusCodes.Status200OK)]
        public Task<ManagedProblemDto> SetVisibility(
            Guid id, [FromBody] VisibilityInputDto input, CancellationToken ct) =>
            problems.SetVisibilityAsync(id, input.Visibility, input.SharedWith, ct);

        [HttpGet("{problemId:guid}/versions")]
        [ProducesResponseType<IReadOnlyList<ManagedProblemVersionDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<ManagedProblemVersionDto>> Versions(
            Guid problemId, CancellationToken ct) =>
            problems.ListVersionsAsync(problemId, ct);

        [HttpGet("{problemId:guid}/versions/{versionId:guid}/content")]
        [ProducesResponseType<IReadOnlyList<StatementRefDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<StatementRefDto>> Content(
            Guid problemId, Guid versionId, CancellationToken ct) =>
            problems.ContentAsync(problemId, versionId, ct);

        /// <summary>
        /// The Runner archive. 404 when the version has none — a version nobody
        /// has finished preparing is not a failure, and the Client treats the
        /// absence as one it can show.
        /// </summary>
        [HttpGet("{problemId:guid}/versions/{versionId:guid}/package")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Package(Guid problemId, Guid versionId, CancellationToken ct)
        {
            var package = await problems.PackageAsync(problemId, versionId, ct)
                ?? throw new NotFoundException("Package");
            return File(package.Bytes, "application/zip", package.Name);
        }
    }
}

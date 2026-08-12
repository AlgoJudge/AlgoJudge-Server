using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>Permission templates: the sets a grant starts from.</summary>
    [ApiController]
    [Route("permission-templates")]
    [Authorize]
    public class PermissionTemplatesController(IGrantService grants) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<PermissionTemplateDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<PermissionTemplateDto>> List(CancellationToken ct) =>
            grants.ListTemplatesAsync(ct);

        [HttpPost]
        [ProducesResponseType<PermissionTemplateDto>(StatusCodes.Status201Created)]
        public async Task<ActionResult<PermissionTemplateDto>> Create(
            [FromBody] PermissionTemplateInputDto input, CancellationToken ct)
        {
            var created = await grants.CreateTemplateAsync(input, ct);
            return Created($"/api/v1/permission-templates/{created.Id}", created);
        }

        /// <summary>PUT, not POST: the input is the whole template, so this replaces it.</summary>
        [HttpPut("{id:guid}")]
        [ProducesResponseType<PermissionTemplateDto>(StatusCodes.Status200OK)]
        public Task<PermissionTemplateDto> Update(
            Guid id, [FromBody] PermissionTemplateInputDto input, CancellationToken ct) =>
            grants.UpdateTemplateAsync(id, input, ct);

        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await grants.DeleteTemplateAsync(id, ct);
            return NoContent();
        }
    }

    /// <summary>Who may do what, and — in an activity — who is in it.</summary>
    [ApiController]
    [Route("grants")]
    [Authorize]
    public class GrantsController(IGrantService grants) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<PageDto<GrantDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<GrantDto>> List(
            [FromQuery] int page, [FromQuery] int pageSize,
            [FromQuery] string? userId, [FromQuery] Guid? activityId, [FromQuery] string? scope,
            CancellationToken ct) =>
            grants.ListAsync(new PageQuery { Page = page, PageSize = pageSize }, userId, activityId, scope, ct);

        [HttpPost]
        [ProducesResponseType<GrantDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public Task<GrantDto> Set([FromBody] GrantInputDto input, CancellationToken ct) =>
            grants.SetAsync(input, ct);

        /// <summary>
        /// Revoking removes the row — a grant has no revoked state — so it is a
        /// delete. In an activity that also removes the membership, because the
        /// grant is the membership.
        /// </summary>
        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
        {
            await grants.RevokeAsync(id, ct);
            return NoContent();
        }
    }

    [ApiController]
    [Route("users")]
    [Authorize]
    public class UsersController(IUserService users) : ControllerBase
    {
        /// <summary>The lookup a grant editor cannot do without.</summary>
        [HttpGet]
        [ProducesResponseType<IReadOnlyList<ManagedUserSummaryDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<ManagedUserSummaryDto>> Search(
            [FromQuery] string? q, CancellationToken ct) => users.SearchAsync(q, ct);

        [HttpGet("managed")]
        [ProducesResponseType<PageDto<ManagedUserDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<ManagedUserDto>> List(
            [FromQuery] int page, [FromQuery] int pageSize, [FromQuery] string? search,
            [FromQuery] bool includeBlocked, [FromQuery] bool temporaryOnly, CancellationToken ct) =>
            users.ListAsync(
                new PageQuery { Page = page, PageSize = pageSize }, search, includeBlocked, temporaryOnly, ct);

        /// <summary>The password comes back once. The Server keeps only a hash.</summary>
        [HttpPost]
        [ProducesResponseType<CreatedCredentialDto>(StatusCodes.Status201Created)]
        public async Task<ActionResult<CreatedCredentialDto>> Create(
            [FromBody] UserInputDto input, CancellationToken ct)
        {
            var created = await users.CreateAsync(input, ct);
            return Created($"/api/v1/users/{created.UserId}", created);
        }

        [HttpPost("temporary")]
        [ProducesResponseType<IReadOnlyList<CreatedCredentialDto>>(StatusCodes.Status201Created)]
        public async Task<ActionResult<IReadOnlyList<CreatedCredentialDto>>> CreateTemporary(
            [FromBody] BulkUserInputDto input, CancellationToken ct)
        {
            var created = await users.CreateTemporaryAsync(input, ct);
            return Created("/api/v1/users/managed", created);
        }

        [HttpPut("{id}")]
        [ProducesResponseType<ManagedUserDto>(StatusCodes.Status200OK)]
        public Task<ManagedUserDto> Update(
            string id, [FromBody] UserUpdateInputDto input, CancellationToken ct) =>
            users.UpdateAsync(id, input, ct);

        /// <summary>Blocking stops sign-in; it does not touch what they may do once in.</summary>
        [HttpPost("{id}/blocked")]
        [ProducesResponseType<ManagedUserDto>(StatusCodes.Status200OK)]
        public Task<ManagedUserDto> SetBlocked(
            string id, [FromBody] BlockedInputDto input, CancellationToken ct) =>
            users.SetBlockedAsync(id, input.Blocked, input.Reason, ct);

        [HttpPost("{id}/approve")]
        [ProducesResponseType<ManagedUserDto>(StatusCodes.Status200OK)]
        public Task<ManagedUserDto> Approve(string id, CancellationToken ct) =>
            users.ApproveAsync(id, ct);

        [HttpPost("{id}/password")]
        [ProducesResponseType<CreatedCredentialDto>(StatusCodes.Status200OK)]
        public Task<CreatedCredentialDto> ResetPassword(string id, CancellationToken ct) =>
            users.ResetPasswordAsync(id, ct);

        [HttpGet("{userId}/sessions")]
        [ProducesResponseType<IReadOnlyList<UserSessionDto>>(StatusCodes.Status200OK)]
        public Task<IReadOnlyList<UserSessionDto>> Sessions(string userId, CancellationToken ct) =>
            users.SessionsAsync(userId, ct);
    }

    [ApiController]
    [Route("questions")]
    [Authorize]
    public class ManagerQuestionsController(IManagerReadService panel) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<PageDto<ManagedQuestionDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<ManagedQuestionDto>> List(
            [FromQuery] int page, [FromQuery] int pageSize, [FromQuery] Guid? activityId,
            [FromQuery] Guid? seriesId, [FromQuery] string? kind, [FromQuery] bool unansweredOnly,
            [FromQuery] string? search, CancellationToken ct) =>
            panel.ListQuestionsAsync(
                new PageQuery { Page = page, PageSize = pageSize },
                activityId, seriesId, kind, unansweredOnly, search, ct);

        /// <summary>Answering leaves it unpublished unless the input says otherwise.</summary>
        [HttpPost("{id:guid}/answer")]
        [ProducesResponseType<ManagedQuestionDto>(StatusCodes.Status200OK)]
        public Task<ManagedQuestionDto> Answer(
            Guid id, [FromBody] AnswerInputDto input, CancellationToken ct) =>
            panel.AnswerAsync(id, input, ct);

        [HttpPost("{id:guid}/published")]
        [ProducesResponseType<ManagedQuestionDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<ManagedQuestionDto> SetPublished(
            Guid id, [FromBody] PublishInputDto input, CancellationToken ct) =>
            panel.SetPublishedAsync(id, input.Published, ct);

        /// <summary>Only an announcement. A participant's question is theirs.</summary>
        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            await panel.DeleteAnnouncementAsync(id, ct);
            return NoContent();
        }
    }

    [ApiController]
    [Route("submissions")]
    [Authorize]
    public class ManagerSubmissionsController(IManagerReadService panel) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<PageDto<ManagedSubmissionDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<ManagedSubmissionDto>> List(
            [FromQuery] int page, [FromQuery] int pageSize, [FromQuery] Guid? activityId,
            [FromQuery] Guid? seriesId, [FromQuery] Guid? seriesProblemId, [FromQuery] string? userId,
            [FromQuery] string? state, [FromQuery] string? verdict, [FromQuery] string? search,
            CancellationToken ct) =>
            panel.ListSubmissionsAsync(
                new PageQuery { Page = page, PageSize = pageSize },
                activityId, seriesId, seriesProblemId, userId, state, verdict, search, ct);

        [HttpGet("{id:guid}")]
        [ProducesResponseType<ManagedSubmissionDetailDto>(StatusCodes.Status200OK)]
        public Task<ManagedSubmissionDetailDto> Get(Guid id, CancellationToken ct) =>
            panel.GetSubmissionAsync(id, ct);

        /// <summary>Adds an evaluation job. The previous attempts stay.</summary>
        [HttpPost("{id:guid}/rejudge")]
        [ProducesResponseType<ManagedSubmissionDto>(StatusCodes.Status200OK)]
        public Task<ManagedSubmissionDto> Rejudge(Guid id, CancellationToken ct) =>
            panel.RejudgeAsync(id, ct);

        /// <summary>Stops a job that has not finished. A finished one is history.</summary>
        [HttpPost("{submissionId:guid}/attempts/{attemptId:guid}/cancel")]
        [ProducesResponseType<ManagedSubmissionDetailDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<ManagedSubmissionDetailDto> Cancel(
            Guid submissionId, Guid attemptId, CancellationToken ct) =>
            panel.CancelAttemptAsync(submissionId, attemptId, ct);
    }

    [ApiController]
    [Route("runners")]
    [Authorize]
    public class ManagerRunnersController(IManagerReadService panel) : ControllerBase
    {
        [HttpGet]
        [ProducesResponseType<PageDto<ManagedRunnerDto>>(StatusCodes.Status200OK)]
        public Task<PageDto<ManagedRunnerDto>> List(
            [FromQuery] int page, [FromQuery] int pageSize,
            [FromQuery] string? state, [FromQuery] string? search, CancellationToken ct) =>
            panel.ListRunnersAsync(new PageQuery { Page = page, PageSize = pageSize }, state, search, ct);

        /// <summary>
        /// Nothing is evaluated until a manager approves the fingerprint.
        /// Answers the whole row, as its two siblings below do.
        /// </summary>
        [HttpPost("{id:guid}/approve")]
        [ProducesResponseType<ManagedRunnerDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<ManagedRunnerDto> Approve(Guid id, CancellationToken ct) =>
            panel.ApproveRunnerAsync(id, ct);

        /// <summary>
        /// Permanent: there is no rotation, so a revoked key never comes back and
        /// that Runner returns as a new identity. Whatever it was holding goes
        /// back into the queue.
        /// </summary>
        [HttpPost("{id:guid}/revoke")]
        [ProducesResponseType<ManagedRunnerDto>(StatusCodes.Status200OK)]
        public Task<ManagedRunnerDto> Revoke(
            Guid id, [FromBody] RevokeRunnerInputDto input, CancellationToken ct) =>
            panel.RevokeRunnerAsync(id, input.Reason, ct);

        [HttpPost("{id:guid}/tags")]
        [ProducesResponseType<ManagedRunnerDto>(StatusCodes.Status200OK)]
        public Task<ManagedRunnerDto> SetTags(
            Guid id, [FromBody] RunnerTagsInputDto input, CancellationToken ct) =>
            panel.SetTagsAsync(id, input.Tags, ct);

        [HttpDelete("{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> Forget(Guid id, CancellationToken ct)
        {
            await panel.ForgetRunnerAsync(id, ct);
            return NoContent();
        }

        // **A Runner's attachments are read through `GET /files/{id}`**, like
        // every other stored file. There was a second endpoint here until
        // 2026-08-12, and it asked exactly the same question this one would —
        // `runner:read`, installation-wide — while being a second way to the
        // bytes, which §2 invariant 1 of FILE_STORAGE.md forbids in so many
        // words. What it added was a check that the file belonged to the runner
        // in the path, which is a tidiness rather than a boundary: anybody
        // holding `runner:read` reads every Runner's attachments either way.
        //
        // Its own listing already carries what a reader needs: `attachments`
        // gives the file id, name, size and checksum of each one.
        //
        // `GET /runner/files/{id}` is **not** the same thing and stays: that is
        // the Runner's own door, authorized against the job or trial it holds
        // right now, for a caller that has a token and no session.
    }

    /// <summary>Configuring the installation: its name, its mark, its documents.</summary>
    [ApiController]
    [Route("instance")]
    [Authorize]
    public class InstanceAdminController(
        Database.ApplicationDbContext context,
        IInstanceService instances,
        IDocumentService documents,
        IPermissionService permissions,
        Realtime.IEventHub events
    ) : ControllerBase
    {
        [HttpPut]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        public async Task<InstanceInfoDto> Update(
            [FromBody] InstanceSettingsInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);

            var instance = await instances.EnsureAsync(ct);
            instance.Name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim();
            instance.LocalRegistrationEnabled = input.LocalRegistrationEnabled;
            instance.RequireEmail = input.RequireEmail;
            instance.RequireConfirmedEmail = input.RequireConfirmedEmail;
            instance.ShowLogo = input.ShowLogo;
            instance.ShowLocalSignIn = input.ShowLocalSignIn;
            instance.AccountDeletionEnabled = input.AccountDeletionEnabled;
            await context.SaveChangesAsync(ct);

            return await AnnounceAsync(ct);
        }

        [HttpPut("logo")]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        public async Task<InstanceInfoDto> SetLogo(
            [FromBody] InstanceLogoInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);

            if (input.FileId is null)
            {
                // Absent removes the mark, and the instance falls back to the
                // placeholder the Client ships — visibly a placeholder.
                await documents.UnpublishAsync(FileOwnerKind.InstanceLogo, instance.Id, LogoName(input.Language), ct);
            }
            else
            {
                await documents.PublishAsync(
                    FileOwnerKind.InstanceLogo, instance.Id, LogoName(input.Language),
                    new PublishDocumentInputDto
                    {
                        Statements = [new NewStatementDto { FileId = input.FileId, Language = input.Language }],
                    }, ct);
            }

            return await AnnounceAsync(ct);
        }

        /// <summary>
        /// The logo is one reference per language, named so the default and a
        /// translation cannot collide.
        /// </summary>
        private static string LogoName(string? language) =>
            string.IsNullOrWhiteSpace(language) ? "logo" : $"logo-{language}";

        /// <summary>
        /// Publishing <b>adds</b> a revision with a date rather than replacing
        /// the last, so "which policy was in force on the third of August" stays
        /// answerable.
        /// </summary>
        [HttpPost("documents/{kind}")]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        public async Task<InstanceInfoDto> PublishDocument(
            string kind, [FromBody] PublishDocumentInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);
            await documents.PublishAsync(FileOwnerKind.InstanceDocument, instance.Id, kind, input, ct);
            return await AnnounceAsync(ct);
        }

        /// <summary>Withdrawing removes the references. The revisions stay readable.</summary>
        [HttpDelete("documents/{kind}")]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        public async Task<InstanceInfoDto> UnpublishDocument(string kind, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);
            await documents.UnpublishAsync(FileOwnerKind.InstanceDocument, instance.Id, kind, ct);
            return await AnnounceAsync(ct);
        }

        [HttpGet("documents/{kind}")]
        [ProducesResponseType<IReadOnlyList<InstanceDocumentRefDto>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<InstanceDocumentRefDto>> History(string kind, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);
            var history = await documents.HistoryAsync(FileOwnerKind.InstanceDocument, instance.Id, kind, ct);

            return history.Select(r => new InstanceDocumentRefDto
            {
                Kind = r.Name,
                Language = r.Language,
                ValidFrom = Wire.At(r.ValidFrom),
                IsTemplate = false,
                FileId = Wire.Id(r.FileId),
                Sha256 = r.File?.Sha256 ?? "",
                SizeBytes = r.File?.SizeBytes ?? 0,
            }).ToList();
        }

        /// <summary>
        /// The instance is what every reader holds, so a change is sent whole
        /// rather than as a patch nobody could apply consistently.
        /// </summary>
        private async Task<InstanceInfoDto> AnnounceAsync(CancellationToken ct)
        {
            var info = await instances.GetAsync(ct);
            var everybody = await context.Users
                .AsNoTracking()
                .Where(u => !u.Anonymized)
                .Select(u => u.Id)
                .ToListAsync(ct);

            await events.SendToUsersAsync(everybody, EventTypes.InstanceChanged, new { instance = info }, ct);
            return info;
        }
    }
}

using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using DbFile = AlgoJudge.Server.Database.Models.File;
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
    public class UsersController(IUserService users, IAccountMergeService merges) : ControllerBase
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

        /// <summary>
        /// Blocking stops sign-in <b>and every request after it</b>.
        /// <para>
        /// This said it did not touch what somebody already signed in may do,
        /// and that was true: <c>LockoutEnd</c> is checked at sign-in, so a
        /// blocked person carried on until Identity next revalidated their
        /// cookie — half an hour by default. <c>Authorization/BlockedGate</c>
        /// closed that.
        /// </para>
        /// </summary>
        [HttpPost("{id}/blocked")]
        [ProducesResponseType<ManagedUserDto>(StatusCodes.Status200OK)]
        public Task<ManagedUserDto> SetBlocked(
            string id, [FromBody] BlockedInputDto input, CancellationToken ct) =>
            users.SetBlockedAsync(id, input.Blocked, input.Reason, ct);

        /// <summary>
        /// What carrying this account's work onto another would move, and what
        /// would stop it. Changes nothing.
        /// </summary>
        [HttpPost("{id}/merge-preview")]
        [ProducesResponseType<MergePreviewDto>(StatusCodes.Status200OK)]
        public Task<MergePreviewDto> MergePreview(
            string id, [FromBody] MergeInputDto input, CancellationToken ct) =>
            merges.PreviewAsync(id, input.TargetUserId, ct);

        /// <summary>
        /// Carries this account's work onto another and blocks this one. The
        /// emptied account is removed a day later, and until then an undo puts
        /// everything back where it was.
        /// </summary>
        [HttpPost("{id}/merge")]
        [ProducesResponseType<AccountMergeDto>(StatusCodes.Status200OK)]
        public Task<AccountMergeDto> Merge(
            string id, [FromBody] MergeInputDto input, CancellationToken ct) =>
            merges.MergeAsync(id, input.TargetUserId, ct);

        /// <summary>
        /// Gives the work back and unblocks the account it came from. Offered
        /// only while that account is still whole, which is the day after the
        /// merge.
        /// </summary>
        [HttpPost("merges/{mergeId:guid}/undo")]
        [ProducesResponseType<AccountMergeDto>(StatusCodes.Status200OK)]
        public Task<AccountMergeDto> UndoMerge(Guid mergeId, CancellationToken ct) =>
            merges.UndoAsync(mergeId, ct);

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

        /// <summary>
        /// Rules that this counts towards no standing, or lifts the ruling.
        /// Neither a rejudge nor a cancellation — those are about evaluating,
        /// this about what the evaluation counts for. The submission keeps
        /// everything it had, its place against the ceiling included.
        /// </summary>
        [HttpPost("{id:guid}/excluded")]
        [ProducesResponseType<ManagedSubmissionDetailDto>(StatusCodes.Status200OK)]
        public Task<ManagedSubmissionDetailDto> Excluded(
            Guid id, [FromBody] ExcludedInputDto input, CancellationToken ct) =>
            panel.SetExcludedAsync(id, input.Excluded, input.Reason, ct);
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
        IFileService files,
        IPermissionService permissions,
        IAccessKeyMinting minting,
        Realtime.IEventHub events
    ) : ControllerBase
    {
        /// <summary>
        /// The hosts this installation may fetch a document from.
        /// <para>
        /// <b>Its own pair of endpoints, and manager-only.</b> Every other
        /// instance setting travels on <c>getInstanceInfo</c>, which anybody may
        /// read — and that is right for the boolean, because whether work leaves
        /// the building is the fact a privacy notice is written from. The list of
        /// destinations is not the same thing: it enumerates what an installation
        /// integrates with, which is operational detail rather than a disclosure
        /// somebody is owed.
        /// </para>
        /// </summary>
        /// <summary>Which named secrets this installation holds. Names only.</summary>
        [HttpGet("access-keys")]
        [ProducesResponseType<IReadOnlyList<AccessKeyDto>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<AccessKeyDto>> AccessKeys(CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);

            return await context.AccessKeys
                .AsNoTracking()
                .OrderBy(k => k.Name)
                .Select(k => new AccessKeyDto { Name = k.Name, UpdatedAt = Wire.At(k.UpdatedAt)! })
                .ToListAsync(ct);
        }

        /// <summary>
        /// Sets one, or removes it when the value is empty.
        /// <para>
        /// <b>Write-only through this endpoint</b>, like every other secret here:
        /// the answer says a key is set and when, never what it is. That it can
        /// be read at all is the separate, deliberate exception below.
        /// </para>
        /// </summary>
        [HttpPut("access-keys/{name}")]
        [ProducesResponseType<IReadOnlyList<AccessKeyDto>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<AccessKeyDto>> SetAccessKey(
            string name, [FromBody] AccessKeyInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);

            var key = Named(name);
            var existing = await context.AccessKeys.FirstOrDefaultAsync(k => k.Name == key, ct);
            var value = input.Value.Trim();

            if (value.Length == 0)
            {
                if (existing is not null) context.AccessKeys.Remove(existing);
            }
            else if (existing is null)
            {
                context.AccessKeys.Add(new Database.Models.AccessKey { Name = key, Value = value });
            }
            else
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.UtcNow;
            }

            await context.SaveChangesAsync(ct);
            return await AccessKeys(ct);
        }

        /// <summary>
        /// Hands a caller the credential it needs for a named service.
        /// <para>
        /// <b>What comes back is not always what is stored.</b> Where the service
        /// mints short-lived credentials, this exchanges the stored key for one
        /// and answers with that, carrying the instant it dies. The stored key
        /// stays in this process. Where it does not, the stored value comes back
        /// as it always has.
        /// </para>
        /// <para>
        /// <b>Today there is one key and one gate.</b> `uvaexplorer` is handed to
        /// whoever may import problems, because that is what it is for. A second
        /// key must bring its own gate rather than inherit this one — a model
        /// provider's credential given out because somebody may import problems
        /// is a mistake nobody would notice until the bill arrived.
        /// </para>
        /// </summary>
        [HttpGet("access-keys/{name}/value")]
        [ProducesResponseType<AccessKeyValueDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<AccessKeyValueDto> RequestAccessKey(string name, CancellationToken ct)
        {
            var key = Named(name);

            await permissions.RequireAsync(key switch
            {
                "uvaexplorer" => Permissions.ProblemImportExternal,
                // Unknown names are nobody's to read. Falling back to a manager
                // permission would make every future key readable the day it is
                // added and before anyone had decided who may read it.
                _ => Permissions.SystemAdministrator,
            }, null, ct);

            var stored = await context.AccessKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.Name == key, ct)
                ?? throw new NotFoundException($"The key {key}");

            if (!minting.Mints(key))
            {
                return new AccessKeyValueDto { Name = stored.Name, Value = stored.Value };
            }

            var credential = await minting.MintAsync(key, stored.Value, ct);
            return new AccessKeyValueDto
            {
                Name = stored.Name,
                Value = credential.Value,
                ExpiresAt = Wire.At(credential.ExpiresAt),
            };
        }

        /// <summary>One spelling, so a name cannot be set twice in two cases.</summary>
        private static string Named(string name) => name.Trim().ToLowerInvariant();

        [HttpGet("external-content")]
        [ProducesResponseType<ExternalContentDto>(StatusCodes.Status200OK)]
        public async Task<ExternalContentDto> ExternalContent(CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);

            return new ExternalContentDto
            {
                Enabled = instance.ExternalJudgingEnabled,
                Hosts = instance.ExternalFetchHosts,
            };
        }

        /// <summary>
        /// Replaces the list, whole.
        /// <para>
        /// A replace rather than an add-and-remove pair: the list is short, an
        /// operator edits it as a list, and two endpoints would need an answer
        /// for what happens when the same host arrives twice.
        /// </para>
        /// </summary>
        [HttpPut("external-content")]
        [ProducesResponseType<ExternalContentDto>(StatusCodes.Status200OK)]
        public async Task<ExternalContentDto> SetExternalContent(
            [FromBody] ExternalContentInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);

            // Tidied only in the ways that cannot change which host is meant:
            // blank entries dropped, whitespace trimmed, duplicates collapsed.
            // Case is left as typed — the comparison is already insensitive, and
            // a list that rewrites what an operator entered is one they cannot
            // predict.
            instance.ExternalFetchHosts = input.Hosts
                .Select(h => h.Trim())
                .Where(h => h.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await Concurrency.SaveAsync(context, ct);

            return new ExternalContentDto
            {
                Enabled = instance.ExternalJudgingEnabled,
                Hosts = instance.ExternalFetchHosts,
            };
        }

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
            // Absent means "leave it alone", never "turn it off": a caller that
            // predates this field is saving something else, and reading its
            // silence as a decision would close the door under somebody.
            if (input.ExternalJudgingEnabled is { } externalJudging)
            {
                instance.ExternalJudgingEnabled = externalJudging;
            }
            if (input.SeriesRestrictionsEnabled is { } restrictions)
            {
                instance.SeriesRestrictionsEnabled = restrictions;
            }

            // **This endpoint replaces the whole object**, and since 2026-08-28
            // the panel is not the only writer — `aj-admin config apply` writes
            // the same row from a file. There is no guard to re-run here, so a
            // lost race is answered as one: nothing was written, read it again.
            // Silently putting every field back was the alternative.
            await Concurrency.SaveAsync(context, ct);

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
                await documents.UnpublishAsync(FileOwnerKind.InstanceLogo, instance.Id, DocumentService.LogoName(input.Language), ct);
            }
            else
            {
                await documents.PublishAsync(
                    FileOwnerKind.InstanceLogo, instance.Id, DocumentService.LogoName(input.Language),
                    new PublishDocumentInputDto
                    {
                        Statements = [new NewStatementDto { FileId = input.FileId, Language = input.Language }],
                    }, ct);
            }

            return await AnnounceAsync(ct);
        }

        /// <summary>
        /// Sets the operator's colours and typeface, by either of its two doors.
        /// <para>
        /// <b>One thing is in force and one thing can be downloaded.</b> The
        /// panel's form sends values and this Server writes the canonical YAML;
        /// an operator with a file of their own sends its id and the bytes are
        /// published as they arrived — untouched, so the checksum a
        /// pre-configuration directory compares against still matches.
        /// </para>
        /// </summary>
        [HttpPut("theme")]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<InstanceInfoDto> SetTheme(
            [FromBody] InstanceThemeInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);

            if ((input.FileId is null) == (input.Theme is null))
            {
                throw new ValidationException(
                    "A theme is set either from a file or from values, and this request states "
                    + (input.FileId is null ? "neither" : "both") + ".",
                    "theme.input");
            }

            var faces = await FontNamesAsync(instance.Id, ct);
            string fileId;

            if (input.FileId is { } uploaded)
            {
                var file = await Stored(uploaded, ct);
                if (file.SizeBytes > ThemeDocument.MaxBytes)
                {
                    throw new ValidationException(
                        $"A theme is a few hundred bytes of colours; this file is "
                        + $"{file.SizeBytes} and the ceiling is {ThemeDocument.MaxBytes}.",
                        "theme.size");
                }

                // Parsed to refuse it, not to rewrite it. What is published is
                // what the operator wrote.
                ThemeDocument.Parse(await TextAsync(file, ct), faces);
                fileId = uploaded;
            }
            else
            {
                // Written, then read back through exactly the same validation the
                // other door goes through, then written again from what came out.
                // The round trip is what makes the stored document canonical —
                // and what stops the form having a validation path of its own.
                var stated = ThemeDocument.Serialise(Root(input.Theme!));
                var canonical = ThemeDocument.Serialise(ThemeDocument.Parse(stated, faces));

                var bytes = System.Text.Encoding.UTF8.GetBytes(canonical);
                var stored = await files.StoreAsync(
                    new MemoryStream(bytes), "theme.yml", "application/yaml",
                    IFileService.Checksum(bytes), ct);
                fileId = Wire.Id(stored.Id);
            }

            await documents.PublishAsync(
                FileOwnerKind.InstanceTheme, instance.Id, ThemeDocument.ReferenceName,
                new PublishDocumentInputDto
                {
                    Statements = [new NewStatementDto { FileId = fileId }],
                }, ct);

            return await AnnounceAsync(ct);
        }

        /// <summary>Withdrawing puts the installation back on the theme the Client ships.</summary>
        [HttpDelete("theme")]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        public async Task<InstanceInfoDto> UnsetTheme(CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);
            await documents.UnpublishAsync(
                FileOwnerKind.InstanceTheme, instance.Id, ThemeDocument.ReferenceName, ct);
            return await AnnounceAsync(ct);
        }

        /// <summary>
        /// The faces this installation has stored, whether or not a theme uses
        /// them. <c>/instance</c> lists only the ones in force, so the form needs
        /// this to offer a face that has been uploaded and not yet declared.
        /// </summary>
        [HttpGet("fonts")]
        [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
        public async Task<IReadOnlyList<string>> Fonts(CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);
            return (await FontNamesAsync(instance.Id, ct)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Publishes one face under the name a theme calls it by.
        /// <para>
        /// <b>woff2, checked on the bytes.</b> The declared type is whatever the
        /// uploader said it was, and this file is fetched by every visitor's
        /// browser.
        /// </para>
        /// </summary>
        [HttpPost("fonts")]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<InstanceInfoDto> AddFont(
            [FromBody] InstanceFontInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);

            var name = ThemeDocument.FontFileNameOrRefuse(input.Name);
            var file = await Stored(input.FileId, ct);

            if (file.SizeBytes > MaxFontBytes)
            {
                throw new ValidationException(
                    $"'{name}' is {file.SizeBytes} bytes and the ceiling is {MaxFontBytes}. A face "
                    + "subset to the alphabets a screen needs is a small fraction of that; a file "
                    + "this size is usually a whole family or an unsubsetted one.",
                    "font.size");
            }

            await using (var content = await files.OpenAsync(file, ct))
            {
                var header = new byte[4];
                var read = await content.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, ct);
                if (read < 4 || !ThemeDocument.IsWoff2(header))
                {
                    throw new ValidationException(
                        $"'{name}' does not begin wOF2, so it is not a WOFF2 face whatever it is "
                        + "called. Every visitor's browser fetches this file.",
                        "font.format");
                }
            }

            await documents.PublishAsync(
                FileOwnerKind.InstanceFont, instance.Id, name,
                new PublishDocumentInputDto
                {
                    Statements = [new NewStatementDto { FileId = input.FileId }],
                }, ct);

            return await AnnounceAsync(ct);
        }

        /// <summary>
        /// Withdraws a face — unless the published theme draws with it.
        /// <para>
        /// <b>Refused rather than allowed to break the theme.</b> A theme naming a
        /// face that is not stored cannot be read, and a theme that cannot be read
        /// is no theme at all: withdrawing this quietly would turn one deletion
        /// into every colour on the installation reverting.
        /// </para>
        /// </summary>
        [HttpDelete("fonts/{name}")]
        [ProducesResponseType<InstanceInfoDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<InstanceInfoDto> RemoveFont(string name, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.InstanceUpdate, null, ct);
            var instance = await instances.EnsureAsync(ct);

            var stored = ThemeDocument.FontFileNameOrRefuse(name);
            var info = await instances.GetAsync(ct);

            if (info.Theme?.Fonts.Any(face => face.Name == stored) == true)
            {
                throw new ValidationException(
                    $"The published theme draws with '{stored}'. Publish a theme that does not, "
                    + "and then withdraw the face.",
                    "font.inUse");
            }

            await documents.UnpublishAsync(FileOwnerKind.InstanceFont, instance.Id, stored, ct);
            return await AnnounceAsync(ct);
        }

        /// <summary>
        /// A face subset to the alphabets a screen needs is tens of kilobytes.
        /// The ceiling is generous enough for one that is not subset at all and
        /// mean enough to refuse a whole family in one file.
        /// </summary>
        private const long MaxFontBytes = 512 * 1024;

        private async Task<HashSet<string>> FontNamesAsync(Guid instanceId, CancellationToken ct) =>
            (await context.FileReferences
                .AsNoTracking()
                .Where(r => r.OwnerKind == FileOwnerKind.InstanceFont
                    && r.InstanceId == instanceId
                    && r.SupersededAt == null)
                .Select(r => r.Name)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        private async Task<DbFile> Stored(string fileId, CancellationToken ct) =>
            Guid.TryParse(fileId, out var id) && await files.FindAsync(id, ct) is { } file
                ? file
                : throw new ValidationException("That file is not stored", "file.missing");

        private async Task<string> TextAsync(DbFile file, CancellationToken ct)
        {
            await using var content = await files.OpenAsync(file, ct);
            using var reader = new StreamReader(content);
            return await reader.ReadToEndAsync(ct);
        }

        /// <summary>The form's values, as the document they are about to be written as.</summary>
        private static ThemeRoot Root(ThemeColoursInputDto input) => new()
        {
            Format = ThemeDocument.Format,
            Version = ThemeDocument.Version,
            Light = Scheme(input.Light),
            Dark = Scheme(input.Dark),
            FontFamily = input.FontFamily,
            FontFamilyHeadings = input.FontFamilyHeadings,
            Fonts = input.Fonts?.Select(face => new ThemeFontFace
            {
                Family = face.Family,
                File = face.File,
                Weight = face.Weight,
                Style = face.Style,
            }).ToList(),
        };

        private static ThemeColours? Scheme(ThemeColoursDto? colours) => colours is null ? null : new()
        {
            Primary = colours.Primary,
            Secondary = colours.Secondary,
            Accent = colours.Accent,
            Link = colours.Link,
            Body = colours.Body,
            Surface = colours.Surface,
            Text = colours.Text,
            Dimmed = colours.Dimmed,
            Border = colours.Border,
            NavBackground = colours.NavBackground,
            NavText = colours.NavText,
            NavActiveBackground = colours.NavActiveBackground,
            NavActiveText = colours.NavActiveText,
            HeaderBackground = colours.HeaderBackground,
            HeaderText = colours.HeaderText,
        };

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

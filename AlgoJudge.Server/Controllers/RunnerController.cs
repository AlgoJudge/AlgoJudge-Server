using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// The Runner surface.
    /// <para>
    /// <b>[AllowAnonymous] on the class, not carelessness.</b> A Runner is not a
    /// user and holds no cookie: it authenticates with its own token, checked by
    /// <see cref="IRunnerService.AuthenticateAsync"/> on every call that needs
    /// it. Leaving <c>[Authorize]</c> here would demand a session a Runner cannot
    /// have.
    /// </para>
    /// <para>
    /// The Runner is authorized against <b>the job it holds</b>, not against
    /// being a Runner. Without that, any approved Runner could fetch every test
    /// package in the installation.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("runner")]
    [AllowAnonymous]
    public class RunnerController(
        IRunnerService runners,
        IFileService files,
        ITrialService trials
    ) : ControllerBase
    {
        /// <summary>
        /// Presents a public key. Registration is not approval: an administrator
        /// approves the fingerprint, and nothing is evaluated before that.
        /// </summary>
        [HttpPost("register")]
        [ProducesResponseType<RunnerRegisteredDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public Task<RunnerRegisteredDto> Register(
            [FromBody] RunnerRegisterDto input, CancellationToken ct) =>
            runners.RegisterAsync(input, Address(), ct);

        /// <summary>Step one: something to sign. Single-use, and it expires.</summary>
        [HttpPost("auth/challenge")]
        [ProducesResponseType<RunnerChallengeDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public Task<RunnerChallengeDto> Challenge(
            [FromBody] RunnerChallengeRequestDto input, CancellationToken ct) =>
            runners.ChallengeAsync(input.Fingerprint, ct);

        /// <summary>Step two: prove the private key, receive a token with a lifetime.</summary>
        [HttpPost("auth/token")]
        [ProducesResponseType<RunnerTokenDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public Task<RunnerTokenDto> Token(
            [FromBody] RunnerTokenRequestDto input, CancellationToken ct) =>
            runners.TokenAsync(input, Address(), ct);

        /// <summary>
        /// Takes one queued job, or answers 204 when nothing matched — which is a
        /// normal state and not an error.
        /// </summary>
        [HttpPost("jobs/claim")]
        [ProducesResponseType<ClaimedJobDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<ClaimedJobDto>> Claim(
            [FromBody] ClaimRequestDto? input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            var job = await runners.ClaimAsync(runner, input?.LeaseSeconds, ct);
            return job is null ? NoContent() : Ok(job);
        }

        /// <summary>
        /// Takes one queued **trial**, or answers 204.
        /// <para>
        /// A separate endpoint rather than a flag on `jobs/claim`, for the same
        /// reason a trial has its own table (D-9): a Runner that has not been
        /// taught about trials keeps working, and a queue of trials can never
        /// delay the queue that decides somebody's mark.
        /// </para>
        /// </summary>
        [HttpPost("trials/claim")]
        [ProducesResponseType<ClaimedTrialDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<ClaimedTrialDto>> ClaimTrial(
            [FromBody] ClaimRequestDto? input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            var trial = await trials.ClaimAsync(runner, input?.LeaseSeconds, ct);
            return trial is null ? NoContent() : Ok(trial);
        }

        /// <summary>
        /// Records what was measured, once — **and the package is deleted here**
        /// (D-12), successfully or not.
        /// <para>
        /// Idempotent on the same terms as a job report: a Runner that resends
        /// gets `duplicate: true` rather than a second record. A trial that
        /// failed carries a reason and no measurement; the two never both.
        /// </para>
        /// </summary>
        [HttpPost("trials/{trialId:guid}/report")]
        [ProducesResponseType<TrialReportAcceptedDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<TrialReportAcceptedDto> ReportTrial(
            Guid trialId, [FromBody] TrialReportInputDto input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            return await trials.ReportAsync(runner, trialId, input, ct);
        }

        /// <summary>Holds a trial's lease open while it is still running.</summary>
        [HttpPost("trials/{trialId:guid}/lease")]
        [ProducesResponseType<TrialLeaseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public async Task<TrialLeaseDto> RenewTrial(
            Guid trialId, [FromBody] LeaseRequestDto input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            return await trials.RenewAsync(runner, trialId, input.LeaseToken, input.LeaseSeconds, ct);
        }

        /// <summary>
        /// Records a verdict, once.
        /// <para>
        /// Idempotent on the lease token: a Runner that resends because it did
        /// not see the acknowledgement gets the same result back with
        /// <c>duplicate: true</c>, rather than a second one.
        /// </para>
        /// </summary>
        [HttpPost("jobs/{jobId:guid}/report")]
        [ProducesResponseType<ReportAcceptedDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<ReportAcceptedDto> Report(
            Guid jobId, [FromBody] ReportResultDto report, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            return await runners.ReportAsync(runner, jobId, report, ct);
        }

        /// <summary>Liveness, and the address the Server records for this Runner.</summary>
        [HttpPost("heartbeat")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Heartbeat(CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            await runners.HeartbeatAsync(runner, Address(), ct);
            return NoContent();
        }

        /// <summary>
        /// Extends a lease. A long evaluation renews rather than being cut off —
        /// the deadline exists so a job survives a Runner that died, not so it
        /// interrupts one that is working.
        /// </summary>
        [HttpPost("jobs/{jobId:guid}/lease")]
        [ProducesResponseType<LeaseDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public async Task<LeaseDto> Renew(
            Guid jobId, [FromBody] LeaseRequestDto input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            return await runners.RenewAsync(runner, jobId, input.LeaseToken, input.LeaseSeconds, ct);
        }

        /// <summary>Still working. Renews the lease, which is all the Server can do with the news.</summary>
        [HttpPost("jobs/{jobId:guid}/progress")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> Progress(
            Guid jobId, [FromBody] LeaseRequestDto input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            await runners.ProgressAsync(runner, jobId, input.LeaseToken, ct);
            return NoContent();
        }

        /// <summary>
        /// Reads a file one of this Runner's held jobs needs — the package, the
        /// submitted source.
        /// <para>
        /// Its own endpoint rather than <c>/files/{id}</c>, because a Runner
        /// carries a token and not a session, and because the answer here is
        /// "does a job you hold reference this" — a different question from the
        /// one the file endpoint asks.
        /// </para>
        /// </summary>
        [HttpGet("files/{id:guid}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            // Either question may say yes: a job this Runner holds references
            // these bytes, or a trial it holds does. Both are "something you are
            // working on right now", and neither lets a Runner probe ids.
            if (!await runners.MayReadAsync(runner, id, ct)
                && !await trials.MayReadAsync(runner, id, ct))
            {
                throw new NotFoundException("File");
            }

            var file = await files.FindAsync(id, ct) ?? throw new NotFoundException("File");

            Response.Headers.CacheControl = "private, max-age=31536000, immutable";

            // Streamed rather than handed over as an array: this is the endpoint
            // several Runners pull a 128 MiB package through at the same time.
            var content = await files.OpenAsync(file, ct);

            // A Runner does not send `If-None-Match` today and does not have to:
            // both of these are additive, and a request without either header is
            // answered exactly as before. What they buy is a package download
            // that can be resumed instead of restarted.
            // Every argument named: the byte[] and Stream overloads differ in
            // what their third positional parameter means, and picking the wrong
            // one is a compile error only by luck.
            return File(
                fileStream: content,
                contentType: file.MimeType,
                fileDownloadName: file.Name,
                lastModified: null,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.Sha256}\""),
                enableRangeProcessing: true);
        }

        /// <summary>
        /// Stores bytes a Runner produced. The checksum is recomputed here as it
        /// is for anybody else — a Runner's own output gets the same integrity
        /// chain as everything else in the product.
        /// </summary>
        [HttpPost("files")]
        [Consumes("multipart/form-data")]
        [Api.MultipartForm(File = "file", FileRequired = true, Fields = ["sha256"], RequiredFields = ["sha256"])]
        [RequestSizeLimit(UploadLimits.Package)]
        [DisableFormValueModelBinding]
        [ProducesResponseType<UploadedFileDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<UploadedFileDto>> Upload(CancellationToken ct)
        {
            // Before a byte is read: a Runner that cannot prove who it is does
            // not get to write into the store.
            await runners.AuthenticateAsync(Token(), ct);

            var upload = await MultipartUpload.ReadAsync(
                Request, UploadLimits.Package,
                (content, _, _, token) => files.StageAsync(content, token), ct);

            if (upload.File is not { SizeBytes: > 0 } staged)
            {
                if (upload.File is { } empty) await files.DiscardAsync(empty, ct);
                throw new ValidationException("A file is required", "file.required");
            }

            var stored = await files.CommitAsync(
                staged, upload.FileName ?? "", upload.ContentType ?? "", upload.Field("sha256"), ct);
            return Created($"/api/v1/runner/files/{Api.Contracts.Wire.Id(stored.Id)}", Api.Projections.Uploaded(stored));
        }

        /// <summary>Names an uploaded file on the attempt this Runner holds.</summary>
        [HttpPost("jobs/{jobId:guid}/files")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status409Conflict)]
        public async Task<IActionResult> AttachToJob(
            Guid jobId, [FromBody] AttachToJobDto input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            if (!Guid.TryParse(input.FileId, out var fileId))
            {
                throw new ValidationException("A file id is required", "file.required");
            }
            await runners.AttachToJobAsync(runner, jobId, input.LeaseToken, fileId, input.Name, ct);
            return NoContent();
        }

        /// <summary>
        /// Names an uploaded file on the Runner itself — its log, its `lscpu`.
        /// Replaces the name rather than adding another.
        /// </summary>
        [HttpPost("files/attach")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<IActionResult> AttachToSelf(
            [FromBody] AttachToSelfDto input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            if (!Guid.TryParse(input.FileId, out var fileId))
            {
                throw new ValidationException("A file id is required", "file.required");
            }
            await runners.AttachToSelfAsync(runner, fileId, input.Name, ct);
            return NoContent();
        }

        /// <summary>The bearer token, from the header a Runner sets.</summary>
        private string? Token()
        {
            var header = Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? header[prefix.Length..].Trim()
                : null;
        }

        /// <summary>
        /// Where the Server saw the connection come from.
        /// <para>
        /// Read here rather than reported, because a machine is a bad witness to
        /// how it is reached. Correct only with <c>ForwardedHeaders</c>
        /// configured — without it every Runner behind a proxy records the proxy.
        /// </para>
        /// </summary>
        private string? Address() => HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    // Approving a Runner lives with revoking and tagging in
    // `PanelController.ManagerRunnersController`, not here. It is a manager
    // surface carrying the ordinary session, and it belongs beside its siblings
    // — kept apart, it drifted onto the registration acknowledgement's shape and
    // answered a manager with `{runnerId, fingerprint, state}` where the two
    // endpoints next to it answered the whole row.
}

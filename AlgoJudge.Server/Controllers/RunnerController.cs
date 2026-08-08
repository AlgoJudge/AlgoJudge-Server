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
    public class RunnerController(IRunnerService runners, IFileService files) : ControllerBase
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
            if (!await runners.MayReadAsync(runner, id, ct)) throw new NotFoundException("File");

            var file = await files.FindAsync(id, ct) ?? throw new NotFoundException("File");

            Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            Response.Headers.ETag = $"\"{file.Sha256}\"";
            return File(file.Content, file.MimeType, file.Name);
        }

        /// <summary>
        /// Stores bytes a Runner produced. The checksum is recomputed here as it
        /// is for anybody else — a Runner's own output gets the same integrity
        /// chain as everything else in the product.
        /// </summary>
        [HttpPost("files")]
        [ProducesResponseType<UploadedFileDto>(StatusCodes.Status201Created)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<UploadedFileDto>> Upload(
            [FromForm] IFormFile file, [FromForm] string sha256, CancellationToken ct)
        {
            await runners.AuthenticateAsync(Token(), ct);
            if (file is null || file.Length == 0)
            {
                throw new ValidationException("A file is required", "file.required");
            }

            await using var content = file.OpenReadStream();
            var stored = await files.StoreAsync(content, file.FileName, file.ContentType, sha256, ct);
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

    /// <summary>
    /// Approving and revoking a Runner. A manager surface, not a Runner one, so
    /// it carries the ordinary session authentication.
    /// </summary>
    [ApiController]
    [Route("runners")]
    [Authorize]
    public class RunnersAdminController(
        Database.ApplicationDbContext context,
        IPermissionService permissions,
        ICurrentUserService currentUser,
        TimeProvider clock
    ) : ControllerBase
    {
        [HttpPost("{id:guid}/approve")]
        [ProducesResponseType<RunnerRegisteredDto>(StatusCodes.Status200OK)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public async Task<RunnerRegisteredDto> Approve(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Authorization.Permissions.RunnerApprove, null, ct);
            var runner = await context.Runners.FindAsync([id], ct) ?? throw new NotFoundException("Runner");

            if (runner.State == Database.Models.RunnerState.Revoked)
            {
                throw new ConflictException(
                    "A revoked Runner cannot be approved; it must register again", "runner.revoked");
            }

            runner.State = Database.Models.RunnerState.Approved;
            runner.ApprovedAt = clock.GetUtcNow().UtcDateTime;
            runner.ApprovedByUserId = currentUser.UserId;
            await context.SaveChangesAsync(ct);

            return new RunnerRegisteredDto
            {
                RunnerId = Api.Contracts.Wire.Id(runner.Id),
                Fingerprint = runner.Fingerprint,
                State = Api.Projections.Wire(runner.State),
            };
        }
    }
}

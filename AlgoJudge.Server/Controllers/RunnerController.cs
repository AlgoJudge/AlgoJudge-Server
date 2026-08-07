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
    public class RunnerController(IRunnerService runners) : ControllerBase
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

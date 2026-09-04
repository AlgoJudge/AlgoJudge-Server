using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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
        ITrialService trials,
        IQueueSignal queue,
        IHostApplicationLifetime lifetime,
        IServiceScopeFactory scopes
    ) : ControllerBase
    {
        /// <summary>
        /// The longest a <c>claim</c> may be held open.
        /// <para>
        /// Five minutes, which is what an installation that owns its whole
        /// network path can use. It is a ceiling and not a recommendation: the
        /// Runner ships asking for twenty-five seconds, because the thing that
        /// cuts a silent request is an intermediary nobody here controls.
        /// </para>
        /// </summary>
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How much of the wait to spread the timeouts over.
        /// <para>
        /// Without it every Runner that started together answers its own 204 in
        /// the same instant and they all ask again together, for ever — the same
        /// reason the Runner's own backoff carries jitter. A sixteenth is
        /// Consul's figure for the same problem.
        /// </para>
        /// </summary>
        private const int JitterDivisor = 16;

        /// <summary>
        /// How far to spread the looks a single nudge provokes.
        /// <para>
        /// **A nudge is a broadcast.** One submission completes the task every
        /// waiting Runner is holding, so all of them resume at the same instant
        /// and each runs a full claim — a transaction, a locking select and a
        /// rollback. One wins and the rest did the work for nothing.
        /// </para>
        /// <para>
        /// At four or twelve Runners that is noise. At a hundred it is a hundred
        /// simultaneous transactions per submission against a connection pool of
        /// exactly a hundred, and the first thing to break would be ordinary web
        /// traffic rather than the claims themselves. A few tens of milliseconds
        /// drawn per waiter turns the spike into a queue, and costs a latency
        /// nobody can perceive.
        /// </para>
        /// <para>
        /// The deadline's jitter above does not do this: it spreads the *204s*,
        /// and a nudge is simultaneous by construction.
        /// </para>
        /// </summary>
        private static readonly TimeSpan NudgeSpread = TimeSpan.FromMilliseconds(50);
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
            var token = Token();

            // **The handout itself is not the client's to interrupt.** A claim
            // commits before its answer exists — `Running`, a lease token, a
            // delivery spent — and only then is the response built, so a request
            // torn down in between leaves a job nobody holds, that nobody else
            // may take, and that cost a participant an attempt. The Runner
            // cannot give it back: it never learned the lease token.
            //
            // So the look runs on the process's own lifetime and the *wait*
            // keeps the request's, which is the half where interrupting costs
            // nothing. What this cannot close is an answer lost with no abort
            // ever observed — a black-holed connection — and that is what the
            // reaper's refund is for.
            var handing = lifetime.ApplicationStopping;

            // **Nothing is held while waiting.** The transaction, the lock and
            // the connection all live inside `ClaimAsync` and are gone before
            // the wait begins — otherwise a fleet holding claims open would be
            // a fleet holding the connection pool empty.
            var waiting = Waiting(input?.WaitSeconds);
            var started = Environment.TickCount64;

            // **Captured before the look, never after it.** The obvious order —
            // look, find nothing, then start listening — leaves this request
            // deaf for as long as the look takes to unwind: the rollback round
            // trip, the transaction's disposal, the return. A submission
            // committed in that window fires its nudge into an empty room,
            // because the waiter it was meant for is not holding anything yet,
            // and the cost is a full wait of latency for work that was already
            // there. Holding the signal first turns the same window into a
            // nudge already delivered.
            //
            // Both halves of that window widen under exactly the load
            // `NudgeSpread` exists for: the continuations after `CommitAsync`
            // and `RollbackAsync` are thread-pool work, and a fleet resuming
            // together is what starves the pool.
            var nudge = queue.Capture();

            // The first look is this request's own, which is the whole of the
            // work when no wait was asked for.
            var runner = await runners.AuthenticateAsync(token, handing);
            if (await runners.ClaimAsync(runner, input?.LeaseSeconds, handing) is { } first)
            {
                return await HandedOverAsync(runners, first, handing);
            }

            for (; ; )
            {
                var left = waiting - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                if (left <= TimeSpan.Zero)
                {
                    // The same 204 as always, and it still means "nothing
                    // matched" rather than anything having gone wrong.
                    return NoContent();
                }

                // A nudge says something became claimable; it does not say it
                // was claimable by *this* Runner, whose types and tags may not
                // match. So the answer is another look, not a job.
                if (await nudge.WaitAsync(left, ct))
                {
                    // Woken rather than timed out, so this look is one of many
                    // starting together — see `NudgeSpread`.
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(
                            Random.Shared.NextDouble() * NudgeSpread.TotalMilliseconds),
                        ct);
                }

                // **A scope of its own for every later look, and this is not
                // tidiness.** A request scope carries one `DbContext`, and a
                // tracking query returns the instance already in its change
                // tracker rather than the row as it now stands. Holding one
                // request open for the length of a wait therefore froze two
                // things that are checked precisely because they change:
                //
                //   - the maintenance level, so a drain begun mid-hold was
                //     invisible and this claim could hand out a job *after*
                //     the drainer had seen the queue go quiet and closed the
                //     door — defeating the one guarantee a drain offers;
                //   - the Runner's own row, so revoking a Runner or retagging
                //     an activity away from it did not take effect until the
                //     hold ended, against a comment on `AuthenticateAsync`
                //     saying it is checked on every call for exactly that
                //     reason.
                //
                // Both were true only because a claim became long. A fresh
                // scope reads both from the database again, and costs one
                // lookup per nudge.
                // Captured before the look it protects, for the reason given
                // where the first one is taken.
                nudge = queue.Capture();

                using var scope = scopes.CreateScope();
                var again = scope.ServiceProvider.GetRequiredService<IRunnerService>();
                var current = await again.AuthenticateAsync(token, handing);
                if (await again.ClaimAsync(current, input?.LeaseSeconds, handing) is { } job)
                {
                    return await HandedOverAsync(again, job, handing);
                }
            }
        }

        /// <summary>
        /// Answers with the job, unless the caller has already gone — in which
        /// case the handout is undone rather than left leased to nobody.
        /// <para>
        /// **A best effort, and it says so.** `RequestAborted` is set when the
        /// going was noticed, which is not every way a client goes; the
        /// remaining cases are the reaper's, and cost the participant nothing
        /// because the delivery is refunded there too. What this buys over the
        /// reaper alone is the ten minutes in between.
        /// </para>
        /// </summary>
        private async Task<ActionResult<ClaimedJobDto>> HandedOverAsync(
            IRunnerService service, ClaimedJobDto job, CancellationToken handing)
        {
            if (!HttpContext.RequestAborted.IsCancellationRequested) return Ok(job);

            await service.UnclaimAsync(Guid.Parse(job.JobId), handing);
            return NoContent();
        }

        /// <summary>
        /// How long this request may be held, clamped and jittered.
        /// </summary>
        private static TimeSpan Waiting(int? asked)
        {
            if (asked is not { } seconds || seconds <= 0)
            {
                return TimeSpan.Zero;
            }

            var wanted = TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));
            // Shortened rather than lengthened, so the clamp above stays a
            // ceiling and a Runner's own request timeout is never overrun.
            var spread = Random.Shared.NextDouble() * wanted.TotalMilliseconds / JitterDivisor;
            return wanted - TimeSpan.FromMilliseconds(spread);
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

        /// <summary>
        /// Gives a job back because this Runner is stopping. It is queued again
        /// at once, and the delivery the claim counted is given back — an
        /// operator restarting a fleet must not spend a submission's attempts.
        /// </summary>
        [HttpPost("jobs/{jobId:guid}/release")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType<ProblemDto>(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Release(
            Guid jobId, [FromBody] LeaseRequestDto input, CancellationToken ct)
        {
            var runner = await runners.AuthenticateAsync(Token(), ct);
            await runners.ReleaseAsync(runner, jobId, input.LeaseToken, ct);
            return NoContent();
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
            // The same rule as the participant-facing endpoint, and deliberately
            // the same code: these two answers must not drift. See
            // `Utils/Downloads.cs`.
            Response.Headers.ContentDisposition = Downloads.Disposition(file);

            return File(
                fileStream: content,
                contentType: Downloads.ContentType(file),
                fileDownloadName: null,
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
            //
            // **And it is kept.** Throwing this answer away was the whole of a
            // defect: the commit then asked a session service that answers null
            // for a Runner, so every Runner's uploads landed in one anonymous
            // pool and one Runner could attach another's fresh bytes as its own
            // log.
            var runner = await runners.AuthenticateAsync(Token(), ct);

            var upload = await MultipartUpload.ReadAsync(
                Request, UploadLimits.Package,
                (content, _, _, token) => files.StageAsync(content, token), ct);

            if (upload.File is not { SizeBytes: > 0 } staged)
            {
                if (upload.File is { } empty) await files.DiscardAsync(empty, ct);
                throw new ValidationException("A file is required", "file.required");
            }

            var stored = await files.CommitAsync(
                staged, upload.FileName ?? "", upload.ContentType ?? "", upload.Field("sha256"),
                Services.Uploader.Runner(runner.Id), ct);
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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using DbRunner = AlgoJudge.Server.Database.Models.Runner;

namespace AlgoJudge.Server.Services
{
    public interface IRunnerService
    {
        Task<RunnerRegisteredDto> RegisterAsync(RunnerRegisterDto input, string? address, CancellationToken ct);
        Task<RunnerChallengeDto> ChallengeAsync(string fingerprint, CancellationToken ct);
        Task<RunnerTokenDto> TokenAsync(RunnerTokenRequestDto input, string? address, CancellationToken ct);
        Task<DbRunner> AuthenticateAsync(string? token, CancellationToken ct);
        Task<ClaimedJobDto?> ClaimAsync(DbRunner runner, int? leaseSeconds, CancellationToken ct);
        Task<ReportAcceptedDto> ReportAsync(DbRunner runner, Guid jobId, ReportResultDto report, CancellationToken ct);

        Task HeartbeatAsync(DbRunner runner, string? address, CancellationToken ct);
        Task<LeaseDto> RenewAsync(DbRunner runner, Guid jobId, string leaseToken, int? seconds, CancellationToken ct);
        Task ProgressAsync(DbRunner runner, Guid jobId, string leaseToken, CancellationToken ct);

        /// <summary>
        /// Whether this Runner may read these bytes <b>through a job it is
        /// holding</b>. The whole authorization question for a Runner's reads.
        /// </summary>
        Task<bool> MayReadAsync(DbRunner runner, Guid fileId, CancellationToken ct);

        /// <summary>Attaches a file the Runner uploaded to itself, under a name it replaces.</summary>
        Task AttachToSelfAsync(DbRunner runner, Guid fileId, string name, CancellationToken ct);

        /// <summary>Attaches a file to an attempt the Runner is holding.</summary>
        Task AttachToJobAsync(DbRunner runner, Guid jobId, string leaseToken, Guid fileId, string name, CancellationToken ct);
    }

    /// <summary>
    /// Runner registration, the handshake, and the job queue.
    /// <para>
    /// A Runner self-registers its key and an administrator approves it; nothing
    /// is evaluated before approval. The key is generated once and is
    /// <b>immutable</b> — there is no rotation, so a leaked key is revoked and
    /// that Runner comes back as a new identity.
    /// </para>
    /// </summary>
    public class RunnerService(
        ApplicationDbContext context,
        ISubmissionService submissions,
        IMaintenanceService maintenance,
        TimeProvider clock,
        ILogger<RunnerService> logger
    ) : IRunnerService
    {
        private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);
        private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan MaxLease = TimeSpan.FromMinutes(60);
        private const int MaxDeliveries = 5;

        /// <summary>
        /// Nonces and tokens, in memory.
        /// <para>
        /// Deliberately not in the database: both are short-lived, and a Server
        /// restart invalidating them costs a Runner one handshake. Storing them
        /// would add two tables that exist only to be swept. The cost is stated
        /// plainly — with several Server instances behind a load balancer these
        /// do not travel, so a Runner must repeat the handshake against whichever
        /// instance answers. That is fine for one instance and has to change
        /// before there are two.
        /// </para>
        /// </summary>
        private static readonly ConcurrentDictionary<string, (string Fingerprint, DateTimeOffset Expires)> Nonces = new();
        private static readonly ConcurrentDictionary<string, (Guid RunnerId, DateTimeOffset Expires)> Tokens = new();

        public async Task<RunnerRegisteredDto> RegisterAsync(
            RunnerRegisterDto input, string? address, CancellationToken ct)
        {
            var publicKey = (input.PublicKey ?? "").Trim();
            byte[] raw;
            try
            {
                raw = Convert.FromBase64String(publicKey);
            }
            catch (FormatException)
            {
                throw new ValidationException("The public key is not base64", "runner.key.malformed");
            }
            if (raw.Length != 32)
            {
                throw new ValidationException("An Ed25519 public key is 32 bytes", "runner.key.length");
            }

            var fingerprint = Fingerprint(raw);
            var existing = await context.Runners.FirstOrDefaultAsync(r => r.Fingerprint == fingerprint, ct);

            if (existing is not null)
            {
                if (existing.State == RunnerState.Revoked)
                {
                    // A revoked key never comes back. That is what makes
                    // revocation mean something when there is no rotation.
                    throw new ConflictException(
                        "That key has been revoked; register a new one", "runner.revoked");
                }
                // Registering again is how a Runner reports a restart. Its
                // capabilities may have changed; its identity may not.
                existing.Name = input.Name;
                existing.Product = input.Product;
                existing.Version = input.Version;
                existing.ProblemTypes = input.ProblemTypes.ToList();
                // A capability like any other, and it may change: a Runner
                // reconfigured to forward work says so on its next registration
                // rather than keeping the answer it gave when it first started.
                existing.External = input.External;
                existing.Machine = input.Machine is null ? null : JsonSerializer.Serialize(input.Machine);
                existing.Address = address;
                existing.LastSeenAt = clock.GetUtcNow().UtcDateTime;
                await context.SaveChangesAsync(ct);
                return Registered(existing);
            }

            var runner = new DbRunner
            {
                Name = input.Name,
                Product = input.Product,
                Version = input.Version,
                PublicKey = publicKey,
                Fingerprint = fingerprint,
                State = RunnerState.PendingApproval,
                ProblemTypes = input.ProblemTypes.ToList(),
                External = input.External,
                Machine = input.Machine is null ? null : JsonSerializer.Serialize(input.Machine),
                // Read from the connection, never reported. A machine is a bad
                // witness to how it is reached.
                Address = address,
                LastSeenAt = clock.GetUtcNow().UtcDateTime,
            };
            context.Runners.Add(runner);
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Runner {Fingerprint} registered, awaiting approval", fingerprint);
            return Registered(runner);
        }

        private static RunnerRegisteredDto Registered(DbRunner runner) => new()
        {
            RunnerId = Wire.Id(runner.Id),
            Fingerprint = runner.Fingerprint,
            State = Projections.Wire(runner.State),
        };

        private static string Fingerprint(byte[] publicKey) =>
            Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();

        public async Task<RunnerChallengeDto> ChallengeAsync(string fingerprint, CancellationToken ct)
        {
            var runner = await context.Runners.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Fingerprint == fingerprint, ct)
                ?? throw new NotFoundException("Runner");

            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var expires = clock.GetUtcNow().Add(NonceLifetime);
            Nonces[nonce] = (runner.Fingerprint, expires);

            Sweep();
            return new RunnerChallengeDto { Nonce = nonce, ExpiresAt = expires.UtcDateTime.ToString("O") };
        }

        public async Task<RunnerTokenDto> TokenAsync(
            RunnerTokenRequestDto input, string? address, CancellationToken ct)
        {
            // Single use: taken out of the table before it is checked, so a
            // signature cannot be replayed even by the Runner that made it.
            if (!Nonces.TryRemove(input.Nonce ?? "", out var issued))
            {
                throw new ForbiddenActionException("Unknown or spent nonce", "runner.nonce.unknown");
            }
            if (issued.Expires <= clock.GetUtcNow())
            {
                throw new ForbiddenActionException("The nonce has expired", "runner.nonce.expired");
            }
            if (issued.Fingerprint != input.Fingerprint)
            {
                throw new ForbiddenActionException("The nonce was issued to another key", "runner.nonce.mismatch");
            }

            var runner = await context.Runners.FirstOrDefaultAsync(r => r.Fingerprint == input.Fingerprint, ct)
                ?? throw new NotFoundException("Runner");

            if (runner.State != RunnerState.Approved)
            {
                throw new ForbiddenActionException(
                    "This Runner has not been approved", "runner.notApproved");
            }

            if (!VerifySignature(runner.PublicKey, input.Nonce!, input.Signature ?? ""))
            {
                throw new ForbiddenActionException("The signature does not verify", "runner.signature");
            }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var expires = clock.GetUtcNow().Add(TokenLifetime);
            Tokens[token] = (runner.Id, expires);

            runner.Address = address;
            runner.LastSeenAt = clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(ct);

            return new RunnerTokenDto { Token = token, ExpiresAt = expires.UtcDateTime.ToString("O") };
        }

        private static bool VerifySignature(string publicKeyBase64, string nonce, string signatureBase64)
        {
            try
            {
                var key = new Ed25519PublicKeyParameters(Convert.FromBase64String(publicKeyBase64), 0);
                var signature = Convert.FromBase64String(signatureBase64);
                var message = Encoding.UTF8.GetBytes(nonce);

                var verifier = new Ed25519Signer();
                verifier.Init(false, key);
                verifier.BlockUpdate(message, 0, message.Length);
                return verifier.VerifySignature(signature);
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                return false;
            }
        }

        public async Task<DbRunner> AuthenticateAsync(string? token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(token) || !Tokens.TryGetValue(token, out var issued))
            {
                throw new UnauthenticatedException("A Runner token is required");
            }
            if (issued.Expires <= clock.GetUtcNow())
            {
                Tokens.TryRemove(token, out _);
                throw new UnauthenticatedException("The Runner token has expired");
            }

            var runner = await context.Runners.FirstOrDefaultAsync(r => r.Id == issued.RunnerId, ct)
                ?? throw new UnauthenticatedException("Unknown Runner");

            // Checked on every call, not only at the handshake: revoking a Runner
            // has to stop the one that is already holding a token.
            if (runner.State != RunnerState.Approved)
            {
                throw new ForbiddenActionException("This Runner is not approved", "runner.notApproved");
            }
            return runner;
        }

        private void Sweep()
        {
            var now = clock.GetUtcNow();
            foreach (var (nonce, issued) in Nonces)
            {
                if (issued.Expires <= now) Nonces.TryRemove(nonce, out _);
            }
            foreach (var (token, issued) in Tokens)
            {
                if (issued.Expires <= now) Tokens.TryRemove(token, out _);
            }
        }

        /// <summary>
        /// Takes one queued job, atomically.
        /// <para>
        /// <c>FOR UPDATE SKIP LOCKED</c> is the whole mechanism: two Runners
        /// asking at the same instant take different rows instead of fighting
        /// over one, and neither waits. This is raw SQL because EF has no way to
        /// express it, and it is why the integration tests may not use an
        /// in-memory provider — the guarantee being relied on is PostgreSQL's.
        /// </para>
        /// </summary>
        public async Task<ClaimedJobDto?> ClaimAsync(DbRunner runner, int? leaseSeconds, CancellationToken ct)
        {
            // **An empty answer rather than a refusal.** A Server that is
            // draining has work in its queue and is declining to hand it out;
            // `204` is exactly what a Runner already does the right thing with,
            // and a 503 here would be a second thing to teach for no gain. The
            // work stays queued and goes out when the window ends.
            if (await maintenance.StateAsync(ct) is { Level: not MaintenanceLevel.Open })
            {
                return null;
            }

            // **An installation that has not chosen to send work outside sends
            // none**, and says so the same way a drained Server does: an empty
            // queue. A Runner that forwards submissions is not refused, revoked
            // or told off — it is simply given nothing, and the work waits for
            // the switch rather than failing against it.
            //
            // Read here rather than cached: it is one row, on a path that is
            // already doing a transaction and a locking read, and an operator
            // turning the switch on expects the queue to start draining rather
            // than to drain after a restart.
            if (runner.External && !await ExternalJudgingAllowedAsync(ct))
            {
                return null;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var lease = leaseSeconds is { } seconds
                ? TimeSpan.FromSeconds(Math.Clamp(seconds, 60, MaxLease.TotalSeconds))
                : DefaultLease;

            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // Types are matched by equality and never parsed, which is what keeps
            // "adding a problem type is not a Server change" true while still
            // letting the Server route work.
            //
            // Cast to object deliberately: `SqlQueryRaw` takes `params object[]`,
            // so handing it a `string[]` bare spreads the array into one
            // parameter per element and `{0}` becomes the first type alone —
            // which Postgres then rejects, because `= ANY(scalar)` is not a
            // thing. One parameter carrying the whole array is what is wanted.
            var types = (object)runner.ProblemTypes.ToArray();

            // **The whole of the Server's knowledge about external judging, in
            // one comparison.** An external problem goes to an external Runner
            // and a local one does not — equality, not a rule with an exception,
            // so neither half can leak into the other. The Server does not read
            // the problem type to decide this, does not know which service is on
            // the far end, and treats every external problem alike.
            //
            // The equality matters in both directions. A local problem reaching
            // a forwarding Runner would send somebody's work out of the building
            // without anyone having chosen that, which is worse than the case
            // this was written for.
            var external = (object)runner.External;

            // The lock is taken on the id alone, and the row is loaded through EF
            // afterwards inside the same transaction.
            //
            // Selecting the whole entity here instead would mean `SELECT j.*`,
            // which does not return `xmin` — the system column the concurrency
            // token maps to — and EF refuses a row it cannot find every mapped
            // column in. Locking by id keeps the raw SQL to the one thing EF
            // genuinely cannot express.
            var claimedId = await context.Database
                .SqlQueryRaw<Guid>("""
                    SELECT j."Id" AS "Value" FROM "EvaluationJobs" j
                    JOIN "Submissions" s ON s."Id" = j."SubmissionId"
                    JOIN "SeriesProblems" sp ON sp."Id" = s."SeriesProblemId"
                    JOIN "Problems" p ON p."Id" = sp."ProblemId"
                    WHERE j."State" = 0
                      AND p."Type" = ANY({0})
                      AND p."External" = {1}
                    ORDER BY j."CreatedAt"
                    FOR UPDATE OF j SKIP LOCKED
                    LIMIT 1
                    """, types, external)
                .ToListAsync(ct);

            if (claimedId.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            var job = await context.EvaluationJobs.FirstAsync(j => j.Id == claimedId[0], ct);

            job.State = EvaluationJobState.Running;
            job.RunnerId = runner.Id;
            job.LeaseToken = Uuid.New();
            job.ClaimedAt = now;
            job.LeaseExpiresAt = now.Add(lease);
            job.Deliveries += 1;

            runner.LastSeenAt = now;

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await submissions.AnnounceAsync(job.SubmissionId, ct);

            return await DescribeAsync(job, ct);
        }

        private async Task<ClaimedJobDto> DescribeAsync(EvaluationJob job, CancellationToken ct)
        {
            var submission = await context.Submissions
                .AsNoTracking()
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Problem)
                .Include(s => s.Files).ThenInclude(f => f.File)
                .FirstAsync(s => s.Id == job.SubmissionId, ct);

            var assignment = submission.SeriesProblem!;

            var package = await context.FileReferences.AsNoTracking()
                .Include(r => r.File)
                .FirstOrDefaultAsync(
                    r => r.ProblemVersionId == job.ProblemVersionId
                        && r.Name == PackageNames.Archive, ct);

            var version = await context.ProblemVersions.AsNoTracking()
                .FirstAsync(v => v.Id == job.ProblemVersionId, ct);

            return new ClaimedJobDto
            {
                JobId = Wire.Id(job.Id),
                SubmissionId = Wire.Id(job.SubmissionId),
                Attempt = job.Attempt,
                LeaseToken = Wire.Id(job.LeaseToken!.Value),
                LeaseExpiresAt = Wire.At(job.LeaseExpiresAt!.Value),
                ProblemType = assignment.Problem!.Type,
                ProblemVersionId = Wire.Id(job.ProblemVersionId),
                PackageFileId = package is null ? "" : Wire.Id(package.FileId),
                PackageSha256 = package?.File?.Sha256 ?? "",
                Files = submission.Files.Select(Projections.SubmissionFile).ToList(),
                Language = submission.Language,
                // The chain, merged: package, then version, then assignment. The
                // Server merged two documents it never read.
                Config = MergeConfig(version.Config, assignment.Config),
            };
        }

        /// <summary>
        /// The later layer wins, member by member at the top level.
        /// <para>
        /// The Server does not understand either document — it only knows that
        /// the assignment's entries override the version's. A deeper merge would
        /// require knowing what the members mean.
        /// </para>
        /// </summary>
        private static object? MergeConfig(string? version, string? assignment)
        {
            var lower = Projections.Opaque(version) as JsonElement?;
            var upper = Projections.Opaque(assignment) as JsonElement?;
            if (lower is null) return upper;
            if (upper is null) return lower;
            if (lower.Value.ValueKind != JsonValueKind.Object || upper.Value.ValueKind != JsonValueKind.Object)
            {
                return upper;
            }

            var merged = new Dictionary<string, JsonElement>();
            foreach (var member in lower.Value.EnumerateObject()) merged[member.Name] = member.Value;
            foreach (var member in upper.Value.EnumerateObject()) merged[member.Name] = member.Value;
            return merged;
        }

        /// <summary>
        /// Records a verdict, once.
        /// <para>
        /// Idempotent on the lease token, backed by a unique index: a Runner that
        /// resends because it did not see the acknowledgement gets the same
        /// result rather than a second one, and a Runner whose lease was already
        /// reclaimed is refused rather than allowed to overwrite a newer attempt.
        /// </para>
        /// </summary>
        public async Task<ReportAcceptedDto> ReportAsync(
            DbRunner runner, Guid jobId, ReportResultDto report, CancellationToken ct)
        {
            var job = await context.EvaluationJobs
                .Include(j => j.Result)
                .FirstOrDefaultAsync(j => j.Id == jobId, ct)
                ?? throw new NotFoundException("Evaluation job");

            if (!Guid.TryParse(report.LeaseToken, out var presented))
            {
                throw new ValidationException("A lease token is required", "runner.lease.malformed");
            }

            // The repeat. Checked before the lease, because a Runner resending
            // after its lease expired is still telling the truth about what it
            // computed — and the stored result is what it computed.
            if (job.Result is not null)
            {
                return new ReportAcceptedDto
                {
                    ResultId = Wire.Id(job.Result.Id),
                    State = Projections.Wire(job.State),
                    Duplicate = true,
                };
            }

            if (job.LeaseToken != presented)
            {
                throw new ForbiddenActionException(
                    "This lease is no longer held; the job was reclaimed", "runner.lease.stale");
            }
            if (job.RunnerId != runner.Id)
            {
                throw new ForbiddenActionException("That job belongs to another Runner", "runner.lease.foreign");
            }
            if (job.State != EvaluationJobState.Running)
            {
                throw new ConflictException(
                    $"A job in state {Projections.Wire(job.State)} takes no result", "job.state");
            }

            var now = clock.GetUtcNow().UtcDateTime;

            // The board's ceiling, not a document's: `extra` rides the results
            // feed once per submission per contestant. Checked here rather than
            // inline since 2026-08-22, so that the envelope rule reaches it too.
            var extra = Opaque.Store(report.Extra, "extra", OpaqueLimits.Board);

            // **A judged result has a verdict; a failure has none, and that is
            // not the same kind of absence.** An infrastructure failure is not a
            // judgement — it already carries no score and no maximum — so the
            // column stays nullable and the obligation lands here, on the path
            // that claims to have judged something.
            //
            // Required from 2026-08-22. Before that a Runner could report a
            // completed evaluation with no word for what happened, and every
            // screen showed a blank where the outcome belongs.
            if (!report.InfrastructureFailure)
            {
                if (string.IsNullOrWhiteSpace(report.Verdict))
                {
                    throw new ValidationException(
                        "A judged result must carry a verdict", "result.verdict.missing");
                }
                // The column is `varchar(64)`. Unchecked, a longer one reached
                // the database as an unhandled error rather than as an answer
                // the Runner could act on.
                if (report.Verdict.Length > 64)
                {
                    throw new ValidationException(
                        $"`verdict` is {report.Verdict.Length} characters, over the 64 the column holds",
                        "result.verdict.tooLong");
                }
            }

            var result = new Result
            {
                EvaluationJobId = job.Id,
                // Copied from the job on purpose: what a result was judged
                // against has to stay pinned to it.
                ProblemVersionId = job.ProblemVersionId,
                Score = report.InfrastructureFailure ? null : report.Score,
                MaxScore = report.InfrastructureFailure ? null : report.MaxScore,
                Verdict = report.Verdict,
                Extra = extra,
                RunnerVersion = report.RunnerVersion ?? runner.Version,
            };
            context.Results.Add(result);

            // An infrastructure failure is not a wrong answer and must never be
            // scored as one.
            job.State = report.InfrastructureFailure ? EvaluationJobState.Failed : EvaluationJobState.Completed;
            job.FailureReason = report.InfrastructureFailure ? report.FailureReason : null;
            job.FinishedAt = now;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;

            foreach (var attachment in report.Files ?? [])
            {
                if (!Guid.TryParse(attachment.FileId, out var fileId)) continue;
                if (!await context.Files.AnyAsync(f => f.Id == fileId, ct)) continue;

                context.FileReferences.Add(new FileReference
                {
                    FileId = fileId,
                    OwnerKind = FileOwnerKind.Attempt,
                    EvaluationJobId = job.Id,
                    // Participant scope on the reference; whether a participant
                    // actually reads it is the activity's attachment table, and
                    // an unlisted name is managers-only.
                    Scope = FileScope.Participant,
                    Name = attachment.Name,
                });
            }

            if (!report.InfrastructureFailure) runner.CompletedJobs += 1;
            runner.LastSeenAt = now;

            await context.SaveChangesAsync(ct);
            await submissions.AnnounceAsync(job.SubmissionId, ct);

            return new ReportAcceptedDto
            {
                ResultId = Wire.Id(result.Id),
                State = Projections.Wire(job.State),
                Duplicate = false,
            };
        }

        public async Task HeartbeatAsync(DbRunner runner, string? address, CancellationToken ct)
        {
            runner.LastSeenAt = clock.GetUtcNow().UtcDateTime;
            // Read from the connection every time, not only at registration: a
            // Runner that moved is at a new address, and it is still a bad
            // witness to its own.
            if (address is not null) runner.Address = address;
            await context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Extends a lease the Runner still holds.
        /// <para>
        /// The deadline is a correctness mechanism, not a safety net: the Runner
        /// is stateless, so a job whose lease runs out is reclaimed and given to
        /// somebody else. A long evaluation renews rather than being cut off.
        /// </para>
        /// <para>
        /// <b>Renewing never shortens.</b> This replaced the deadline outright
        /// until 2026-08-09, so a Runner that claimed for ten minutes and then
        /// said "still working" asking for one brought its own deadline eight
        /// minutes closer — and the obvious implementation, a short ping on a
        /// short interval, left itself a permanent sixty-second margin. One
        /// pause longer than that and the job goes to a second Runner while the
        /// first is still computing. A call named "renew" must not be able to do
        /// that.
        /// </para>
        /// </summary>
        public async Task<LeaseDto> RenewAsync(
            DbRunner runner, Guid jobId, string leaseToken, int? seconds, CancellationToken ct)
        {
            var lease = seconds is { } requested
                ? TimeSpan.FromSeconds(Math.Clamp(requested, 60, MaxLease.TotalSeconds))
                : DefaultLease;

            var job = await ExtendAsync(runner, jobId, leaseToken, lease, ct);

            return new LeaseDto
            {
                JobId = Wire.Id(job.Id),
                LeaseToken = leaseToken,
                LeaseExpiresAt = Wire.At(job.LeaseExpiresAt.Value),
            };
        }

        /// <summary>
        /// Says the Runner is still working. Renews the lease as a side effect,
        /// because that is what "still working" means to the Server — there is
        /// nothing else it can do with the news.
        /// </summary>
        public async Task ProgressAsync(
            DbRunner runner, Guid jobId, string leaseToken, CancellationToken ct)
        {
            // Same rule as renewing, for the same reason: saying "still working"
            // must never bring the deadline closer — and it loses the same race,
            // so it goes through the same place.
            await ExtendAsync(runner, jobId, leaseToken, DefaultLease, ct);
        }

        /// <summary>
        /// Pushes a held lease out, and survives losing the race to do it.
        ///
        /// <para>
        /// <b>Reading a job and then writing it is two steps, and the reaper fits
        /// between them.</b> The moment a deadline passes, <c>LeaseReaper</c>
        /// takes the job back; a renewal already past its read then writes a row
        /// that has moved, and PostgreSQL's <c>xmin</c> — the concurrency token
        /// on <c>EvaluationJob</c>, and the only one on this path — makes that
        /// update match nothing. EF raises <c>DbUpdateConcurrencyException</c>,
        /// and unhandled it left the endpoint answering <b>500</b>.
        /// </para>
        ///
        /// <para>
        /// <b>Which is the one answer a Runner cannot act on.</b> "Try again in a
        /// moment" and "you have lost this job, stop computing" are opposite
        /// actions, and a 500 is both. §5 of the accepted Server–Runner API
        /// already names the right one — <c>runner.lease.stale</c> — so this is
        /// the contract being met rather than changed.
        /// </para>
        ///
        /// <para>
        /// The conflict is not itself the answer, though: it says the row moved,
        /// not who moved it or into what. So the job is re-read and put back
        /// through the same rules, which produce <c>stale</c>, <c>foreign</c>,
        /// <c>job.state</c> or a plain 404 on their own evidence. A conflict that
        /// changed none of those is somebody touching an unrelated column, and
        /// the write is simply tried again.
        /// </para>
        /// </summary>
        private async Task<EvaluationJob> ExtendAsync(
            DbRunner runner, Guid jobId, string leaseToken, TimeSpan lease, CancellationToken ct)
        {
            // Two attempts, not a loop without an end: the second read is against
            // a row somebody has just written, so a further conflict would mean
            // continuous contention on one job, and spinning through it would
            // hold a request open rather than answer it.
            for (var attempt = 0; ; attempt++)
            {
                var job = await HeldJobAsync(runner, jobId, leaseToken, ct);

                job.LeaseExpiresAt = Later(job.LeaseExpiresAt, clock.GetUtcNow().UtcDateTime.Add(lease));
                runner.LastSeenAt = clock.GetUtcNow().UtcDateTime;

                try
                {
                    await context.SaveChangesAsync(ct);
                    return job;
                }
                catch (DbUpdateConcurrencyException conflict) when (attempt == 0)
                {
                    // Reloaded from the database rather than re-queried: the
                    // context is still tracking the stale instance, and a second
                    // read would hand back exactly what was just refused.
                    foreach (var entry in conflict.Entries)
                    {
                        await entry.ReloadAsync(ct);
                    }
                }
            }
        }

        /// <summary>
        /// The later of a deadline already granted and one now asked for.
        /// <para>
        /// A Runner that genuinely wants to give a job up early releases it;
        /// there is no reason to express that by asking for a short renewal, and
        /// nothing ever did.
        /// </para>
        /// </summary>
        private static DateTime Later(DateTime? current, DateTime proposed) =>
            current is { } held && held > proposed ? held : proposed;

        /// <summary>
        /// The job this Runner is holding under this lease, or a refusal.
        /// <para>
        /// Three separate things are checked, and each is a different mistake:
        /// the job exists, this Runner has it, and the lease it presents is the
        /// current one. A Runner whose lease was reclaimed while it worked must
        /// be refused rather than allowed to overwrite whoever has it now.
        /// </para>
        /// </summary>
        private async Task<EvaluationJob> HeldJobAsync(
            DbRunner runner, Guid jobId, string leaseToken, CancellationToken ct)
        {
            var job = await context.EvaluationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
                ?? throw new NotFoundException("Evaluation job");

            if (!Guid.TryParse(leaseToken, out var presented) || job.LeaseToken != presented)
            {
                throw new ForbiddenActionException(
                    "This lease is no longer held; the job was reclaimed", "runner.lease.stale");
            }
            if (job.RunnerId != runner.Id)
            {
                throw new ForbiddenActionException("That job belongs to another Runner", "runner.lease.foreign");
            }
            if (job.State != EvaluationJobState.Running)
            {
                throw new ConflictException(
                    $"A job in state {Projections.Wire(job.State)} is not being worked on", "job.state");
            }

            return job;
        }

        /// <summary>
        /// A Runner reads what the jobs it is <b>currently holding</b> need, and
        /// nothing else.
        /// <para>
        /// Authorized against the job, not against being a Runner. Without that,
        /// any approved Runner could fetch every test package in the
        /// installation by asking for file ids — and the packages are the
        /// problems.
        /// </para>
        /// </summary>
        public async Task<bool> MayReadAsync(DbRunner runner, Guid fileId, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var held = await context.EvaluationJobs
                .AsNoTracking()
                .Where(j => j.RunnerId == runner.Id
                    && j.State == EvaluationJobState.Running
                    && j.LeaseExpiresAt != null && j.LeaseExpiresAt > now)
                .Select(j => new { j.Id, j.ProblemVersionId, j.SubmissionId })
                .ToListAsync(ct);

            if (held.Count == 0) return false;

            var versions = held.Select(j => j.ProblemVersionId).ToList();
            var submissions = held.Select(j => j.SubmissionId).ToList();
            var jobs = held.Select(j => j.Id).ToList();

            return await context.FileReferences.AsNoTracking().AnyAsync(r =>
                r.FileId == fileId
                && ((r.ProblemVersionId != null && versions.Contains(r.ProblemVersionId.Value))
                    || (r.SubmissionId != null && submissions.Contains(r.SubmissionId.Value))
                    || (r.EvaluationJobId != null && jobs.Contains(r.EvaluationJobId.Value))), ct);
        }

        /// <summary>
        /// What a Runner uploads about itself — `runner.log`, `lscpu.txt`.
        /// <para>
        /// It <b>replaces</b> the name rather than adding another, so "old
        /// versions" means earlier ones under the same name. That is what the
        /// twenty-revision limit counts, and what keeps a chatty Runner costing a
        /// fixed amount rather than an unbounded one.
        /// </para>
        /// </summary>
        public async Task AttachToSelfAsync(
            DbRunner runner, Guid fileId, string name, CancellationToken ct)
        {
            if (!await context.Files.AnyAsync(f => f.Id == fileId, ct))
            {
                throw new ValidationException("That file is not stored", "file.missing");
            }

            var now = clock.GetUtcNow().UtcDateTime;

            var current = await context.FileReferences
                .Where(r => r.RunnerId == runner.Id && r.Name == name && r.SupersededAt == null)
                .ToListAsync(ct);
            foreach (var reference in current) reference.SupersededAt = now;

            context.FileReferences.Add(new FileReference
            {
                FileId = fileId,
                OwnerKind = FileOwnerKind.Runner,
                RunnerId = runner.Id,
                // Operator material: a Runner's log is diagnostics, never
                // something a participant reads.
                Scope = FileScope.Manager,
                Name = name,
            });

            runner.LastSeenAt = now;
            await context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// Attaches the Runner's output to the attempt it is holding — its
        /// <c>log</c>, its <c>details</c>. Uploaded through the ordinary file
        /// endpoint first; this names it.
        /// </summary>
        public async Task AttachToJobAsync(
            DbRunner runner, Guid jobId, string leaseToken, Guid fileId, string name, CancellationToken ct)
        {
            var job = await HeldJobAsync(runner, jobId, leaseToken, ct);

            if (!await context.Files.AnyAsync(f => f.Id == fileId, ct))
            {
                throw new ValidationException("That file is not stored", "file.missing");
            }

            var already = await context.FileReferences
                .AnyAsync(r => r.EvaluationJobId == job.Id && r.Name == name, ct);
            if (already)
            {
                throw new ConflictException(
                    $"This attempt already has a file called {name}", "attempt.file.duplicate");
            }

            context.FileReferences.Add(new FileReference
            {
                FileId = fileId,
                OwnerKind = FileOwnerKind.Attempt,
                EvaluationJobId = job.Id,
                // Participant scope on the reference; whether a participant
                // actually reads it is the activity's attachment table, and an
                // unlisted name is managers-only.
                Scope = FileScope.Participant,
                Name = name,
            });

            await context.SaveChangesAsync(ct);
        }

        /// <summary>How many times a job may be handed out before it is given up on.</summary>
        public static int DeliveryCap => MaxDeliveries;

        /// <summary>
        /// Whether this installation has chosen to send work to a service it
        /// does not run.
        /// <para>
        /// Absent row reads as <c>false</c>. An installation whose singleton has
        /// not been written yet has chosen nothing, and "nothing chosen" is the
        /// off position for this one — the safe direction is the one where
        /// nobody's submission leaves the building by default.
        /// </para>
        /// </summary>
        private async Task<bool> ExternalJudgingAllowedAsync(CancellationToken ct) =>
            await context.Instance
                .AsNoTracking()
                .Select(i => i.ExternalJudgingEnabled)
                .FirstOrDefaultAsync(ct);

        /// <summary>How long a fresh lease runs when the Runner does not ask.</summary>
        public static TimeSpan DefaultLeaseTime => DefaultLease;
    }
}

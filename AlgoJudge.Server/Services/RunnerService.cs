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
        Task ReleaseAsync(DbRunner runner, Guid jobId, string leaseToken, CancellationToken ct);

        /// <summary>
        /// Undoes a handout whose answer never reached the Runner.
        /// </summary>
        Task UnclaimAsync(Guid jobId, CancellationToken ct);

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
        IQueueSignal queue,
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
        /// How stale <see cref="DbRunner.LastSeenAt"/> is allowed to get.
        /// <para>
        /// **The field is written on eight paths and read by one**, and that
        /// reader — the manager panel's <c>IsConnected</c> — rounds it to two
        /// minutes. Every renewal, every progress note, every claim and every
        /// report was rewriting the same row: an External Runner holding twenty
        /// jobs renews all twenty on one cycle, so nineteen of every twenty
        /// writes landed on one row inside one second, each leaving a dead
        /// tuple for autovacuum on a table with a dozen rows in it.
        /// </para>
        /// <para>
        /// Thirty seconds is comfortably inside the reader's two minutes and
        /// suppresses only the amplified paths: a heartbeat is a minute apart
        /// and is never blocked by this.
        /// </para>
        /// <para>
        /// The same reasoning, and the same shape, as
        /// <see cref="Realtime.SessionTrackingMiddleware"/>'s throttle on a
        /// session's <c>LastRequestAt</c>.
        /// </para>
        /// </summary>
        private static readonly TimeSpan SeenThrottle = TimeSpan.FromSeconds(30);

        /// <summary>
        /// When each Runner's row was last touched. **In memory on purpose**:
        /// losing it on a restart costs one extra write per Runner.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> Touched = new();

        /// <summary>
        /// Records that this Runner is alive, at most once per
        /// <see cref="SeenThrottle"/>.
        /// <para>
        /// Assigning nothing is the whole of the suppression: EF writes the
        /// columns it tracked as modified, so a call that changes nothing here
        /// simply leaves <c>LastSeenAt</c> out of the statement it was going to
        /// send anyway. Nothing extra is saved and nothing extra is skipped.
        /// </para>
        /// </summary>
        internal static void Seen(DbRunner runner, DateTime now)
        {
            if (Touched.TryGetValue(runner.Id, out var written)
                && now - written < SeenThrottle)
            {
                return;
            }

            Touched[runner.Id] = now;
            runner.LastSeenAt = now;
        }

        /// <summary>
        /// How many times a job may be handed back by a Runner that is stopping
        /// before it starts costing a delivery like everything else.
        /// <para>
        /// Being shut down is an operator's doing and a participant's attempts
        /// are not the operator's to spend — but a Runner crash-looping under a
        /// supervisor hands jobs back in exactly the same way, and from here
        /// nothing tells the two apart except how often it happens.
        /// </para>
        /// </summary>
        private const int FreeReleases = 3;

        /// <summary>
        /// How many unheard-of deliveries are given back before the cap is
        /// allowed to do its work. Three, for the same reason as above: an
        /// operator restarting a fleet and a job that poisons every Runner it
        /// reaches look identical from here, and only the count separates them.
        /// </summary>
        internal const int FreeRefunds = 3;

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

                // **Proof of the private key, before anything is written.** A
                // first registration cannot have one — the challenge endpoint
                // has no row to issue a nonce against — but every later one can,
                // and until 2026-09-04 none was asked for: anyone who could read
                // this Runner's public key could rewrite the fields the claim
                // pairs work on and the fields a manager reads when approving
                // it, while its approval stayed exactly where it was.
                Spend(input.Nonce, fingerprint);
                if (!VerifySignature(existing.PublicKey, input.Nonce!, input.Signature ?? ""))
                {
                    throw new ForbiddenActionException(
                        "The signature does not verify", "runner.signature");
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
                // **`Tags` is deliberately absent from this list.** It is seeded
                // from the Runner's configuration once, below, and belongs to
                // the operator afterwards — a restart must not be able to move a
                // Runner into an examination's pool.
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
                // Seeded here and nowhere else, so that thirty laboratory
                // machines can be deployed from one Compose file rather than
                // tagged one at a time in the panel.
                Tags = RunnerTags.Validated(input.Tags, "The Runner's tags"),
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

        /// <summary>
        /// Takes a nonce out of the table and says whether it was this key's.
        /// <para>
        /// <b>Single use, and removed before it is checked</b>, so a signature
        /// cannot be replayed even by the Runner that made it. Shared by the two
        /// anonymous calls that accept one — the handshake and a re-registration
        /// — because a fingerprint check that lived in one of them would be a
        /// check the other could be written without: <c>auth/challenge</c> hands
        /// a nonce to anyone who names a fingerprint, so "the nonce exists" is
        /// not evidence about who is presenting it.
        /// </para>
        /// </summary>
        private void Spend(string? nonce, string fingerprint)
        {
            if (!Nonces.TryRemove(nonce ?? "", out var issued))
            {
                throw new ForbiddenActionException("Unknown or spent nonce", "runner.nonce.unknown");
            }
            if (issued.Expires <= clock.GetUtcNow())
            {
                throw new ForbiddenActionException("The nonce has expired", "runner.nonce.expired");
            }
            if (issued.Fingerprint != fingerprint)
            {
                throw new ForbiddenActionException(
                    "The nonce was issued to another key", "runner.nonce.mismatch");
            }
        }

        public async Task<RunnerTokenDto> TokenAsync(
            RunnerTokenRequestDto input, string? address, CancellationToken ct)
        {
            Spend(input.Nonce, input.Fingerprint);

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
        /// The three filters a job passes before anybody is handed it: what this
        /// Runner can evaluate, whether it forwards work, and which pool it is
        /// in. A constant, so nothing built from a value reaches the database.
        /// </summary>
        private const string ClaimSql = $$"""
            SELECT j."Id" AS "Value" FROM "EvaluationJobs" j
            JOIN "Submissions" s ON s."Id" = j."SubmissionId"
            JOIN "SeriesProblems" sp ON sp."Id" = s."SeriesProblemId"
            JOIN "Problems" p ON p."Id" = sp."ProblemId"
            JOIN "Series" se ON se."Id" = sp."SeriesId"
            JOIN "Activities" a ON a."Id" = sp."ActivityId"
            WHERE j."State" = 0
              AND p."Type" = ANY({0})
              AND p."External" = {1}
              AND ({{RunnerTags.WorkTagsSql}}) && {2}::text[]
              AND NOT EXISTS (
                SELECT 1 FROM "EvaluationJobs" live
                WHERE live."SubmissionId" = j."SubmissionId" AND live."State" = 1)
            ORDER BY j."CreatedAt"
            FOR UPDATE OF j SKIP LOCKED
            LIMIT 1
            """;

        /// <summary>
        /// Takes one queued job, atomically.
        /// <para>
        /// <c>FOR UPDATE SKIP LOCKED</c> is the whole mechanism: two Runners
        /// asking at the same instant take different rows instead of fighting
        /// over one, and neither waits. This is raw SQL because EF has no way to
        /// express it, and it is why the integration tests may not use an
        /// in-memory provider — the guarantee being relied on is PostgreSQL's.
        /// </para>
        /// <para>
        /// **One submission is judged by one Runner at a time**, which the row
        /// lock alone never said: a rejudge issued while an attempt was running
        /// left two live jobs on one submission, and two Runners took them.
        /// A rejudge now supersedes a *queued* sibling outright, so the only
        /// case left is a sibling that is already <c>Running</c> — and the
        /// fourth filter holds the new attempt behind it rather than taking the
        /// job away from a Runner that is part-way through it. The wait ends
        /// when that attempt reports, which nudges the queue.
        /// </para>
        /// </summary>
        public async Task<ClaimedJobDto?> ClaimAsync(DbRunner runner, int? leaseSeconds, CancellationToken ct)
        {
            // **An empty answer rather than a refusal.** A Server that is
            // draining has work in its queue and is declining to hand it out;
            // `204` is exactly what a Runner already does the right thing with,
            // and a 503 here would be a second thing to teach for no gain. The
            // work stays queued and goes out when the window ends.
            if (await maintenance.LevelAsync(ct) is not MaintenanceLevel.Open)
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

            // **Which pool this Runner is in**, and the third filter of three.
            // The other two say what it can do; this one says whose work it may
            // be given, and it is the only one an operator sets. `RunnerTags`
            // owns both halves of the comparison — see the note there on why an
            // empty list is `default` rather than "anything".
            var tags = (object)RunnerTags.Effective(runner.Tags);

            // The lock is taken on the id alone, and the row is loaded through EF
            // afterwards inside the same transaction.
            //
            // Selecting the whole entity here instead would mean `SELECT j.*`,
            // which does not return `xmin` — the system column the concurrency
            // token maps to — and EF refuses a row it cannot find every mapped
            // column in. Locking by id keeps the raw SQL to the one thing EF
            // genuinely cannot express.
            //
            // **The tags are read here rather than stamped on the job**, so
            // retagging an activity redirects work that is already queued. A
            // stamp would leave yesterday's queue going to yesterday's Runners,
            // with nothing on any screen to say so.
            var claimedId = await context.Database
                .SqlQueryRaw<Guid>(ClaimSql, types, external, tags)
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
            // **Cleared here, so every requeue path gets it for free.** A job
            // that comes back from a release, a report or the reaper is claimed
            // again by this line, and each claim has to earn its own
            // acknowledgement — the previous holder's is not evidence about
            // this one.
            job.AcknowledgedAt = null;
            job.LeaseExpiresAt = now.Add(lease);
            // Kept as well as applied: a heartbeat has to renew by the lease this
            // job was granted, and the deadline alone cannot say what that was.
            job.LeaseSeconds = (int)lease.TotalSeconds;
            job.Deliveries += 1;

            Seen(runner, now);

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

            // Read for its `props` alone, which says which problem this is —
            // `uva@1`'s archive number lives there. Not a configuration layer:
            // the chain is the package and then the assignment.
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
                Props = Projections.Opaque(submission.Props),
                ProblemVersionProps = Projections.Opaque(version.Props),
                // **One layer, handed over whole.** The chain was package →
                // version → assignment and the Server merged the last two for a
                // Runner that then laid the result over the package. The middle
                // layer is gone (2026-08-22), so there is nothing left to merge:
                // the assignment's document travels as it was stored and the
                // Runner performs the one merge that remains.
                Config = Projections.Opaque(assignment.Config),
            };
        }

        // **`MergeConfig` and `Deepen` were here, and there is nothing left
        // for them to do.** They laid the assignment's configuration over the
        // problem version's, in depth — a fix made on 2026-08-22 so that an
        // assignment narrowing a time limit stopped dropping the memory limit
        // beside it. The middle layer went the same week, and one layer needs no
        // merge.
        //
        // The deep merge itself is not lost: `Config::overlaid` in the Runner's
        // `aj-package` is the surviving half, and it is the half that was always
        // load-bearing, because it is what lays an assignment over a package.

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
            //
            // **But only for the Runner whose result it is.** The ownership term
            // is not a second lease check; it is what stops this branch being an
            // oracle. Without it any approved Runner naming a finished job and
            // any well-formed GUID was handed that job's result id and state,
            // having held nothing. A completed report leaves `RunnerId` where it
            // was — only the requeue on an infrastructure failure clears it — so
            // a genuine resend still lands here.
            //
            // **Guarding the branch rather than reordering the refusals**, which
            // was the first attempt and was wrong: a job the reaper had already
            // reclaimed has `RunnerId` of *nobody*, so an owner check in front
            // answered "that job belongs to another Runner" to the Runner whose
            // lease had simply run out — misleading, and against what §6 says
            // that case answers. The refusals below keep their order.
            if (job.Result is not null && job.RunnerId == runner.Id)
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

            // A document's ceiling, not the board's: this one travels with a
            // single result to a single reader, so the multiplication that sets
            // `extra`'s 2 kB does not apply to it.
            var props = Opaque.Store(report.Props, "props");

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

            // **Not final, and this is what decides it.** A Runner cannot tell
            // a broken package from a torn download, or a broken host from a bad
            // second, so the first infrastructure failure is a reason to try
            // again rather than a verdict on the submission. The delivery count
            // that bounds every other way a job comes back bounds this too.
            var again = report.InfrastructureFailure && job.Deliveries < MaxDeliveries;

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
                Props = props,
                RunnerVersion = report.RunnerVersion ?? runner.Version,
            };
            // **No result while it is going to be tried again.** One stored is
            // what makes a repeat answer `duplicate`, so a result kept from a
            // failed attempt would hand the next Runner's work back to it as a
            // duplicate of the failure.
            if (!again) context.Results.Add(result);

            // An infrastructure failure is not a wrong answer and must never be
            // scored as one.
            job.State = again
                ? EvaluationJobState.Queued
                : report.InfrastructureFailure ? EvaluationJobState.Failed : EvaluationJobState.Completed;
            job.FailureReason = report.InfrastructureFailure ? report.FailureReason : null;
            job.FinishedAt = again ? null : now;
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            job.LeaseSeconds = null;
            // Back to nobody's, as the reaper leaves it: the next claim is a
            // fresh delivery and this Runner has no more part in it.
            if (again)
            {
                job.RunnerId = null;
                job.ClaimedAt = null;
            }

            foreach (var attachment in report.Files ?? [])
            {
                if (!Guid.TryParse(attachment.FileId, out var fileId)) continue;
                if (!await IsOwnUploadAsync(runner, fileId, ct)) continue;

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
            Seen(runner, now);

            await context.SaveChangesAsync(ct);
            // **Finishing releases a sibling now, which it never used to.** The
            // rule was "only when it went back", because a job that finished is
            // not work for anybody and waking twelve Runners to say so is a
            // hundred and fifty pointless looks over a contest. That still
            // holds for the queue at large — but since a claim refuses a
            // submission whose sibling is `Running`, a rejudge queued behind
            // this attempt became claimable at exactly this moment, and nothing
            // else will say so. Sent only when there is such a sibling, so the
            // original reasoning survives for every ordinary report.
            if (again
                || await context.EvaluationJobs.AnyAsync(
                    other => other.SubmissionId == job.SubmissionId
                        && other.State == EvaluationJobState.Queued, ct))
            {
                queue.Wake();
            }
            await submissions.AnnounceAsync(job.SubmissionId, ct);

            return new ReportAcceptedDto
            {
                ResultId = again ? null : Wire.Id(result.Id),
                State = Projections.Wire(job.State),
                Duplicate = false,
            };
        }

        public async Task HeartbeatAsync(DbRunner runner, string? address, CancellationToken ct)
        {
            // **Not throttled, and it is the one that must not be.** A heartbeat
            // is a Runner saying it is alive and nothing else, so suppressing it
            // would skip the write for the only caller whose entire purpose is
            // this field. A minute apart it would clear the throttle anyway;
            // saying so here means a shorter interval keeps working.
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

            var (job, expires) = await ExtendAsync(runner, jobId, leaseToken, lease, ct);

            return new LeaseDto
            {
                JobId = Wire.Id(job.Id),
                LeaseToken = leaseToken,
                LeaseExpiresAt = Wire.At(expires),
            };
        }

        /// <summary>
        /// Undoes a handout whose answer never reached the Runner.
        /// <para>
        /// **Not a release, and deliberately not routed through one.** A release
        /// is a Runner saying it is stopping, and it spends one of three free
        /// ones; this is the Server noticing that the caller it committed a job
        /// to has gone before the answer could reach it. Nobody was stopped,
        /// nothing was tried, and the delivery is given back without counting —
        /// the same rule the reaper applies to a claim nobody was ever heard
        /// from about, arriving sooner because here the going was observed.
        /// </para>
        /// <para>
        /// **Refuses to touch a job anybody was heard from.** The check is not
        /// belt and braces: this runs after the request was seen to abort, and
        /// an abort is not proof the answer failed to arrive.
        /// </para>
        /// </summary>
        public async Task UnclaimAsync(Guid jobId, CancellationToken ct)
        {
            var job = await context.EvaluationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null
                || job.State != EvaluationJobState.Running
                || job.AcknowledgedAt is not null)
            {
                return;
            }

            // The token goes first, for the reason `LeaseReaper` gives.
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            job.LeaseSeconds = null;
            job.RunnerId = null;
            job.ClaimedAt = null;
            job.State = EvaluationJobState.Queued;
            if (job.Deliveries > 0) job.Deliveries -= 1;

            await context.SaveChangesAsync(ct);
            queue.Wake();
            await submissions.AnnounceAsync(job.SubmissionId, ct);
        }

        /// <summary>
        /// Gives a job back, because this Runner is stopping.
        /// <para>
        /// <b>The job is queued again at once and the delivery the claim counted
        /// is given back.</b> Being shut down is an operator's doing, and a
        /// participant's attempts are not the operator's to spend — so a fleet
        /// restarted during a contest costs the submissions in flight their
        /// place in the queue and nothing else.
        /// </para>
        /// <para>
        /// <b>It means only that.</b> Every other way a job comes back without a
        /// result is a report: a Runner that could not judge one has something
        /// to say about why, and one being stopped has not.
        /// </para>
        /// </summary>
        public async Task ReleaseAsync(
            DbRunner runner, Guid jobId, string leaseToken, CancellationToken ct)
        {
            var job = await HeldJobAsync(runner, jobId, leaseToken, ct);

            // The token goes first, for the reason `LeaseReaper` gives: a Runner
            // that wakes up and reports against the old lease is refused rather
            // than allowed to overwrite whoever has the job now.
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;
            job.LeaseSeconds = null;
            job.RunnerId = null;
            job.ClaimedAt = null;
            job.State = EvaluationJobState.Queued;

            if (job.Releases < FreeReleases)
            {
                job.Releases += 1;
                // Never below zero: the claim that counted it may have been a
                // delivery this job was already given back once.
                job.Deliveries = Math.Max(0, job.Deliveries - 1);
            }

            Seen(runner, clock.GetUtcNow().UtcDateTime);
            await context.SaveChangesAsync(ct);
            // **The whole point of the release.** A Runner stopping hands its
            // job back so somebody else takes it *at once*; without this the
            // job would sit until another Runner's wait ran out.
            queue.Wake();
            await submissions.AnnounceAsync(job.SubmissionId, ct);
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
            //
            // **By the job's own lease, not by this Server's default.** Passing
            // the default here meant a Runner that asked for eighty seconds, was
            // granted eighty and was told eighty, held six hundred the moment it
            // reported progress — which `Runner::take` in the UVa Runner does
            // immediately. Nothing said so on either side: the claim answer was
            // honest and the row disagreed with it a fraction of a second later.
            //
            // The effect was not a shortened lease — `Later` forbids that — but a
            // silently lengthened one, and a Runner computing its own timing
            // against a number the Server had already overridden.
            await ExtendAsync(runner, jobId, leaseToken, null, ct);
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
        /// <param name="lease">
        /// How far out to push the deadline, or <c>null</c> for the lease this
        /// job was granted at claim — falling back to the Server's default for a
        /// job claimed before that was recorded.
        /// </param>
        /// <returns>
        /// The job <b>and the deadline this call wrote</b>. The column is
        /// nullable and the value never is, but only this method can say so —
        /// <see cref="Later"/> returns a plain <c>DateTime</c>, and that
        /// knowledge does not survive the return type. Handing it back is what
        /// lets the caller answer without a <c>.Value</c> nobody can check.
        /// </returns>
        private async Task<(EvaluationJob Job, DateTime LeaseExpiresAt)> ExtendAsync(
            DbRunner runner, Guid jobId, string leaseToken, TimeSpan? lease, CancellationToken ct)
        {
            // Two attempts, not a loop without an end: the second read is against
            // a row somebody has just written, so a further conflict would mean
            // continuous contention on one job, and spinning through it would
            // hold a request open rather than answer it.
            for (var attempt = 0; ; attempt++)
            {
                var job = await HeldJobAsync(runner, jobId, leaseToken, ct);

                var by = lease
                    ?? (job.LeaseSeconds is { } granted
                        ? TimeSpan.FromSeconds(granted)
                        : DefaultLease);

                var expires = Later(job.LeaseExpiresAt, clock.GetUtcNow().UtcDateTime.Add(by));
                job.LeaseExpiresAt = expires;
                // The amplified one: every renewal and every progress note.
                Seen(runner, clock.GetUtcNow().UtcDateTime);

                try
                {
                    await context.SaveChangesAsync(ct);
                    return (job, expires);
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

            // **Here rather than at the claim, which is the whole point.** Every
            // lease-bearing call arrives through this method — report, release,
            // renew and progress — so this is the one place that can say a
            // Runner was heard from about a job, and it can only be reached by
            // one that received the lease token the claim's answer carried.
            // Written once and never moved, so it dates the first contact.
            job.AcknowledgedAt ??= clock.GetUtcNow().UtcDateTime;

            return job;
        }

        /// <summary>
        /// Whether this file is one <b>this</b> Runner uploaded and nothing owns
        /// yet — the only kind of file a Runner may name in an attachment.
        /// <para>
        /// <b>The three attach paths checked that the file existed, and nothing
        /// else.</b> Since this answers by reference and an attachment
        /// <i>creates</i> the reference, an approved Runner could name any file
        /// id in the installation and then read the bytes: another activity's
        /// problem package, another participant's <c>source</c>.
        /// </para>
        /// <para>
        /// <b>It then asked the wrong question for a year: "uploaded by
        /// nobody".</b> A Runner uploads with no session, so its files carried a
        /// null user — and so did every other Runner's. Absence is not identity,
        /// so one Runner could name another's fresh bytes and attach them as its
        /// own log. <see cref="DbFile.UploadedByRunnerId"/> exists to make the
        /// question answerable, and this is the only reader that needs it.
        /// </para>
        /// <para>
        /// <b>The second half stays, on its own reasoning.</b> One upload, one
        /// reference: without it a Runner could hang one blob off two attempts,
        /// or off an attempt and off itself at two scopes, and the collector's
        /// accounting is per name and per reference —
        /// <c>FileCollector</c> groups superseded references by
        /// <c>(RunnerId, Name)</c> and sweeps what nothing points at.
        /// </para>
        /// </summary>
        private async Task<bool> IsOwnUploadAsync(DbRunner runner, Guid fileId, CancellationToken ct) =>
            await context.Files.AnyAsync(
                f => f.Id == fileId && f.UploadedByRunnerId == runner.Id, ct)
            && !await context.FileReferences.AnyAsync(r => r.FileId == fileId, ct);

        /// <summary>
        /// A Runner reads what the jobs it is <b>currently holding</b> need, and
        /// nothing else.
        /// <para>
        /// Authorized against the job, not against being a Runner. Without that,
        /// any approved Runner could fetch every test package in the
        /// installation by asking for file ids — and the packages are the
        /// problems.
        /// </para>
        /// <para>
        /// It answers by <see cref="FileReference"/>, so what a Runner may attach
        /// decides what it may read. See <see cref="IsOwnUploadAsync"/>.
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
            if (!await IsOwnUploadAsync(runner, fileId, ct))
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

            // **The duplicate name is asked about first**, and the order matters:
            // attaching the same file twice under the same name is a Runner
            // repeating itself, and it must keep saying so. The check below would
            // otherwise answer it — the first attachment is what gives the file
            // the reference that check refuses — and a Runner would be told its
            // own log did not exist.
            var already = await context.FileReferences
                .AnyAsync(r => r.EvaluationJobId == job.Id && r.Name == name, ct);
            if (already)
            {
                throw new ConflictException(
                    $"This attempt already has a file called {name}", "attempt.file.duplicate");
            }

            if (!await IsOwnUploadAsync(runner, fileId, ct))
            {
                throw new ValidationException("That file is not stored", "file.missing");
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

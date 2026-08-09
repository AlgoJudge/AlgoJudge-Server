using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;
using DbRunner = AlgoJudge.Server.Database.Models.Runner;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Trial runs: a package somebody wants timed, attached to no problem.
    /// <para>
    /// Its own service for the same reason it has its own table (D-9). A trial
    /// produces timings rather than a verdict, so none of the submission
    /// machinery — scoring, attempt ceilings, rankings, results — applies, and
    /// sharing a service would mean each of those growing a branch that asks
    /// "but is this a trial?".
    /// </para>
    /// </summary>
    public interface ITrialService
    {
        Task<TrialDto> RequestAsync(string idOrSlug, string problemType, Guid packageFileId, CancellationToken ct);
        Task<TrialDto> GetAsync(string idOrSlug, Guid trialId, CancellationToken ct);
        Task<ClaimedTrialDto?> ClaimAsync(DbRunner runner, int? leaseSeconds, CancellationToken ct);
        Task<TrialReportAcceptedDto> ReportAsync(DbRunner runner, Guid trialId, TrialReportInputDto report, CancellationToken ct);
        Task<TrialLeaseDto> RenewAsync(DbRunner runner, Guid trialId, string leaseToken, int? leaseSeconds, CancellationToken ct);
        Task<bool> MayReadAsync(DbRunner runner, Guid fileId, CancellationToken ct);
        Task<int> ReapAsync(CancellationToken ct);
    }

    public class TrialService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IFileService files,
        TimeProvider clock
    ) : ITrialService
    {
        private static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan MaxLease = TimeSpan.FromHours(1);

        /// <summary>
        /// How many trials one person may have **unfinished** in one activity.
        /// <para>
        /// The ceiling exists because `trial:run` is grantable to participants,
        /// and a separate table keeps trials out of the queue that decides
        /// somebody's mark but **not** off the machines: a Runner claiming from
        /// both spends the same minutes either way. Thirty people each timing a
        /// slow program is thirty containers nobody is being marked for.
        /// </para>
        /// <para>
        /// Counted on the unfinished rather than on a lifetime total, so it
        /// clears itself and needs no reset. A constant rather than a column
        /// because it is a floor under abuse, not a policy anybody has asked to
        /// tune; the day somebody does, it becomes an activity setting and this
        /// comment is the argument for what the default should be.
        /// </para>
        /// </summary>
        public const int MaxUnfinishedPerUser = 3;

        public async Task<TrialDto> RequestAsync(
            string idOrSlug, string problemType, Guid packageFileId, CancellationToken ct)
        {
            var activity = await Resolve(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.TrialRun, activity.Id, ct);

            var userId = currentUser.UserId ?? throw new UnauthenticatedException();

            if (string.IsNullOrWhiteSpace(problemType))
            {
                throw new ValidationException("A problem type is required", "trial.type.missing");
            }

            // The bytes must exist and be the caller's own. A trial package
            // carries no `FileReference`, so the file service's own rule already
            // says only its uploader may read it — this is the same question
            // asked before anything is queued.
            if (!await files.CanReadAsync(packageFileId, ct))
            {
                throw new NotFoundException("Package file");
            }

            var unfinished = await context.Trials.CountAsync(
                t => t.ActivityId == activity.Id
                     && t.UserId == userId
                     && (t.State == EvaluationJobState.Queued || t.State == EvaluationJobState.Running),
                ct);
            if (unfinished >= MaxUnfinishedPerUser)
            {
                throw new ConflictException(
                    $"You already have {unfinished} trials waiting in this activity. "
                    + "Wait for one to finish before asking for another.",
                    "trial.tooMany");
            }

            var trial = new Trial
            {
                ActivityId = activity.Id,
                UserId = userId,
                PackageFileId = packageFileId,
                ProblemType = problemType.Trim(),
            };
            context.Trials.Add(trial);
            await context.SaveChangesAsync(ct);

            return ToDto(trial);
        }

        public async Task<TrialDto> GetAsync(string idOrSlug, Guid trialId, CancellationToken ct)
        {
            var activity = await Resolve(idOrSlug, ct);
            var userId = currentUser.UserId ?? throw new UnauthenticatedException();

            var trial = await context.Trials.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == trialId && t.ActivityId == activity.Id, ct)
                ?? throw new NotFoundException("Trial");

            // Whoever asked for it, or somebody who reads everybody's work in
            // this activity. A trial is a private measurement by default: it is
            // not an attempt, and showing one person's timings to another says
            // something about their solution they did not publish.
            if (trial.UserId != userId
                && !await permissions.HasAsync(Permissions.SubmissionReadAll, activity.Id, ct))
            {
                throw new NotFoundException("Trial");
            }

            return ToDto(trial);
        }

        public async Task<ClaimedTrialDto?> ClaimAsync(DbRunner runner, int? leaseSeconds, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var lease = leaseSeconds is { } seconds
                ? TimeSpan.FromSeconds(Math.Clamp(seconds, 60, MaxLease.TotalSeconds))
                : DefaultLease;

            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            // Same reservation as the job queue, on its own table: locked by id,
            // `SKIP LOCKED` so two Runners never take the same row, and the
            // entity loaded through EF afterwards so `xmin` comes with it.
            var types = (object)runner.ProblemTypes.ToArray();
            var claimedId = await context.Database
                .SqlQueryRaw<Guid>("""
                    SELECT t."Id" AS "Value" FROM "Trials" t
                    WHERE t."State" = 0
                      AND t."ProblemType" = ANY({0})
                      AND t."PackageFileId" IS NOT NULL
                    ORDER BY t."CreatedAt"
                    FOR UPDATE OF t SKIP LOCKED
                    LIMIT 1
                    """, types)
                .ToListAsync(ct);

            if (claimedId.Count == 0)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            var trial = await context.Trials.FirstAsync(t => t.Id == claimedId[0], ct);
            var package = await files.FindAsync(trial.PackageFileId!.Value, ct);
            if (package is null)
            {
                // The bytes went away between queueing and claiming. Nothing to
                // run, and nothing to blame the Runner for.
                trial.State = EvaluationJobState.Failed;
                trial.FailureReason = "The package was gone before the trial started";
                trial.FinishedAt = now;
                await context.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return null;
            }

            trial.State = EvaluationJobState.Running;
            trial.RunnerId = runner.Id;
            trial.LeaseToken = Uuid.New();
            trial.ClaimedAt = now;
            trial.LeaseExpiresAt = now.Add(lease);
            trial.Deliveries += 1;

            runner.LastSeenAt = now;

            await context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new ClaimedTrialDto
            {
                TrialId = Wire.Id(trial.Id),
                LeaseToken = Wire.Id(trial.LeaseToken!.Value),
                LeaseExpiresAt = Wire.At(trial.LeaseExpiresAt!.Value),
                ProblemType = trial.ProblemType,
                PackageFileId = Wire.Id(package.Id),
                PackageSha256 = package.Sha256,
            };
        }

        public async Task<TrialReportAcceptedDto> ReportAsync(
            DbRunner runner, Guid trialId, TrialReportInputDto report, CancellationToken ct)
        {
            var trial = await context.Trials.FirstOrDefaultAsync(t => t.Id == trialId, ct)
                ?? throw new NotFoundException("Trial");

            if (!Guid.TryParse(report.LeaseToken, out var presented))
            {
                throw new ValidationException("A lease token is required", "runner.lease.malformed");
            }

            // The repeat, checked before the lease: a Runner resending after its
            // lease expired is still telling the truth about what it measured.
            if (trial.FinishedAt is not null)
            {
                return new TrialReportAcceptedDto
                {
                    TrialId = Wire.Id(trial.Id),
                    State = Projections.Wire(trial.State),
                    Duplicate = true,
                };
            }

            if (trial.LeaseToken != presented)
            {
                throw new ForbiddenActionException(
                    "This lease is no longer held; the trial was reclaimed", "runner.lease.stale");
            }
            if (trial.RunnerId != runner.Id)
            {
                throw new ForbiddenActionException("That trial belongs to another Runner", "runner.lease.foreign");
            }
            if (trial.State != EvaluationJobState.Running)
            {
                throw new ConflictException(
                    $"A trial in state {Projections.Wire(trial.State)} takes no report", "trial.state");
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var failed = !string.IsNullOrWhiteSpace(report.FailureReason);

            trial.State = failed ? EvaluationJobState.Failed : EvaluationJobState.Completed;
            trial.FailureReason = failed ? report.FailureReason!.Trim() : null;
            trial.Measurement = failed ? null : report.Measurement;
            trial.FinishedAt = now;
            trial.LeaseToken = null;
            trial.LeaseExpiresAt = null;

            // **The package does not survive the trial** (D-12), successfully or
            // not. Cleared on the row first, so that a failure to remove the
            // bytes leaves an orphan rather than a trial pointing at a file that
            // may or may not be there.
            var packageId = trial.PackageFileId;
            trial.PackageFileId = null;

            runner.LastSeenAt = now;
            await context.SaveChangesAsync(ct);

            if (packageId is { } id) await files.DeleteUnreferencedAsync(id, ct);

            return new TrialReportAcceptedDto
            {
                TrialId = Wire.Id(trial.Id),
                State = Projections.Wire(trial.State),
                Duplicate = false,
            };
        }

        public async Task<TrialLeaseDto> RenewAsync(
            DbRunner runner, Guid trialId, string leaseToken, int? leaseSeconds, CancellationToken ct)
        {
            var trial = await context.Trials.FirstOrDefaultAsync(t => t.Id == trialId, ct)
                ?? throw new NotFoundException("Trial");

            if (!Guid.TryParse(leaseToken, out var presented) || trial.LeaseToken != presented)
            {
                throw new ForbiddenActionException(
                    "This lease is no longer held; the trial was reclaimed", "runner.lease.stale");
            }
            if (trial.RunnerId != runner.Id)
            {
                throw new ForbiddenActionException("That trial belongs to another Runner", "runner.lease.foreign");
            }
            if (trial.State != EvaluationJobState.Running)
            {
                throw new ConflictException(
                    $"A trial in state {Projections.Wire(trial.State)} has no lease", "trial.state");
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var lease = leaseSeconds is { } seconds
                ? TimeSpan.FromSeconds(Math.Clamp(seconds, 60, MaxLease.TotalSeconds))
                : DefaultLease;

            // **Never earlier than it already is.** A renewal that shortened a
            // lease would let a slow Runner hand its own work back mid-run — the
            // same defect that was found and fixed on the job queue.
            var wanted = now.Add(lease);
            trial.LeaseExpiresAt = trial.LeaseExpiresAt is { } held && held > wanted ? held : wanted;
            runner.LastSeenAt = now;
            await context.SaveChangesAsync(ct);

            return new TrialLeaseDto
            {
                TrialId = Wire.Id(trial.Id),
                LeaseToken = Wire.Id(presented),
                LeaseExpiresAt = Wire.At(trial.LeaseExpiresAt!.Value),
            };
        }

        public Task<bool> MayReadAsync(DbRunner runner, Guid fileId, CancellationToken ct) =>
            // Only the package of a trial this Runner is holding right now.
            // Nothing else, and never by probing ids: a Runner that has finished
            // or lost the lease can no longer read the bytes.
            context.Trials.AnyAsync(
                t => t.RunnerId == runner.Id
                     && t.State == EvaluationJobState.Running
                     && t.PackageFileId == fileId,
                ct);

        /// <summary>
        /// Returns trials whose lease has run out, and gives up on the ones that
        /// have been handed out too often.
        /// <para>
        /// The lease is what makes a trial survive a Runner that dies mid-run —
        /// the same mechanism as the job queue, and it has to be swept by
        /// somebody or an expired lease just sits there.
        /// </para>
        /// </summary>
        public async Task<int> ReapAsync(CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var expired = await context.Trials
                .Where(t => t.State == EvaluationJobState.Running
                            && t.LeaseExpiresAt != null
                            && t.LeaseExpiresAt < now)
                .ToListAsync(ct);

            foreach (var trial in expired)
            {
                trial.LeaseToken = null;
                trial.LeaseExpiresAt = null;
                trial.RunnerId = null;

                if (trial.Deliveries >= 5)
                {
                    // Handed out five times and never answered. Something about
                    // this package stops a Runner finishing, and returning it
                    // again would spend the machine on it for ever.
                    trial.State = EvaluationJobState.Failed;
                    trial.FailureReason = "No Runner finished this trial after five attempts";
                    trial.FinishedAt = now;

                    var packageId = trial.PackageFileId;
                    trial.PackageFileId = null;
                    await context.SaveChangesAsync(ct);
                    if (packageId is { } id) await files.DeleteUnreferencedAsync(id, ct);
                }
                else
                {
                    trial.State = EvaluationJobState.Queued;
                    trial.ClaimedAt = null;
                }
            }

            await context.SaveChangesAsync(ct);
            return expired.Count;
        }

        private async Task<Activity> Resolve(string idOrSlug, CancellationToken ct)
        {
            var activity = Guid.TryParse(idOrSlug, out var id)
                ? await context.Activities.FirstOrDefaultAsync(a => a.Id == id, ct)
                : await context.Activities.FirstOrDefaultAsync(a => a.Slug == idOrSlug, ct);
            return activity ?? throw new NotFoundException("Activity");
        }

        private static TrialDto ToDto(Trial trial) => new()
        {
            Id = Wire.Id(trial.Id),
            ActivityId = Wire.Id(trial.ActivityId),
            State = Projections.Wire(trial.State),
            ProblemType = trial.ProblemType,
            CreatedAt = Wire.At(trial.CreatedAt),
            FinishedAt = trial.FinishedAt is { } at ? Wire.At(at) : null,
            FailureReason = trial.FailureReason,
            Measurement = trial.Measurement,
            HasPackage = trial.PackageFileId is not null,
        };
    }
}

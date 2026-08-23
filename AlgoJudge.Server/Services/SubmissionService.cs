using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface ISubmissionService
    {
        Task<PageDto<SubmissionSummaryDto>> ListAsync(string activityIdOrSlug, PageQuery paging, CancellationToken ct);
        Task<SubmissionDetailDto> GetAsync(string activityIdOrSlug, Guid submissionId, CancellationToken ct);
        Task<SubmissionSummaryDto> SubmitAsync(
            string activityIdOrSlug, string problemSlug, string? props, StagedBytes staged,
            string fileName, string declaredSha256, CancellationToken ct);

        /// <summary>Tells everyone who may read it that a submission moved.</summary>
        Task AnnounceAsync(Guid submissionId, CancellationToken ct);
    }

    public class SubmissionService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IActivityService activities,
        IProblemService problems,
        IFileService files,
        ISeriesGate gate,
        IResultsService results,
        IEventHub events,
        IEventAudience audience,
        IRequestOrigin origin
    ) : ISubmissionService
    {
        public async Task<PageDto<SubmissionSummaryDto>> ListAsync(
            string activityIdOrSlug, PageQuery paging, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.SubmissionReadOwn, activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);

            var query = context.Submissions
                .Where(s => s.SeriesProblem!.ActivityId == activity.Id && s.UserId == user.Id);

            var total = await query.CountAsync(ct);
            var page = await query
                // Newest first, with the id as a tiebreaker so two submissions in
                // the same millisecond cannot swap between pages.
                .OrderByDescending(s => s.CreatedDate).ThenByDescending(s => s.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Problem)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .ToListAsync(ct);

            return new PageDto<SubmissionSummaryDto>
            {
                Items = page.Select(Summary).ToList(),
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        private static SubmissionSummaryDto Summary(Submission submission)
        {
            var assignment = submission.SeriesProblem!;
            // The newest attempt is what the submission currently says. Older
            // ones are history and stay readable, but they do not speak for it.
            var current = Scoring.Current(submission);
            // **Both numbers from one place.** Reading the score here and the
            // maximum somewhere else is how a raw attempt score ended up beside
            // a rescaled maximum on the same screen.
            var (score, maxScore) = Scoring.Reported(assignment, current?.Result);

            return new SubmissionSummaryDto
            {
                Id = Wire.Id(submission.Id),
                ProblemId = Wire.Id(assignment.Id),
                ProblemSlug = assignment.Slug,
                ProblemName = assignment.Name ?? assignment.Problem?.Name ?? assignment.Slug,
                SeriesId = Wire.Id(assignment.SeriesId),
                SubmittedAt = Wire.At(submission.CreatedDate),
                Props = Projections.Opaque(submission.Props),
                State = Projections.Wire(current?.State ?? EvaluationJobState.Queued),
                Score = score,
                MaxScore = maxScore,
                Verdict = current?.Result?.Verdict,
            };
        }

        public async Task<SubmissionDetailDto> GetAsync(
            string activityIdOrSlug, Guid submissionId, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            var user = await currentUser.RequireAsync(ct);

            var submission = await context.Submissions
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Problem)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .Include(s => s.Jobs).ThenInclude(j => j.Files).ThenInclude(f => f.File)
                .Include(s => s.Files).ThenInclude(f => f.File)
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.Id == submissionId && s.SeriesProblem!.ActivityId == activity.Id, ct)
                ?? throw new NotFoundException("Submission");

            var mine = submission.UserId == user.Id;
            if (!mine) await permissions.RequireAsync(Permissions.SubmissionReadAll, activity.Id, ct);
            else await permissions.RequireAsync(Permissions.SubmissionReadOwn, activity.Id, ct);

            var isManager = await permissions.HasAsync(Permissions.SubmissionSourceReadAll, activity.Id, ct);
            var rules = await context.AttachmentRules.AsNoTracking()
                .Where(r => r.ActivityId == activity.Id)
                .ToDictionaryAsync(r => r.Name, r => r.Visibility, ct);

            // Filtering happens where the data leaves. Doing it when the answer
            // is assembled — rather than when a fixture is built — is what makes
            // a manager changing the table change what yesterday's submissions
            // show.
            bool Readable(FileReference reference) =>
                isManager
                || (rules.TryGetValue(reference.Name, out var visibility)
                    && visibility == AttachmentVisibility.Participant);

            var summary = Summary(submission);
            return new SubmissionDetailDto
            {
                Id = summary.Id,
                ProblemId = summary.ProblemId,
                ProblemSlug = summary.ProblemSlug,
                ProblemName = summary.ProblemName,
                SeriesId = summary.SeriesId,
                SubmittedAt = summary.SubmittedAt,
                Props = summary.Props,
                State = summary.State,
                Score = summary.Score,
                MaxScore = summary.MaxScore,
                Verdict = summary.Verdict,
                ProblemType = submission.SeriesProblem!.Problem?.Type ?? "standard-io@1",
                AuthorName = submission.User is null ? submission.UserId : Projections.DisplayName(submission.User),
                Attempts = submission.Jobs
                    .OrderByDescending(j => j.Attempt)
                    .Select(job => new EvaluationAttemptDto
                    {
                        Id = Wire.Id(job.Id),
                        Attempt = job.Attempt,
                        StartedAt = Wire.At(job.ClaimedAt ?? job.CreatedAt),
                        FinishedAt = Wire.At(job.FinishedAt),
                        State = Projections.Wire(job.State),
                        Verdict = job.Result?.Verdict,
                        // **Rescaled, like the submission above it.** This was
                        // the Runner's raw number while the enclosing summary
                        // was the assignment's — 70 in the attempt list and 35
                        // in the header, on one screen, and neither expression
                        // looked wrong on its own.
                        Score = Scoring.Reported(submission.SeriesProblem!, job.Result).Score,
                        Props = Projections.Opaque(job.Result?.Props),
                        Files = job.Files.Where(Readable).Select(Projections.SubmissionFile).ToList(),
                    })
                    .ToList(),
                Files = submission.Files.Where(Readable).Select(Projections.SubmissionFile).ToList(),
            };
        }

        /// <summary>
        /// Accepts a solution, and queues exactly one job for it.
        /// <para>
        /// Every refusal here is the Server's, not the form's. The submit screen
        /// offers only the activity's languages and hides the button on a closed
        /// round, and none of that is a rule until this method says so.
        /// </para>
        /// </summary>
        public async Task<SubmissionSummaryDto> SubmitAsync(
            string activityIdOrSlug, string problemSlug, string? props, StagedBytes staged,
            string fileName, string declaredSha256, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.SubmissionCreate, activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);

            if (activity.ArchivedAt is not null)
            {
                throw new ConflictException("An archived activity accepts no submissions", "activity.archived");
            }

            var assignment = await context.SeriesProblems
                .Include(sp => sp.Series)
                .Include(sp => sp.Problem)
                .FirstOrDefaultAsync(sp => sp.ActivityId == activity.Id && sp.Slug == problemSlug, ct)
                ?? throw new NotFoundException("Problem");

            if (!gate.MayReadProblems(assignment.Series!, activity)) throw new NotFoundException("Problem");
            if (!gate.MaySubmit(assignment.Series!))
            {
                throw new ForbiddenActionException("This series is not accepting submissions", "series.closed");
            }

            // **The language check was here, and it is the Runner's now.**
            // The Server read `language` off the form and compared it against a
            // list on the activity. It cannot: the language is one member of an
            // opaque document, and a Server that reached into it would be
            // reading a problem type's vocabulary — the thing the whole opaque
            // arrangement exists to prevent.
            //
            // The allowed set lives in the assignment's `config` and travels with
            // the job, so the refusal happens where the catalogue is understood.
            // Nothing is lost: the check ran on a string the Server could not
            // validate the meaning of either.

            var ceiling = assignment.MaxSubmissions ?? activity.MaxSubmissionsPerProblem;
            if (ceiling is { } limit)
            {
                var used = await context.Submissions
                    .CountAsync(s => s.SeriesProblemId == assignment.Id && s.UserId == user.Id, ct);
                if (used >= limit)
                {
                    throw new ForbiddenActionException(
                        "No submissions left for this problem", "submission.limit");
                }
            }

            // **This used to check nothing.** `content.CanSeek` is false for a
            // body arriving over a socket, so the guard was skipped for every
            // real submission, and the only ceiling that ran was the one after
            // the bytes had already been stored. The staged size is what actually
            // arrived, and refusing here means the row is never written.
            var maxBytes = assignment.MaxUploadBytes ?? activity.MaxUploadBytes;
            if (staged.SizeBytes > maxBytes) throw new PayloadTooLargeException(maxBytes);

            // **The third of the three limits the Server exists to enforce, and
            // the one that was never enforced.** It was stored, projected,
            // editable in the panel and read by nothing — a promise the code did
            // not keep, against a specification that names the attachment count
            // as a column precisely *because* the Server polices it.
            //
            // A submission carries exactly one attachment today, so what this
            // rejects is an assignment configured to accept none. It is the wall
            // that becomes load-bearing the day the form carries more, and it
            // costs one comparison to have it standing already.
            var maxAttachments = assignment.MaxAttachments ?? activity.MaxAttachments;
            if (maxAttachments < 1)
            {
                throw new ForbiddenActionException(
                    "This problem accepts no attachments", "submission.attachments");
            }

            // **Checked before the bytes are committed, with the other
            // refusals.** Everything above this line refuses without leaving
            // anything behind, and the envelope check has to be on the same side
            // of that line: `CommitAsync` is the point of no return, and a
            // document refused after it would leave a blob nothing references.
            var declared = Opaque.StoreText(props, "props");

            var version = await ((ProblemService)problems).ResolveVersionAsync(assignment, ct)
                ?? throw new ConflictException("This problem has no published version", "problem.noVersion");

            var file = await files.CommitAsync(staged, fileName, "text/plain", declaredSha256, ct);

            var submission = new Submission
            {
                UserId = user.Id,
                SeriesProblemId = assignment.Id,
                Props = declared,
                // Where it came from, for a judge auditing the contest. Through
                // `IRequestOrigin`, so the address is un-mapped and the device
                // is a UUID or nothing — see `Submission.IpAddress`.
                IpAddress = origin.Address,
                SessionId = origin.SessionId,
                DeviceId = origin.DeviceId,
            };
            context.Submissions.Add(submission);

            context.FileReferences.Add(new FileReference
            {
                FileId = file.Id,
                OwnerKind = FileOwnerKind.Submission,
                SubmissionId = submission.Id,
                // Under a submission, participant scope means its author. The
                // activity's table then decides whether `source` reaches them.
                Scope = FileScope.Participant,
                Name = AttachmentNames.Source,
                // **No `Language` here.** This column is a BCP-47 subtag — it is
                // how a version keeps one statement per natural language — and a
                // programming language was being written into it, truncated at
                // sixteen characters. Two meanings of one word met in one line;
                // the column keeps the meaning it is documented with.
            });

            context.EvaluationJobs.Add(new EvaluationJob
            {
                SubmissionId = submission.Id,
                Attempt = 1,
                // Pinned here and copied onto the Result, so a later
                // republication cannot change what this was judged against.
                ProblemVersionId = version.Id,
                State = EvaluationJobState.Queued,
            });

            await context.SaveChangesAsync(ct);
            await AnnounceAsync(submission.Id, ct);

            var stored = await context.Submissions
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Problem)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .FirstAsync(s => s.Id == submission.Id, ct);
            return Summary(stored);
        }

        /// <summary>
        /// Sends `submissionStateChanged` to everybody entitled to it.
        /// <para>
        /// The author, and whoever may read every submission in the activity. The
        /// socket carries the <b>same</b> projection the endpoint would — it is
        /// not a second path to the data, and a screen must never learn from an
        /// event something the REST call would have withheld.
        /// </para>
        /// </summary>
        public async Task AnnounceAsync(Guid submissionId, CancellationToken ct)
        {
            var submission = await context.Submissions
                .AsNoTracking()
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Problem)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .FirstOrDefaultAsync(s => s.Id == submissionId, ct);
            if (submission?.SeriesProblem is null) return;

            var activityId = submission.SeriesProblem.ActivityId;
            var payload = new SubmissionStateChangedData
            {
                ActivityId = Wire.Id(activityId),
                Submission = Summary(submission),
            };

            var recipients = new HashSet<string> { submission.UserId };

            // Staff of this activity see every submission move. Resolved by the
            // shared audience rather than a query of its own: this one read only
            // the activity's `IsSystem` grants, so an administrator holding a
            // system grant and nothing else never heard about a submission they
            // are entitled to see.
            foreach (var reader in await audience.InActivityAsync(
                activityId, Permissions.SubmissionReadAll, ct))
            {
                recipients.Add(reader);
            }

            await events.SendToUsersAsync(recipients, EventTypes.SubmissionStateChanged, payload, ct);

            // And what it did to their standing on the problem, which is a
            // different fact on a different screen: the submissions list shows
            // the attempt, the problem list shows the best of them. Only the
            // author — a status is one reader's own.
            var assignment = submission.SeriesProblem;
            var mine = await context.Submissions
                .AsNoTracking()
                .Where(s => s.SeriesProblemId == assignment.Id && s.UserId == submission.UserId)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .ToListAsync(ct);

            // `Scoring` reads submissions, not the jobs under them: the best of
            // what somebody sent, and how many times they sent it.
            var (best, outOf) = Scoring.BestOf(mine);
            var maxPoints = Scoring.Scale(assignment, outOf);

            await events.SendToUserAsync(submission.UserId, EventTypes.ProblemStatusChanged, new
            {
                activityId = Wire.Id(activityId),
                problem = new ProblemSummaryDto
                {
                    Id = Wire.Id(assignment.Id),
                    Slug = assignment.Slug,
                    Name = assignment.Name ?? assignment.Problem?.Name ?? assignment.Slug,
                    Status = Scoring.Status(mine, best),
                    BestScore = Scoring.Rescale(best, maxPoints),
                    MaxScore = mine.Count == 0 ? null : maxPoints,
                    Attempts = mine.Count,
                },
            }, ct);

            // And the board, for everybody entitled to it — each getting exactly
            // what `GET /results` would have given them, because the socket is
            // not a second path to the data.
            foreach (var (userId, result) in await results.BroadcastAsync(submissionId, ct))
            {
                await events.SendToUserAsync(userId, EventTypes.RankingChanged, new RankingChangedData
                {
                    ActivityId = Wire.Id(activityId),
                    Change = "result",
                    SeriesId = Wire.Id(submission.SeriesProblem.SeriesId),
                    Result = result,
                }, ct);
            }
        }
    }
}

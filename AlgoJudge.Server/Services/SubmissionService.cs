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
            string activityIdOrSlug, string problemSlug, string? language, Stream content,
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
        IEventHub events
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
            var maxPoints = Scoring.MaxPoints(assignment);

            return new SubmissionSummaryDto
            {
                Id = Wire.Id(submission.Id),
                ProblemId = Wire.Id(assignment.Id),
                ProblemSlug = assignment.Slug,
                ProblemName = assignment.Name ?? assignment.Problem?.Name ?? assignment.Slug,
                SeriesId = Wire.Id(assignment.SeriesId),
                SubmittedAt = Wire.At(submission.CreatedDate),
                Language = submission.Language,
                State = Projections.Wire(current?.State ?? EvaluationJobState.Queued),
                Score = Scoring.Rescale(current?.Result?.Score, maxPoints),
                MaxScore = current?.Result?.Score is null ? null : maxPoints,
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
                Language = summary.Language,
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
                        Score = job.Result?.Score,
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
            string activityIdOrSlug, string problemSlug, string? language, Stream content,
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

            if (!string.IsNullOrWhiteSpace(language)
                && activity.Languages.Count > 0
                && !activity.Languages.Contains(language))
            {
                throw new ForbiddenActionException(
                    $"This activity does not accept {language}", "submission.language");
            }

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

            var maxBytes = assignment.MaxUploadBytes ?? activity.MaxUploadBytes;
            if (content.CanSeek && content.Length > maxBytes) throw new PayloadTooLargeException(maxBytes);

            var version = await ((ProblemService)problems).ResolveVersionAsync(assignment, ct)
                ?? throw new ConflictException("This problem has no published version", "problem.noVersion");

            var file = await files.StoreAsync(content, fileName, "text/plain", declaredSha256, ct);
            if (file.SizeBytes > maxBytes) throw new PayloadTooLargeException(maxBytes);

            var submission = new Submission
            {
                UserId = user.Id,
                SeriesProblemId = assignment.Id,
                Language = language,
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
                Language = language,
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

            // Staff of this activity see every submission move. Read from the
            // grants, and only those that actually carry the permission — a
            // manager whose right to read submissions was taken away is a manager
            // this event does not reach.
            var staff = await context.Grants.AsNoTracking()
                .Where(g => g.ActivityId == activityId && g.IsSystem && g.State == GrantState.Active)
                .Select(g => new { g.UserId, g.Permissions })
                .ToListAsync(ct);

            foreach (var grant in staff)
            {
                if (grant.Permissions.Contains(Permissions.SubmissionReadAll, StringComparison.Ordinal))
                {
                    recipients.Add(grant.UserId);
                }
            }

            await events.SendToUsersAsync(recipients, EventTypes.SubmissionStateChanged, payload, ct);
        }
    }
}

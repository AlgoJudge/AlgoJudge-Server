using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;
using DbRunner = AlgoJudge.Server.Database.Models.Runner;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Submissions, questions and Runners as a manager sees them: across
    /// activities, with the author's name, and without the attachment filtering
    /// a participant's view applies.
    /// </summary>
    public interface IManagerReadService
    {
        Task<PageDto<ManagedSubmissionDto>> ListSubmissionsAsync(
            PageQuery paging, Guid? activityId, Guid? seriesId, Guid? assignmentId,
            string? userId, string? state, string? verdict, string? search, CancellationToken ct);
        Task<ManagedSubmissionDetailDto> GetSubmissionAsync(Guid id, CancellationToken ct);
        Task<ManagedSubmissionDto> RejudgeAsync(Guid submissionId, CancellationToken ct);
        Task<int> RejudgeAssignmentAsync(Guid assignmentId, CancellationToken ct);
        Task<int> RejudgeSeriesAsync(Guid seriesId, CancellationToken ct);
        Task<ManagedSubmissionDetailDto> CancelAttemptAsync(Guid submissionId, Guid attemptId, CancellationToken ct);

        /// <summary>Rules that a submission counts towards no standing, or lifts it.</summary>
        Task<ManagedSubmissionDetailDto> SetExcludedAsync(
            Guid submissionId, bool excluded, string? reason, CancellationToken ct);

        Task<PageDto<ManagedQuestionDto>> ListQuestionsAsync(
            PageQuery paging, Guid? activityId, Guid? seriesId, string? kind,
            bool unansweredOnly, string? search, CancellationToken ct);
        Task<ManagedQuestionDto> AnswerAsync(Guid id, AnswerInputDto input, CancellationToken ct);
        Task<ManagedQuestionDto> SetPublishedAsync(Guid id, bool published, CancellationToken ct);
        Task<ManagedQuestionDto> AnnounceAsync(string activityIdOrSlug, AnnouncementInputDto input, CancellationToken ct);
        Task DeleteAnnouncementAsync(Guid id, CancellationToken ct);

        Task<PageDto<ManagedRunnerDto>> ListRunnersAsync(
            PageQuery paging, string? state, string? search, CancellationToken ct);
        Task<ManagedRunnerDto> ApproveRunnerAsync(Guid id, CancellationToken ct);
        Task<ManagedRunnerDto> RevokeRunnerAsync(Guid id, string? reason, CancellationToken ct);
        Task<ManagedRunnerDto> SetTagsAsync(Guid id, IReadOnlyList<string> tags, CancellationToken ct);
        Task ForgetRunnerAsync(Guid id, CancellationToken ct);
    }

    public class ManagerReadService(
        ApplicationDbContext context,
        IPermissionService permissions,
        ICurrentUserService currentUser,
        IActivityService activities,
        ISubmissionService submissions,
        IEventHub events,
        IEventAudience audience,
        TimeProvider clock
    ) : IManagerReadService
    {
        /// <summary>
        /// Tells whoever watches the Runners that one changed.
        /// <para>
        /// Runners belong to the installation rather than to an activity, so the
        /// audience is everybody holding <c>runner:read</c> anywhere. A Runner
        /// going down is the clearest case for this: nobody caused it, so nobody
        /// is looking at a screen they just acted on.
        /// </para>
        /// </summary>
        private async Task AnnounceRunnerAsync(
            ManagedRunnerDto? runner, string? deletedId, CancellationToken ct)
        {
            var readers = await audience.AnywhereAsync(Permissions.RunnerRead, ct);
            if (readers.Count == 0) return;

            await events.SendToUsersAsync(readers, EventTypes.RunnerChanged,
                deletedId is null ? new { runner } : new { deletedId }, ct);
        }

        /// <summary>
        /// The manager's view of a submission. Distinct from
        /// <c>submissionStateChanged</c>, which is the author's own.
        /// </summary>
        private async Task AnnounceSubmissionAsync(
            Guid activityId, ManagedSubmissionDetailDto submission, CancellationToken ct)
        {
            var readers = await audience.InActivityAsync(activityId, Permissions.SubmissionReadAll, ct);
            if (readers.Count == 0) return;

            await events.SendToUsersAsync(
                readers, EventTypes.SubmissionChanged, new { submission }, ct);
        }

        /// <summary>
        /// The manager's view of a question, beside the participant's own
        /// announcement of it.
        /// </summary>
        private async Task AnnounceManagedQuestionAsync(
            Guid activityId, ManagedQuestionDto? question, string? deletedId, CancellationToken ct)
        {
            var readers = await audience.InActivityAsync(activityId, Permissions.QuestionReadAll, ct);
            if (readers.Count == 0) return;

            await events.SendToUsersAsync(readers, EventTypes.QuestionChanged,
                deletedId is null ? new { question } : new { deletedId }, ct);
        }
        // ── submissions ─────────────────────────────────────────────────────

        public async Task<PageDto<ManagedSubmissionDto>> ListSubmissionsAsync(
            PageQuery paging, Guid? activityId, Guid? seriesId, Guid? assignmentId,
            string? userId, string? state, string? verdict, string? search, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.SubmissionReadAll, activityId, ct);

            var query = context.Submissions
                .AsNoTracking()
                .Include(s => s.User)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Problem)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Series)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Activity)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .AsQueryable();

            // Without an activity, the answer is narrowed to the activities the
            // caller may read submissions in. `submission:read:all` is scoped,
            // and a manager of one course must not see another's.
            if (activityId is { } scoped)
            {
                query = query.Where(s => s.SeriesProblem!.ActivityId == scoped);
            }
            else
            {
                var allowed = await permissions.ActivitiesWithAsync(Permissions.SubmissionReadAll, ct);
                if (allowed is not null)
                {
                    var ids = allowed.ToHashSet();
                    query = query.Where(s => ids.Contains(s.SeriesProblem!.ActivityId));
                }
            }

            if (seriesId is { } series) query = query.Where(s => s.SeriesProblem!.SeriesId == series);
            if (assignmentId is { } assignment) query = query.Where(s => s.SeriesProblemId == assignment);

            if (userId is not null)
            {
                // A filter naming somebody is an authorization surface, not a
                // convenience: it is already inside `submission:read:all`, so
                // the narrowing above is what makes it safe.
                query = query.Where(s => s.UserId == userId);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLower();
                query = query.Where(s =>
                    s.SeriesProblem!.Slug.ToLower().Contains(needle)
                    || s.User!.UserName!.ToLower().Contains(needle));
            }

            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(s => s.CreatedDate).ThenByDescending(s => s.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            // State and verdict live on the newest job, which EF cannot filter
            // on without loading it — so they are applied here, after paging,
            // and the count above is of what matched the rest.
            var items = page.Select(Project).ToList();
            if (state is not null) items = items.Where(s => s.State == state).ToList();
            if (verdict is not null) items = items.Where(s => s.Verdict == verdict).ToList();

            return new PageDto<ManagedSubmissionDto>
            {
                Items = items, Total = total, Page = paging.Page, PageSize = paging.PageSize,
            };
        }

        private static ManagedSubmissionDto Project(Submission submission)
        {
            var assignment = submission.SeriesProblem!;
            var current = Scoring.Current(submission);
            var (score, maxScore) = Scoring.Reported(assignment, current?.Result);

            return new ManagedSubmissionDto
            {
                Id = Wire.Id(submission.Id),
                ActivityId = Wire.Id(assignment.ActivityId),
                ActivitySlug = assignment.Activity?.Slug ?? "",
                SeriesId = Wire.Id(assignment.SeriesId),
                SeriesName = assignment.Series?.Name ?? "",
                SeriesProblemId = Wire.Id(assignment.Id),
                ProblemSlug = assignment.Slug,
                ProblemName = assignment.Name ?? assignment.Problem?.Name ?? assignment.Slug,
                UserId = submission.UserId,
                UserName = submission.User is null
                    ? submission.UserId
                    : Projections.DisplayName(submission.User),
                SubmittedAt = Wire.At(submission.CreatedDate),
                Props = Projections.Opaque(submission.Props),
                State = Projections.Wire(current?.State ?? EvaluationJobState.Queued),
                Verdict = current?.Result?.Verdict,
                Score = score,
                MaxScore = maxScore,
                Attempts = submission.Jobs.Count,
                Excluded = submission.ExcludedAt is not null,
            };
        }

        public async Task<ManagedSubmissionDetailDto> GetSubmissionAsync(Guid id, CancellationToken ct)
        {
            var submission = await LoadSubmissionAsync(id, ct);
            await permissions.RequireAsync(
                Permissions.SubmissionReadAll, submission.SeriesProblem!.ActivityId, ct);

            var summary = Project(submission);

            // Who ruled, by name. A lookup rather than a navigation property:
            // the column is a bare identifier on purpose.
            var ruler = submission.ExcludedByUserId is { } excludedBy
                ? await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == excludedBy, ct)
                : null;

            return new ManagedSubmissionDetailDto
            {
                Id = summary.Id,
                ActivityId = summary.ActivityId,
                ActivitySlug = summary.ActivitySlug,
                SeriesId = summary.SeriesId,
                SeriesName = summary.SeriesName,
                SeriesProblemId = summary.SeriesProblemId,
                ProblemSlug = summary.ProblemSlug,
                ProblemName = summary.ProblemName,
                UserId = summary.UserId,
                UserName = summary.UserName,
                SubmittedAt = summary.SubmittedAt,
                Props = summary.Props,
                State = summary.State,
                Verdict = summary.Verdict,
                Score = summary.Score,
                MaxScore = summary.MaxScore,
                Attempts = summary.Attempts,
                Excluded = summary.Excluded,
                ExcludedAt = Wire.At(submission.ExcludedAt),
                ExcludedBy = ruler is null
                    ? submission.ExcludedByUserId
                    : Projections.DisplayName(ruler),
                ExclusionReason = submission.ExclusionReason,
                ProblemType = submission.SeriesProblem.Problem?.Type ?? "standard-io@1",
                IpAddress = submission.IpAddress?.ToString(),
                SessionId = submission.SessionId is { } session ? Wire.Id(session) : null,
                DeviceId = submission.DeviceId is { } device ? Wire.Id(device) : null,
                AttemptList = submission.Jobs
                    .OrderByDescending(j => j.Attempt)
                    .Select(job => new ManagedAttemptDto
                    {
                        Id = Wire.Id(job.Id),
                        Attempt = job.Attempt,
                        State = Projections.Wire(job.State),
                        StartedAt = Wire.At(job.ClaimedAt ?? job.CreatedAt),
                        FinishedAt = Wire.At(job.FinishedAt),
                        RunnerName = job.Runner?.Name,
                        // A manager sees every attachment, whatever the
                        // activity's visibility table says — that table is about
                        // what reaches a participant.
                        Files = job.Files.Select(Projections.SubmissionFile).ToList(),
                    })
                    .ToList(),
                Files = submission.Files.Select(Projections.SubmissionFile).ToList(),
            };
        }

        private async Task<Submission> LoadSubmissionAsync(Guid id, CancellationToken ct) =>
            await context.Submissions
                .AsNoTracking()
                .Include(s => s.User)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Problem)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Series)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Activity)
                .Include(s => s.Files).ThenInclude(f => f.File)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .Include(s => s.Jobs).ThenInclude(j => j.Runner)
                .Include(s => s.Jobs).ThenInclude(j => j.Files).ThenInclude(f => f.File)
                .FirstOrDefaultAsync(s => s.Id == id, ct)
                ?? throw new NotFoundException("Submission");

        /// <summary>
        /// A rejudge <b>adds an attempt</b> and never overwrites a result. The
        /// previous ones stay, because "what did it say before" is a question
        /// somebody asks when a rejudge changes an outcome.
        /// </summary>
        public async Task<ManagedSubmissionDto> RejudgeAsync(Guid submissionId, CancellationToken ct)
        {
            var submission = await context.Submissions
                .Include(s => s.SeriesProblem)
                .Include(s => s.Jobs)
                .FirstOrDefaultAsync(s => s.Id == submissionId, ct)
                ?? throw new NotFoundException("Submission");

            await permissions.RequireAsync(
                Permissions.SubmissionRejudge, submission.SeriesProblem!.ActivityId, ct);

            await QueueRejudgeAsync(submission, ct);
            await context.SaveChangesAsync(ct);
            await submissions.AnnounceAsync(submissionId, ct);

            return Project(await LoadSubmissionAsync(submissionId, ct));
        }

        private async Task QueueRejudgeAsync(Submission submission, CancellationToken ct)
        {
            var assignment = submission.SeriesProblem
                ?? await context.SeriesProblems.FirstAsync(sp => sp.Id == submission.SeriesProblemId, ct);

            // Judged against what the assignment points at now — which is the
            // point of a rejudge: a corrected package, or a version the manager
            // has since pinned.
            var versionId = assignment.PinnedProblemVersionId
                ?? await context.ProblemVersions
                    .Where(v => v.ProblemId == assignment.ProblemId)
                    .OrderByDescending(v => v.Version)
                    .Select(v => (Guid?)v.Id)
                    .FirstOrDefaultAsync(ct)
                ?? throw new ConflictException("This problem has no published version", "problem.noVersion");

            var attempts = submission.Jobs.Count > 0
                ? submission.Jobs.Max(j => j.Attempt)
                : await context.EvaluationJobs
                    .Where(j => j.SubmissionId == submission.Id)
                    .Select(j => (int?)j.Attempt)
                    .MaxAsync(ct) ?? 0;

            context.EvaluationJobs.Add(new EvaluationJob
            {
                SubmissionId = submission.Id,
                Attempt = attempts + 1,
                ProblemVersionId = versionId,
                State = EvaluationJobState.Queued,
            });
        }

        public async Task<int> RejudgeAssignmentAsync(Guid assignmentId, CancellationToken ct)
        {
            var assignment = await context.SeriesProblems
                .FirstOrDefaultAsync(sp => sp.Id == assignmentId, ct)
                ?? throw new NotFoundException("Assignment");

            await permissions.RequireAsync(Permissions.SubmissionRejudge, assignment.ActivityId, ct);
            return await RejudgeManyAsync(s => s.SeriesProblemId == assignmentId, ct);
        }

        public async Task<int> RejudgeSeriesAsync(Guid seriesId, CancellationToken ct)
        {
            var round = await context.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct)
                ?? throw new NotFoundException("Series");

            await permissions.RequireAsync(Permissions.SubmissionRejudge, round.ActivityId, ct);
            return await RejudgeManyAsync(s => s.SeriesProblem!.SeriesId == seriesId, ct);
        }

        private async Task<int> RejudgeManyAsync(
            System.Linq.Expressions.Expression<Func<Submission, bool>> which, CancellationToken ct)
        {
            var affected = await context.Submissions
                .Include(s => s.SeriesProblem)
                .Include(s => s.Jobs)
                .Where(which)
                .ToListAsync(ct);

            foreach (var submission in affected) await QueueRejudgeAsync(submission, ct);
            await context.SaveChangesAsync(ct);

            foreach (var submission in affected) await submissions.AnnounceAsync(submission.Id, ct);
            return affected.Count;
        }

        /// <summary>
        /// Stops a job that has not finished. <b>A finished one is history</b>
        /// and is refused: cancelling it would mean deciding that a verdict
        /// somebody already saw did not happen.
        /// </summary>
        public async Task<ManagedSubmissionDetailDto> CancelAttemptAsync(
            Guid submissionId, Guid attemptId, CancellationToken ct)
        {
            var job = await context.EvaluationJobs
                .Include(j => j.Submission)!.ThenInclude(s => s!.SeriesProblem)
                .FirstOrDefaultAsync(j => j.Id == attemptId && j.SubmissionId == submissionId, ct)
                ?? throw new NotFoundException("Attempt");

            await permissions.RequireAsync(
                Permissions.SubmissionCancel, job.Submission!.SeriesProblem!.ActivityId, ct);

            if (job.State is EvaluationJobState.Completed or EvaluationJobState.Failed
                or EvaluationJobState.Cancelled)
            {
                throw new ConflictException(
                    "This attempt has already finished and cannot be cancelled", "attempt.finished");
            }

            job.State = EvaluationJobState.Cancelled;
            job.FinishedAt = clock.GetUtcNow().UtcDateTime;
            // The lease goes with it, so a Runner still holding it is refused
            // when it reports rather than allowed to resurrect the job.
            job.LeaseToken = null;
            job.LeaseExpiresAt = null;

            await context.SaveChangesAsync(ct);
            // Two audiences, two facts: the author is told their submission
            // stopped, and whoever watches the activity's submissions is told
            // the row changed.
            await submissions.AnnounceAsync(submissionId, ct);
            var detail = await GetSubmissionAsync(submissionId, ct);
            await AnnounceSubmissionAsync(job.Submission!.SeriesProblem!.ActivityId, detail, ct);
            return detail;
        }

        /// <summary>
        /// A manager's ruling that a submission counts towards no standing.
        /// <para>
        /// <b>It retracts nothing</b>: the verdict, the attempts, the files, the
        /// place in every list and the ceiling it spent all stay. What it leaves
        /// is every reader that computes a standing.
        /// </para>
        /// </summary>
        public async Task<ManagedSubmissionDetailDto> SetExcludedAsync(
            Guid submissionId, bool excluded, string? reason, CancellationToken ct)
        {
            var submission = await context.Submissions
                .Include(s => s.SeriesProblem)
                .FirstOrDefaultAsync(s => s.Id == submissionId, ct)
                ?? throw new NotFoundException("Submission");

            var activityId = submission.SeriesProblem!.ActivityId;
            await permissions.RequireAsync(Permissions.SubmissionExclude, activityId, ct);

            // All three together, both ways: a cleared timestamp beside a kept
            // reason answers "is it excluded" and "why" with two states.
            submission.ExcludedAt = excluded ? clock.GetUtcNow().UtcDateTime : null;
            submission.ExcludedByUserId = excluded ? currentUser.UserId : null;
            submission.ExclusionReason = excluded
                ? (string.IsNullOrWhiteSpace(reason) ? null : reason.Trim())
                : null;

            await context.SaveChangesAsync(ct);

            // The author's screens: the submission row, and their standing on the
            // problem, which a submission has just left.
            await submissions.AnnounceAsync(submissionId, ct);

            var detail = await GetSubmissionAsync(submissionId, ct);
            await AnnounceSubmissionAsync(activityId, detail, ct);

            // **And every open board**, which the ordinary result push cannot
            // do: the Client merges by id and no merge removes a row. So the
            // change travels alone and each reader refetches.
            var watching = await audience.InActivityAsync(activityId, Permissions.RankingRead, ct);
            if (watching.Count > 0)
            {
                await events.SendToUsersAsync(watching, EventTypes.RankingChanged, new RankingChangedData
                {
                    ActivityId = Wire.Id(activityId),
                    Change = "excluded",
                    SeriesId = Wire.Id(submission.SeriesProblem.SeriesId),
                }, ct);
            }

            return detail;
        }

        // ── questions ───────────────────────────────────────────────────────

        public async Task<PageDto<ManagedQuestionDto>> ListQuestionsAsync(
            PageQuery paging, Guid? activityId, Guid? seriesId, string? kind,
            bool unansweredOnly, string? search, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.QuestionReadAll, activityId, ct);

            var query = context.Questions
                .AsNoTracking()
                .Include(q => q.Author)
                .Include(q => q.Activity)
                .Include(q => q.Series)
                .Include(q => q.SeriesProblem).ThenInclude(sp => sp!.Problem)
                .AsQueryable();

            if (activityId is { } scoped)
            {
                query = query.Where(q => q.ActivityId == scoped);
            }
            else
            {
                var allowed = await permissions.ActivitiesWithAsync(Permissions.QuestionReadAll, ct);
                if (allowed is not null)
                {
                    var ids = allowed.ToHashSet();
                    query = query.Where(q => ids.Contains(q.ActivityId));
                }
            }

            if (seriesId is { } series) query = query.Where(q => q.SeriesId == series);
            if (kind is "question") query = query.Where(q => q.Kind == QuestionKind.Question);
            if (kind is "announcement") query = query.Where(q => q.Kind == QuestionKind.Announcement);
            if (unansweredOnly)
            {
                query = query.Where(q => q.Kind == QuestionKind.Question && q.AnswerBody == null);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLower();
                query = query.Where(q => q.Topic.ToLower().Contains(needle) || q.Body.ToLower().Contains(needle));
            }

            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(q => q.CreatedAt).ThenByDescending(q => q.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            var items = new List<ManagedQuestionDto>(page.Count);
            foreach (var question in page) items.Add(await ProjectQuestionAsync(question, ct));

            return new PageDto<ManagedQuestionDto>
            {
                Items = items, Total = total, Page = paging.Page, PageSize = paging.PageSize,
            };
        }

        private async Task<ManagedQuestionDto> ProjectQuestionAsync(Question question, CancellationToken ct)
        {
            var reads = await context.QuestionReads.CountAsync(r => r.QuestionId == question.Id, ct);
            string? answerAuthor = null;
            if (question.AnswerAuthorUserId is { } id)
            {
                var author = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
                answerAuthor = author is null ? id : Projections.DisplayName(author);
            }

            return new ManagedQuestionDto
            {
                Id = Wire.Id(question.Id),
                ActivityId = Wire.Id(question.ActivityId),
                ActivitySlug = question.Activity?.Slug ?? "",
                Kind = question.Kind == QuestionKind.Announcement ? "announcement" : "question",
                Topic = question.Topic,
                Body = question.Body,
                AuthorUserId = question.Kind == QuestionKind.Announcement ? null : question.AuthorUserId,
                AuthorName = question.Kind == QuestionKind.Announcement
                    ? null
                    : question.Author is null ? question.AuthorUserId : Projections.DisplayName(question.Author),
                CreatedAt = Wire.At(question.CreatedAt),
                SeriesId = question.SeriesId is { } s ? Wire.Id(s) : null,
                SeriesName = question.Series?.Name,
                SeriesProblemId = question.SeriesProblemId is { } p ? Wire.Id(p) : null,
                ProblemSlug = question.SeriesProblem?.Slug,
                ProblemName = question.SeriesProblem?.Name ?? question.SeriesProblem?.Problem?.Name,
                Answer = question.AnswerBody is null ? null : new QuestionAnswerDto
                {
                    Body = question.AnswerBody,
                    AuthorName = answerAuthor ?? "",
                    AnsweredAt = Wire.At(question.AnsweredAt ?? question.CreatedAt),
                },
                IsPublished = question.IsPublished,
                ReadCount = reads,
            };
        }

        public async Task<ManagedQuestionDto> AnswerAsync(Guid id, AnswerInputDto input, CancellationToken ct)
        {
            var question = await LoadQuestionAsync(id, ct);
            await permissions.RequireAsync(Permissions.QuestionAnswer, question.ActivityId, ct);
            var author = await currentUser.RequireAsync(ct);

            if (question.Kind == QuestionKind.Announcement)
            {
                throw new ConflictException("An announcement has no question to answer", "question.isAnnouncement");
            }

            var body = input.Body?.Trim() ?? "";
            if (body.Length == 0) throw new ValidationException("An answer is required", "answer.body.required");

            question.AnswerBody = body;
            question.AnswerAuthorUserId = author.Id;
            question.AnsweredAt = clock.GetUtcNow().UtcDateTime;
            // Answering leaves it unpublished unless the input says otherwise:
            // publishing sends it to everybody, and that is a second decision.
            if (input.Publish == true) question.IsPublished = true;

            await context.SaveChangesAsync(ct);
            await AnnounceQuestionAsync(question, ct);
            return await ProjectQuestionAsync(await LoadQuestionAsync(id, ct), ct);
        }

        public async Task<ManagedQuestionDto> SetPublishedAsync(Guid id, bool published, CancellationToken ct)
        {
            var question = await LoadQuestionAsync(id, ct);
            await permissions.RequireAsync(Permissions.QuestionPublish, question.ActivityId, ct);

            if (published && question.Kind == QuestionKind.Question && question.AnswerBody is null)
            {
                throw new ConflictException("Answer it before publishing it", "question.unanswered");
            }

            question.IsPublished = published;
            await context.SaveChangesAsync(ct);
            if (published) await AnnounceQuestionAsync(question, ct);
            var projectedQuestion = await ProjectQuestionAsync(await LoadQuestionAsync(id, ct), ct);
            await AnnounceManagedQuestionAsync(question.ActivityId, projectedQuestion, null, ct);
            return projectedQuestion;
        }

        /// <summary>
        /// Tells the activity's participants. Everybody may read a published
        /// question; an unpublished answer reaches only the person who asked.
        /// </summary>
        private async Task AnnounceQuestionAsync(Question question, CancellationToken ct)
        {
            var recipients = question.IsPublished
                ? await context.Grants.AsNoTracking()
                    .Where(g => g.ActivityId == question.ActivityId && g.State == GrantState.Active)
                    .Select(g => g.UserId)
                    .ToListAsync(ct)
                : [question.AuthorUserId];

            var type = question.Kind == QuestionKind.Announcement
                ? EventTypes.AnnouncementPublished
                : question.AnswerBody is not null
                    ? EventTypes.QuestionAnswered
                    : EventTypes.QuestionPublished;

            await events.SendToUsersAsync(recipients, type, new
            {
                activityId = Wire.Id(question.ActivityId),
                question = await ProjectQuestionAsync(question, ct),
            }, ct);
        }

        public async Task<ManagedQuestionDto> AnnounceAsync(
            string activityIdOrSlug, AnnouncementInputDto input, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.AnnouncementCreate, activity.Id, ct);
            var author = await currentUser.RequireAsync(ct);

            Guid? seriesId = null;
            if (input.SeriesId is { } raw && Guid.TryParse(raw, out var parsed))
            {
                var exists = await context.Series.AnyAsync(s => s.Id == parsed && s.ActivityId == activity.Id, ct);
                if (!exists) throw new NotFoundException("Series");
                seriesId = parsed;
            }

            var announcement = new Question
            {
                ActivityId = activity.Id,
                SeriesId = seriesId,
                Kind = QuestionKind.Announcement,
                Topic = input.Topic?.Trim() ?? "",
                Body = input.Body?.Trim() ?? "",
                AuthorUserId = author.Id,
                // An announcement is published by definition — nobody asked it,
                // and there is nothing to hold back.
                IsPublished = true,
            };

            if (announcement.Topic.Length == 0)
            {
                throw new ValidationException("A topic is required", "announcement.topic.required");
            }

            context.Questions.Add(announcement);
            await context.SaveChangesAsync(ct);

            var stored = await LoadQuestionAsync(announcement.Id, ct);
            await AnnounceQuestionAsync(stored, ct);
            return await ProjectQuestionAsync(stored, ct);
        }

        /// <summary>
        /// Only an announcement may be deleted. <b>A participant's question is
        /// theirs</b>, and removing it would take away the record that they
        /// asked and were answered.
        /// </summary>
        public async Task DeleteAnnouncementAsync(Guid id, CancellationToken ct)
        {
            var question = await LoadQuestionAsync(id, ct);
            await permissions.RequireAsync(Permissions.AnnouncementCreate, question.ActivityId, ct);

            if (question.Kind != QuestionKind.Announcement)
            {
                throw new ConflictException("A question cannot be deleted", "question.notAnnouncement");
            }

            var activityId = question.ActivityId;
            var removedQuestion = Wire.Id(question.Id);
            context.Questions.Remove(question);
            await context.SaveChangesAsync(ct);
            await AnnounceManagedQuestionAsync(activityId, null, removedQuestion, ct);
        }

        private async Task<Question> LoadQuestionAsync(Guid id, CancellationToken ct) =>
            await context.Questions
                .Include(q => q.Author)
                .Include(q => q.Activity)
                .Include(q => q.Series)
                .Include(q => q.SeriesProblem).ThenInclude(sp => sp!.Problem)
                .FirstOrDefaultAsync(q => q.Id == id, ct)
                ?? throw new NotFoundException("Question");

        // ── runners ─────────────────────────────────────────────────────────

        public async Task<PageDto<ManagedRunnerDto>> ListRunnersAsync(
            PageQuery paging, string? state, string? search, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.RunnerRead, null, ct);

            var query = context.Runners.AsNoTracking().AsQueryable();

            if (state is not null)
            {
                var wanted = state switch
                {
                    "approved" => RunnerState.Approved,
                    "revoked" => RunnerState.Revoked,
                    _ => RunnerState.PendingApproval,
                };
                query = query.Where(r => r.State == wanted);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLower();
                query = query.Where(r =>
                    r.Name.ToLower().Contains(needle)
                    || r.Fingerprint.ToLower().Contains(needle)
                    || (r.Address != null && r.Address.ToLower().Contains(needle)));
            }

            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(r => r.RegisteredAt).ThenBy(r => r.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            var items = new List<ManagedRunnerDto>(page.Count);
            foreach (var runner in page) items.Add(await ProjectRunnerAsync(runner, ct));

            return new PageDto<ManagedRunnerDto>
            {
                Items = items, Total = total, Page = paging.Page, PageSize = paging.PageSize,
            };
        }

        private async Task<ManagedRunnerDto> ProjectRunnerAsync(DbRunner runner, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var current = await context.EvaluationJobs
                .AsNoTracking()
                .Where(j => j.RunnerId == runner.Id && j.State == EvaluationJobState.Running)
                .Select(j => (Guid?)j.SubmissionId)
                .FirstOrDefaultAsync(ct);

            var attachments = await context.FileReferences
                .AsNoTracking()
                .Include(r => r.File)
                .Where(r => r.RunnerId == runner.Id && r.SupersededAt == null)
                .ToListAsync(ct);

            MachineDto? machine = null;
            if (runner.Machine is not null)
            {
                try
                {
                    machine = System.Text.Json.JsonSerializer.Deserialize<MachineDto>(
                        runner.Machine, new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                        });
                }
                catch (System.Text.Json.JsonException)
                {
                    // Reported by the Runner and stored without being read. A
                    // shape we cannot show is shown as nothing, not as a failure
                    // of the whole list.
                }
            }

            return new ManagedRunnerDto
            {
                Id = Wire.Id(runner.Id),
                Name = runner.Name,
                Product = runner.Product ?? "",
                Version = runner.Version ?? "",
                ProblemTypes = runner.ProblemTypes,
                External = runner.External,
                Tags = runner.Tags,
                Address = runner.Address ?? "",
                PublicKey = runner.PublicKey,
                Fingerprint = runner.Fingerprint,
                State = Projections.Wire(runner.State),
                // Liveness, not approval — the two say different things and the
                // panel shows both.
                IsConnected = runner.LastSeenAt is { } seen && (now - seen) < TimeSpan.FromMinutes(2),
                LastSeenAt = Wire.At(runner.LastSeenAt),
                RegisteredAt = Wire.At(runner.RegisteredAt),
                ApprovedAt = Wire.At(runner.ApprovedAt),
                RevokedAt = Wire.At(runner.RevokedAt),
                RevokedReason = runner.RevokedReason,
                Machine = machine,
                CurrentSubmissionId = current is { } id ? Wire.Id(id) : null,
                CompletedJobs = runner.CompletedJobs,
                Attachments = attachments.Select(a => new RunnerAttachmentDto
                {
                    Id = Wire.Id(a.FileId),
                    Name = a.Name,
                    MimeType = a.File?.MimeType ?? "text/plain",
                    SizeBytes = a.File?.SizeBytes ?? 0,
                    Sha256 = a.File?.Sha256 ?? "",
                    UploadedAt = Wire.At(a.CreatedAt),
                }).ToList(),
            };
        }

        /// <summary>
        /// A manager approves the fingerprint, and nothing is evaluated before
        /// that.
        /// <para>
        /// Answers the whole record, not the registration acknowledgement. The
        /// caller is a manager refreshing a row and needs everything the row
        /// shows; a Runner learning its own id is the other endpoint, and this
        /// one sat on that shape until 2026-08-08. Its two siblings — revoking
        /// and tagging — always answered this way.
        /// </para>
        /// </summary>
        public async Task<ManagedRunnerDto> ApproveRunnerAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.RunnerApprove, null, ct);

            var runner = await context.Runners.FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new NotFoundException("Runner");

            // Revocation is permanent, so there is nothing to approve back into.
            if (runner.State == RunnerState.Revoked)
            {
                throw new ConflictException(
                    "A revoked Runner cannot be approved; it must register again", "runner.revoked");
            }

            runner.State = RunnerState.Approved;
            runner.ApprovedAt = clock.GetUtcNow().UtcDateTime;
            runner.ApprovedByUserId = currentUser.UserId;
            await context.SaveChangesAsync(ct);

            var projectedRunner = await ProjectRunnerAsync(runner, ct);
            await AnnounceRunnerAsync(projectedRunner, null, ct);
            return projectedRunner;
        }

        /// <summary>
        /// Revoking is permanent. There is no rotation: a leaked key means a new
        /// configuration, a new key and a new registration, and this one never
        /// comes back.
        /// </summary>
        public async Task<ManagedRunnerDto> RevokeRunnerAsync(Guid id, string? reason, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.RunnerRevoke, null, ct);

            var runner = await context.Runners.FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new NotFoundException("Runner");

            runner.State = RunnerState.Revoked;
            runner.RevokedAt = clock.GetUtcNow().UtcDateTime;
            runner.RevokedReason = reason;

            // Whatever it was holding goes back into the queue. The job is not
            // lost because a Runner was; that is what the lease is for, and this
            // is the same recovery done immediately.
            var held = await context.EvaluationJobs
                .Where(j => j.RunnerId == id && j.State == EvaluationJobState.Running)
                .ToListAsync(ct);

            foreach (var job in held)
            {
                job.State = EvaluationJobState.Queued;
                job.RunnerId = null;
                job.LeaseToken = null;
                job.LeaseExpiresAt = null;
                job.ClaimedAt = null;
            }

            await context.SaveChangesAsync(ct);
            foreach (var job in held) await submissions.AnnounceAsync(job.SubmissionId, ct);

            var projectedRunner = await ProjectRunnerAsync(runner, ct);
            await AnnounceRunnerAsync(projectedRunner, null, ct);
            return projectedRunner;
        }

        public async Task<ManagedRunnerDto> SetTagsAsync(
            Guid id, IReadOnlyList<string> tags, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.RunnerUpdate, null, ct);

            var runner = await context.Runners.FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new NotFoundException("Runner");

            // One of the two things about a Runner the operator owns rather than
            // the Runner reporting it — and, since 2026-08-24, what decides
            // which work it is given. Normalised so that `Lab-A` here and
            // `lab-a` on an activity cannot be two pools that read as one.
            runner.Tags = RunnerTags.Validated(tags, "The Runner's tags");
            await context.SaveChangesAsync(ct);
            var projectedRunner = await ProjectRunnerAsync(runner, ct);
            await AnnounceRunnerAsync(projectedRunner, null, ct);
            return projectedRunner;
        }

        public async Task ForgetRunnerAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.RunnerRevoke, null, ct);

            var runner = await context.Runners.FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new NotFoundException("Runner");

            if (runner.State != RunnerState.Revoked)
            {
                throw new ConflictException("Revoke it before forgetting it", "runner.notRevoked");
            }

            var forgotten = Wire.Id(runner.Id);
            context.Runners.Remove(runner);
            await context.SaveChangesAsync(ct);
            await AnnounceRunnerAsync(null, forgotten, ct);
        }
    }
}

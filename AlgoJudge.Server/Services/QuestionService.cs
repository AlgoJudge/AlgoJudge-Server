using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IQuestionService
    {
        Task<PageDto<QuestionDto>> ListAsync(
            string activityIdOrSlug, PageQuery paging, string? search, string? kind,
            Guid? seriesId, Guid? problemId, CancellationToken ct);
        Task<QuestionDto> AskAsync(string activityIdOrSlug, AskQuestionInputDto input, CancellationToken ct);
        Task MarkReadAsync(string activityIdOrSlug, Guid questionId, CancellationToken ct);
    }

    /// <summary>
    /// Questions and announcements: one entity told apart by its kind, because
    /// the two share a list, a scope and a read state and differ only in who may
    /// create one.
    /// </summary>
    public class QuestionService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IActivityService activities,
        ISeriesLockdown lockdown,
        TimeProvider clock
    ) : IQuestionService
    {
        public async Task<PageDto<QuestionDto>> ListAsync(
            string activityIdOrSlug, PageQuery paging, string? search, string? kind,
            Guid? seriesId, Guid? problemId, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.QuestionReadOwn, activity.Id, ct);
            await lockdown.RequireReachableAsync(activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);

            var readsAll = await permissions.HasAsync(Permissions.QuestionReadAll, activity.Id, ct);

            var query = context.Questions
                .AsNoTracking()
                .Where(q => q.ActivityId == activity.Id);

            // A question about a round out of reach goes with the round. One
            // about the activity carries no round and stays — an announcement
            // is how the organiser explains the lockdown.
            var unreachable = (await lockdown.UnreachableRoundsAsync(
                activity.Id, await lockdown.ForReaderAsync(ct), ct)).ToList();
            if (unreachable.Count > 0)
            {
                query = query.Where(q => q.SeriesId == null || !unreachable.Contains(q.SeriesId.Value));
            }

            // A question is visible to its author and to staff until a manager
            // publishes it, after which every participant sees it. An
            // announcement is published by definition.
            if (!readsAll)
            {
                query = query.Where(q => q.IsPublished || q.AuthorUserId == user.Id);
            }

            if (kind is "question") query = query.Where(q => q.Kind == QuestionKind.Question);
            if (kind is "announcement") query = query.Where(q => q.Kind == QuestionKind.Announcement);
            if (seriesId is { } series) query = query.Where(q => q.SeriesId == series);
            if (problemId is { } problem) query = query.Where(q => q.SeriesProblemId == problem);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLower();
                query = query.Where(q => q.Topic.ToLower().Contains(needle) || q.Body.ToLower().Contains(needle));
            }

            var total = await query.CountAsync(ct);

            // Filtered, then sorted, then paged — in that order. Sorting in the
            // Client would order the twenty rows it happens to hold.
            var page = await query
                .OrderByDescending(q => q.CreatedAt).ThenByDescending(q => q.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .Include(q => q.Author)
                .Include(q => q.Series)
                .Include(q => q.SeriesProblem).ThenInclude(sp => sp!.Problem)
                .ToListAsync(ct);

            var ids = page.Select(q => q.Id).ToList();
            var read = await context.QuestionReads
                .AsNoTracking()
                .Where(r => r.UserId == user.Id && ids.Contains(r.QuestionId))
                .Select(r => r.QuestionId)
                .ToListAsync(ct);

            var answerAuthors = await context.Users.AsNoTracking()
                .Where(u => page.Select(q => q.AnswerAuthorUserId).Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => Projections.DisplayName(u), ct);

            return new PageDto<QuestionDto>
            {
                Items = page.Select(q => Project(q, read.Contains(q.Id), answerAuthors)).ToList(),
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        private static QuestionDto Project(
            Question question, bool isRead, IReadOnlyDictionary<string, string> answerAuthors) => new()
            {
                Id = Wire.Id(question.Id),
                Kind = question.Kind == QuestionKind.Announcement ? "announcement" : "question",
                Topic = question.Topic,
                Body = question.Body,
                AuthorName = question.Author is null
                    ? question.AuthorUserId
                    : Projections.DisplayName(question.Author),
                CreatedAt = Wire.At(question.CreatedAt),
                SeriesId = question.SeriesId is { } s ? Wire.Id(s) : null,
                SeriesName = question.Series?.Name,
                ProblemId = question.SeriesProblemId is { } p ? Wire.Id(p) : null,
                ProblemSlug = question.SeriesProblem?.Slug,
                ProblemName = question.SeriesProblem?.Name ?? question.SeriesProblem?.Problem?.Name,
                IsPublished = question.IsPublished,
                IsRead = isRead,
                Answer = question.AnswerBody is null ? null : new QuestionAnswerDto
                {
                    Body = question.AnswerBody,
                    AuthorName = question.AnswerAuthorUserId is { } id
                        ? answerAuthors.GetValueOrDefault(id, id)
                        : "",
                    AnsweredAt = Wire.At(question.AnsweredAt ?? question.CreatedAt),
                },
            };

        public async Task<QuestionDto> AskAsync(
            string activityIdOrSlug, AskQuestionInputDto input, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.QuestionCreate, activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);

            if (activity.ArchivedAt is not null)
            {
                throw new ConflictException("An archived activity accepts no questions", "activity.archived");
            }
            if (!activity.HasQuestions)
            {
                throw new ForbiddenActionException(
                    "This activity does not take questions", "questions.disabled");
            }

            var topic = input.Topic?.Trim() ?? "";
            var body = input.Body?.Trim() ?? "";
            if (topic.Length == 0) throw new ValidationException("A topic is required", "question.topic.required");
            if (body.Length == 0) throw new ValidationException("A question is required", "question.body.required");

            // Asked about one of three things. A problem fills its series in
            // from itself, so the two can never disagree.
            Guid? seriesId = null;
            Guid? assignmentId = null;

            if (input.ProblemId is { } rawProblem && Guid.TryParse(rawProblem, out var problemId))
            {
                var assignment = await context.SeriesProblems
                    .FirstOrDefaultAsync(sp => sp.Id == problemId && sp.ActivityId == activity.Id, ct)
                    ?? throw new NotFoundException("Problem");
                assignmentId = assignment.Id;
                seriesId = assignment.SeriesId;
            }
            else if (input.SeriesId is { } rawSeries && Guid.TryParse(rawSeries, out var parsed))
            {
                var series = await context.Series
                    .FirstOrDefaultAsync(s => s.Id == parsed && s.ActivityId == activity.Id, ct)
                    ?? throw new NotFoundException("Series");
                seriesId = series.Id;
            }

            // **Asking was the one unguarded way into a round out of reach.**
            // Reading it, submitting to it and fetching its statement were all
            // refused; posting a question naming it was not.
            await lockdown.RequireReachableAsync(activity.Id, ct);
            if (seriesId is { } asked)
            {
                var state = await lockdown.ForReaderAsync(ct);
                if ((await lockdown.UnreachableRoundsAsync(activity.Id, state, ct)).Contains(asked))
                {
                    throw new ForbiddenActionException(
                        "This round is out of reach", LockdownCodes.Displaced);
                }
            }

            var question = new Question
            {
                ActivityId = activity.Id,
                SeriesId = seriesId,
                SeriesProblemId = assignmentId,
                Kind = QuestionKind.Question,
                Topic = topic,
                Body = body,
                AuthorUserId = user.Id,
                // Unpublished: it reaches its author and staff until a manager
                // publishes the answer to everybody.
                IsPublished = false,
            };
            context.Questions.Add(question);
            await context.SaveChangesAsync(ct);

            var stored = await context.Questions
                .AsNoTracking()
                .Include(q => q.Author)
                .Include(q => q.Series)
                .Include(q => q.SeriesProblem).ThenInclude(sp => sp!.Problem)
                .FirstAsync(q => q.Id == question.Id, ct);

            return Project(stored, isRead: true, new Dictionary<string, string>());
        }

        /// <summary>
        /// Read state is a property of the pair, not of the question, which is
        /// why it is a row rather than a flag.
        /// </summary>
        public async Task MarkReadAsync(string activityIdOrSlug, Guid questionId, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.QuestionReadOwn, activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);

            var exists = await context.Questions
                .AnyAsync(q => q.Id == questionId && q.ActivityId == activity.Id, ct);
            if (!exists) throw new NotFoundException("Question");

            var already = await context.QuestionReads
                .AnyAsync(r => r.QuestionId == questionId && r.UserId == user.Id, ct);
            // Marking twice is not an error: a screen may say it again on a
            // second visit, and the second time is a no-op rather than a 409.
            if (already) return;

            context.QuestionReads.Add(new QuestionRead
            {
                QuestionId = questionId,
                UserId = user.Id,
                ReadAt = clock.GetUtcNow().UtcDateTime,
            });
            await context.SaveChangesAsync(ct);
        }
    }
}

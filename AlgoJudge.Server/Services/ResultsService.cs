using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IResultsService
    {
        Task<ActivityResultsDto> GetAsync(string activityIdOrSlug, Guid? seriesId, CancellationToken ct);

        /// <summary>
        /// One result, filtered per reader, for everybody entitled to it.
        /// <para>
        /// What the socket pushes. Answers pairs rather than taking a reader,
        /// because the filtering is the same computation for everybody in a
        /// disclosure class and doing it per user would mean a query per
        /// participant on every judged submission.
        /// </para>
        /// </summary>
        Task<IReadOnlyList<(string UserId, ContestantResultDto Result)>> BroadcastAsync(
            Guid submissionId, CancellationToken ct);
    }

    /// <summary>
    /// Results, not a ranking.
    /// <para>
    /// Which board they add up to is the activity's <c>rankingType</c>, and a
    /// Server computing an ICPC penalty would be encoding the semantics of one
    /// ranking type — the thing it is not supposed to know. The arithmetic lives
    /// in the Client, beside the renderers.
    /// </para>
    /// <para>
    /// What the Server keeps is <b>disclosure, not arithmetic</b>, and it applies
    /// in this order:
    /// <list type="number">
    /// <item>the <b>window</b> decides whether a round contributes at all,</item>
    /// <item><c>scoreVisibility</c> decides whose results are in it,</item>
    /// <item>the <b>freeze</b> withholds outcomes.</item>
    /// </list>
    /// A board is assembled in the Client, so <b>anything sent has been
    /// disclosed</b>: withholding at render time is not withholding.
    /// </para>
    /// </summary>
    public class ResultsService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IActivityService activities,
        TimeProvider clock
    ) : IResultsService
    {
        public async Task<ActivityResultsDto> GetAsync(
            string activityIdOrSlug, Guid? seriesId, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.RankingRead, activity.Id, ct);
            var reader = await currentUser.RequireAsync(ct);

            // Nobody may see a score, so there is no board — and no second
            // setting that could disagree with that.
            if (activity.ScoreVisibility == ScoreVisibility.ManagersOnly
                && !await permissions.HasAsync(Permissions.ResultReadAll, activity.Id, ct))
            {
                return Empty(null);
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var unfrozen = await permissions.HasAsync(Permissions.RankingReadUnfrozen, activity.Id, ct);

            var rounds = await context.Series
                .AsNoTracking()
                .Where(s => s.ActivityId == activity.Id && (seriesId == null || s.Id == seriesId))
                .OrderBy(s => s.Order).ThenBy(s => s.Id)
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .ToListAsync(ct);

            // Filter one: a round whose window is shut contributes nothing —
            // not its columns, not its results.
            var visible = rounds.Where(round => unfrozen || WindowOpen(round, now)).ToList();

            // Filter two: whose results are in it.
            var everyone = activity.ScoreVisibility == ScoreVisibility.Everyone
                || await permissions.HasAsync(Permissions.ResultReadAll, activity.Id, ct);

            var members = await context.Grants
                .AsNoTracking()
                .Where(g => g.ActivityId == activity.Id
                    && g.State == GrantState.Active
                    // Whoever runs an activity does not compete in it. A jury
                    // member in the ranking beside the students is a bug.
                    && !g.IsSystem)
                .Include(g => g.User)
                .ToListAsync(ct);

            var contestants = members
                .Where(g => everyone || g.UserId == reader.Id)
                .Select(g => new ContestantDto
                {
                    Id = g.UserId,
                    Name = g.User is null ? g.UserId : Projections.DisplayName(g.User),
                })
                .OrderBy(c => c.Name, StringComparer.CurrentCulture)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();

            var eligible = contestants.Select(c => c.Id).ToHashSet();
            var assignmentIds = visible.SelectMany(r => r.SeriesProblems).Select(sp => sp.Id).ToHashSet();

            var submissions = await context.Submissions
                .AsNoTracking()
                .Where(s => assignmentIds.Contains(s.SeriesProblemId) && eligible.Contains(s.UserId))
                .Include(s => s.SeriesProblem)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .ToListAsync(ct);

            var byRound = visible.ToDictionary(r => r.Id);
            var results = new List<ContestantResultDto>(submissions.Count);

            foreach (var submission in submissions)
            {
                var assignment = submission.SeriesProblem!;
                if (!byRound.TryGetValue(assignment.SeriesId, out var round)) continue;
                results.Add(Disclose(submission, assignment, round, now, unfrozen));
            }

            return new ActivityResultsDto
            {
                Series = visible.Select(round => new ResultSeriesDto
                {
                    Id = Wire.Id(round.Id),
                    Name = round.Name,
                    StartDate = Wire.At(round.StartDate),
                    Frozen = Frozen(round, now, unfrozen),
                    RevealAt = Wire.At(round.RankingRevealAt),
                    Problems = round.SeriesProblems
                        .OrderBy(sp => sp.Order).ThenBy(sp => sp.Id)
                        .Select(sp => new ResultProblemDto
                        {
                            Id = Wire.Id(sp.Id),
                            Slug = sp.Slug,
                            Name = sp.Name ?? sp.Problem?.Name ?? sp.Slug,
                            MaxPoints = Scoring.MaxPoints(sp),
                        })
                        .ToList(),
                }).ToList(),
                Contestants = contestants,
                Results = results
                    .OrderBy(r => r.SubmittedAt, StringComparer.Ordinal)
                    .ThenBy(r => r.Id, StringComparer.Ordinal)
                    .ToList(),
                Me = eligible.Contains(reader.Id) ? reader.Id : null,
            };
        }

        private static ActivityResultsDto Empty(string? me) => new()
        {
            Series = [],
            Contestants = [],
            Results = [],
            Me = me,
        };

        /// <summary>
        /// Whether this round's standings may be seen at all. Absent `from` means
        /// the round's own start; absent `to` means for ever.
        /// </summary>
        private static bool WindowOpen(Series round, DateTime now)
        {
            var from = round.RankingVisibleFrom ?? round.StartDate;
            if (from is not null && from > now) return false;
            if (round.RankingVisibleTo is { } to && to <= now) return false;
            return true;
        }

        /// <summary>
        /// Whether the round is frozen right now. Whoever holds
        /// <c>ranking:read:unfrozen</c> is never inside a freeze.
        /// </summary>
        private static bool Frozen(Series round, DateTime now, bool unfrozen)
        {
            if (unfrozen) return false;
            if (round.RankingFreezeAt is not { } freeze) return false;
            if (freeze > now) return false;
            return round.RankingRevealAt is not { } reveal || reveal > now;
        }

        /// <summary>
        /// One submission, as the board may see it.
        /// <para>
        /// A withheld entry <b>keeps its identity, its problem and its time</b>
        /// and loses everything about how it went. Omitting it instead would
        /// leave a board unable to tell "did not try" from "tried, and you may
        /// not know yet" — and the second is exactly what the `?` cell of a
        /// frozen ICPC board means.
        /// </para>
        /// </summary>
        private static ContestantResultDto Disclose(
            Submission submission, SeriesProblem assignment, Series round, DateTime now, bool unfrozen)
        {
            var withheld = Frozen(round, now, unfrozen)
                && round.RankingFreezeAt is { } freeze
                && submission.CreatedDate >= freeze;

            var basic = new ContestantResultDto
            {
                Id = Wire.Id(submission.Id),
                ContestantId = submission.UserId,
                SeriesId = Wire.Id(round.Id),
                ProblemId = Wire.Id(assignment.Id),
                ProblemSlug = assignment.Slug,
                SubmittedAt = Wire.At(submission.CreatedDate),
            };

            if (withheld) return basic with { Frozen = true };

            var current = Scoring.Current(submission);
            return basic with
            {
                Points = Scoring.Rescale(current?.Result?.Score, Scoring.MaxPoints(assignment)),
                State = Projections.Wire(current?.State ?? EvaluationJobState.Queued),
                Extra = Projections.Opaque(current?.Result?.Extra),
            };
        }

        /// <summary>
        /// The same three filters, applied for everybody who may see this
        /// submission.
        /// <para>
        /// The socket carries what the endpoint would carry. It is not a second
        /// path to the data — a socket that skipped this would be a way around
        /// the rule the endpoint applies.
        /// </para>
        /// <para>
        /// Two queries, whatever the size of the activity: the submission, and
        /// the grants. The filtering itself is pure, and there are only two
        /// distinct answers — the frozen one and the unfrozen one — so they are
        /// computed once and handed to whoever is entitled to each.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<(string UserId, ContestantResultDto Result)>> BroadcastAsync(
            Guid submissionId, CancellationToken ct)
        {
            var submission = await context.Submissions
                .AsNoTracking()
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Series)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Activity)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .FirstOrDefaultAsync(s => s.Id == submissionId, ct);

            var assignment = submission?.SeriesProblem;
            var round = assignment?.Series;
            var activity = assignment?.Activity;
            if (submission is null || assignment is null || round is null || activity is null) return [];

            var now = clock.GetUtcNow().UtcDateTime;

            var withFreeze = Disclose(submission, assignment, round, now, unfrozen: false);
            var withoutFreeze = Disclose(submission, assignment, round, now, unfrozen: true);
            var windowOpen = WindowOpen(round, now);

            var grants = await context.Grants.AsNoTracking()
                .Where(g => g.State == GrantState.Active
                    && (g.ActivityId == null || g.ActivityId == activity.Id))
                .Select(g => new { g.UserId, g.Permissions })
                .ToListAsync(ct);

            // A user may hold both a system grant and one in this activity, and
            // the effective set is their union.
            var held = new Dictionary<string, HashSet<string>>();
            foreach (var grant in grants)
            {
                List<string> keys;
                try
                {
                    keys = System.Text.Json.JsonSerializer.Deserialize<List<string>>(grant.Permissions) ?? [];
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
                if (!held.TryGetValue(grant.UserId, out var set)) held[grant.UserId] = set = [];
                set.UnionWith(keys);
            }

            var recipients = new List<(string, ContestantResultDto)>();

            foreach (var (userId, permissionsHeld) in held)
            {
                var administrator = permissionsHeld.Contains(Permissions.SystemAdministrator);
                if (!administrator && !permissionsHeld.Contains(Permissions.RankingRead)) continue;

                var unfrozen = administrator || permissionsHeld.Contains(Permissions.RankingReadUnfrozen);
                var readsEveryone = administrator || permissionsHeld.Contains(Permissions.ResultReadAll);

                if (activity.ScoreVisibility == ScoreVisibility.ManagersOnly && !readsEveryone) continue;
                if (!unfrozen && !windowOpen) continue;

                // Under `participantOnly` the reader is sent their own results
                // and nobody else's, so somebody else's simply does not reach
                // them.
                var everyone = activity.ScoreVisibility == ScoreVisibility.Everyone || readsEveryone;
                if (!everyone && submission.UserId != userId) continue;

                recipients.Add((userId, unfrozen ? withoutFreeze : withFreeze));
            }

            return recipients;
        }
    }
}

using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface ISeriesService
    {
        Task<IReadOnlyList<SeriesDto>> ListForParticipantAsync(string activityIdOrSlug, CancellationToken ct);
        Task<IReadOnlyList<ManagedSeriesDto>> ListManagedAsync(string activityIdOrSlug, CancellationToken ct);
        Task<ManagedSeriesDto> CreateAsync(string activityIdOrSlug, SeriesInputDto input, CancellationToken ct);
        Task<ManagedSeriesDto> AttachProblemAsync(Guid seriesId, SeriesProblemInputDto input, CancellationToken ct);
    }

    public class SeriesService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IActivityService activities,
        ISeriesGate gate
    ) : ISeriesService
    {
        /// <summary>
        /// The rounds, as a participant sees them.
        /// <para>
        /// The withholding happens here, per request, and not when a fixture is
        /// built: a round that has not opened answers with no `problems` at all —
        /// absent, not empty — and its count only when the manager allowed it.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<SeriesDto>> ListForParticipantAsync(
            string activityIdOrSlug, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityRead, activity.Id, ct);
            var user = await currentUser.RequireAsync(ct);

            var series = await context.Series
                .AsNoTracking()
                .Where(s => s.ActivityId == activity.Id)
                .OrderBy(s => s.Order).ThenBy(s => s.Id)
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .ToListAsync(ct);

            var mine = await context.Submissions
                .AsNoTracking()
                .Where(s => s.UserId == user.Id && s.SeriesProblem!.ActivityId == activity.Id)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .ToListAsync(ct);

            var byAssignment = mine.GroupBy(s => s.SeriesProblemId)
                .ToDictionary(g => g.Key, g => g.ToList());

            return series.Select(round =>
            {
                var open = gate.MayReadProblems(round, activity);
                var problems = round.SeriesProblems
                    .OrderBy(sp => sp.Order).ThenBy(sp => sp.Id)
                    .Select(assignment =>
                    {
                        var attempts = byAssignment.GetValueOrDefault(assignment.Id) ?? [];
                        var best = Scoring.Best(attempts);
                        var maxPoints = Scoring.MaxPoints(assignment);
                        return new ProblemSummaryDto
                        {
                            Id = Wire.Id(assignment.Id),
                            Slug = assignment.Slug,
                            Name = assignment.Name ?? assignment.Problem?.Name ?? assignment.Slug,
                            Status = Scoring.Status(attempts, best),
                            BestScore = Scoring.Rescale(best, maxPoints),
                            MaxScore = attempts.Count == 0 ? null : maxPoints,
                            Attempts = attempts.Count,
                        };
                    })
                    .ToList();

                return new SeriesDto
                {
                    Id = Wire.Id(round.Id),
                    Slug = round.Slug,
                    Name = round.Name,
                    StartDate = Wire.At(round.StartDate),
                    EndDate = Wire.At(round.EndDate),
                    IsOpen = round.IsOpen,
                    PausedAt = Wire.At(round.PausedAt),
                    RankingVisibleFrom = Wire.At(round.RankingVisibleFrom),
                    RankingVisibleTo = Wire.At(round.RankingVisibleTo),
                    // Even the count is withheld unless the manager allowed it.
                    ProblemCount = open || round.RevealProblemCount ? problems.Count : null,
                    Problems = open ? problems : null,
                };
            }).ToList();
        }

        public async Task<IReadOnlyList<ManagedSeriesDto>> ListManagedAsync(
            string activityIdOrSlug, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            var series = await context.Series
                .AsNoTracking()
                .Where(s => s.ActivityId == activity.Id)
                .OrderBy(s => s.Order).ThenBy(s => s.Id)
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .ToListAsync(ct);

            var result = new List<ManagedSeriesDto>(series.Count);
            foreach (var round in series)
            {
                result.Add(Projections.ManagedSeries(round, await AssignmentsAsync(round, ct)));
            }
            return result;
        }

        private async Task<List<ManagedSeriesProblemDto>> AssignmentsAsync(Series round, CancellationToken ct)
        {
            var assignments = round.SeriesProblems.OrderBy(sp => sp.Order).ThenBy(sp => sp.Id).ToList();
            var result = new List<ManagedSeriesProblemDto>(assignments.Count);

            foreach (var assignment in assignments)
            {
                var versions = await context.ProblemVersions
                    .Where(v => v.ProblemId == assignment.ProblemId)
                    .Select(v => new { v.Id, v.Version })
                    .ToListAsync(ct);

                var pinned = versions.FirstOrDefault(v => v.Id == assignment.PinnedProblemVersionId);
                var effective = pinned?.Id ?? versions.OrderByDescending(v => v.Version).FirstOrDefault()?.Id;

                result.Add(new ManagedSeriesProblemDto
                {
                    Id = Wire.Id(assignment.Id),
                    SeriesId = Wire.Id(assignment.SeriesId),
                    ProblemId = Wire.Id(assignment.ProblemId),
                    ProblemSlug = assignment.Problem?.Slug ?? "",
                    ProblemName = assignment.Problem?.Name ?? "",
                    Slug = assignment.Slug,
                    Name = assignment.Name,
                    Order = assignment.Order,
                    PinnedProblemVersionId = assignment.PinnedProblemVersionId is { } id ? Wire.Id(id) : null,
                    PinnedVersion = pinned?.Version,
                    CurrentVersion = versions.Count == 0 ? 0 : versions.Max(v => v.Version),
                    HasPackage = effective is { } versionId
                        && await context.FileReferences.AnyAsync(
                            r => r.ProblemVersionId == versionId && r.Name == PackageNames.Archive, ct),
                    SubmissionCount = await context.Submissions
                        .CountAsync(s => s.SeriesProblemId == assignment.Id, ct),
                    Config = Projections.Opaque(assignment.Config),
                    MaxPoints = assignment.MaxPoints,
                    MaxUploadBytes = assignment.MaxUploadBytes,
                    MaxAttachments = assignment.MaxAttachments,
                    MaxSubmissions = assignment.MaxSubmissions,
                });
            }
            return result;
        }

        public async Task<ManagedSeriesDto> CreateAsync(
            string activityIdOrSlug, SeriesInputDto input, CancellationToken ct)
        {
            var activity = await activities.ResolveAsync(activityIdOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            if (activity.ArchivedAt is not null)
            {
                throw new ConflictException("An archived activity accepts no changes", "activity.archived");
            }

            var slug = input.Slug?.Trim() ?? "";
            if (slug.Length == 0) throw new ValidationException("A slug is required", "slug.required");
            if (await context.Series.AnyAsync(s => s.ActivityId == activity.Id && s.Slug == slug, ct))
            {
                throw new ConflictException(
                    "A series with that slug already exists in this activity", "series.slug.taken");
            }

            var order = await context.Series.CountAsync(s => s.ActivityId == activity.Id, ct) + 1;
            var start = ActivityService.ParseInstant(input.StartDate);
            var end = ActivityService.ParseInstant(input.EndDate);

            var series = new Series
            {
                ActivityId = activity.Id,
                Slug = slug,
                Name = input.Name?.Trim() is { Length: > 0 } name ? name : slug,
                StartDate = start,
                EndDate = end,
                Order = order,
                RevealProblemCount = input.RevealProblemCount ?? true,
                RankingFreezeAt = ActivityService.ParseInstant(input.RankingFreezeAt),
                RankingRevealAt = ActivityService.ParseInstant(input.RankingRevealAt),
                RankingVisibleFrom = ActivityService.ParseInstant(input.RankingVisibleFrom),
                RankingVisibleTo = ActivityService.ParseInstant(input.RankingVisibleTo),
                // The scheduler owns every transition, so a new series starts
                // shut and is opened by it — including one created with a start
                // already in the past, which the next scan picks up and marks
                // late rather than pretending it just happened.
                IsOpen = false,
            };
            context.Series.Add(series);
            await context.SaveChangesAsync(ct);

            var stored = await context.Series
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .FirstAsync(s => s.Id == series.Id, ct);
            return Projections.ManagedSeries(stored, await AssignmentsAsync(stored, ct));
        }

        /// <summary>
        /// Attaches a library problem to a round.
        /// <para>
        /// The pin is set <b>here</b>, to the library's current version (decided
        /// 2026-08-08). Publishing a correction therefore does not change what a
        /// running round is judged against, and following it is a manager's
        /// deliberate act rather than a side effect of fixing a typo.
        /// </para>
        /// </summary>
        public async Task<ManagedSeriesDto> AttachProblemAsync(
            Guid seriesId, SeriesProblemInputDto input, CancellationToken ct)
        {
            var series = await context.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct)
                ?? throw new NotFoundException("Series");

            await permissions.RequireAsync(Permissions.ProblemAttach, series.ActivityId, ct);

            if (!Guid.TryParse(input.ProblemId, out var problemId))
            {
                throw new ValidationException("A problem id is required", "problem.required");
            }
            var problem = await context.Problems.FirstOrDefaultAsync(p => p.Id == problemId, ct)
                ?? throw new NotFoundException("Problem");

            if (problem.ArchivedAt is not null)
            {
                throw new ConflictException("An archived problem cannot be attached", "problem.archived");
            }

            var slug = input.Slug?.Trim() is { Length: > 0 } given ? given : problem.Slug;

            // Unique across the whole activity, not the series. The database
            // enforces it too; this is here to answer with a code the Client
            // understands rather than a constraint violation.
            if (await context.SeriesProblems.AnyAsync(
                    sp => sp.ActivityId == series.ActivityId && sp.Slug == slug, ct))
            {
                throw new ConflictException(
                    "That problem slug is already used in this activity", "assignment.slug.taken");
            }

            Guid? pinned = null;
            if (input.PinnedProblemVersionId is { } requested && Guid.TryParse(requested, out var explicitPin))
            {
                pinned = explicitPin;
            }
            else
            {
                pinned = await context.ProblemVersions
                    .Where(v => v.ProblemId == problemId)
                    .OrderByDescending(v => v.Version)
                    .Select(v => (Guid?)v.Id)
                    .FirstOrDefaultAsync(ct);
            }

            var order = await context.SeriesProblems.CountAsync(sp => sp.SeriesId == seriesId, ct) + 1;

            context.SeriesProblems.Add(new SeriesProblem
            {
                SeriesId = seriesId,
                // Denormalised from the series so the database can enforce the
                // activity-wide slug rule on its own.
                ActivityId = series.ActivityId,
                ProblemId = problemId,
                PinnedProblemVersionId = pinned,
                Slug = slug,
                Name = input.Name,
                Order = order,
                Config = Opaque.Store(input.Config, "config"),
                Spec = Opaque.Store(input.Spec, "spec"),
                Props = Opaque.Store(input.Props, "props"),
                MaxPoints = input.MaxPoints,
                MaxUploadBytes = input.MaxUploadBytes,
                MaxAttachments = input.MaxAttachments,
                MaxSubmissions = input.MaxSubmissions,
            });

            await context.SaveChangesAsync(ct);

            var stored = await context.Series
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .FirstAsync(s => s.Id == seriesId, ct);
            return Projections.ManagedSeries(stored, await AssignmentsAsync(stored, ct));
        }
    }
}

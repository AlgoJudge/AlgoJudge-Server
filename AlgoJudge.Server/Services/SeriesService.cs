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
        ISeriesGate gate,
        ISeriesLockdown lockdown
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

            var all = await context.Series
                .AsNoTracking()
                .Where(s => s.ActivityId == activity.Id)
                .OrderBy(s => s.Order).ThenBy(s => s.Id)
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .ToListAsync(ct);

            // **Hidden is absent, locked is present and says so.** A series
            // restricted to an address this reader is not at leaves no trace —
            // its dates and its problem count are exactly what it withholds. One
            // displaced by something more important keeps its row, because
            // "not now, because of X" is the whole message.
            var state = await lockdown.ForReaderAsync(ct);
            var series = all.Where(s => !state.IsHidden(s.Id)).ToList();

            var mine = await context.Submissions
                .AsNoTracking()
                .Where(s => s.UserId == user.Id && s.SeriesProblem!.ActivityId == activity.Id)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .ToListAsync(ct);

            var byAssignment = mine.GroupBy(s => s.SeriesProblemId)
                .ToDictionary(g => g.Key, g => g.ToList());

            return series.Select(round =>
            {
                // Displaced: its problems go with it, exactly as a closed round's
                // do. Withheld by the Server, never left to the screen.
                var locked = state.IsLocked(activity.Id, round.Importance);
                var open = !locked && gate.MayReadProblems(round, activity);
                var problems = round.SeriesProblems
                    .OrderBy(sp => sp.Order).ThenBy(sp => sp.Id)
                    .Select(assignment =>
                    {
                        var attempts = byAssignment.GetValueOrDefault(assignment.Id) ?? [];
                        var (best, outOf) = Scoring.BestOf(attempts);
                        // Where the assignment states no point value, the scale
                        // is the package's own — which the Server learns from a
                        // result, because it never opens a package.
                        var maxPoints = Scoring.Scale(assignment, outOf);
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
                    ProblemCount = open || (!locked && round.RevealProblemCount) ? problems.Count : null,
                    Problems = open ? problems : null,
                    Locked = locked
                        ? new LockedDto { SeriesName = state.DisplacerFor(activity.Id)?.SeriesName ?? "" }
                        : null,
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
                .Include(s => s.AddressRules)
                .ToListAsync(ct);

            // Read once for the whole page rather than per round.
            var pools = await RunnerTags.ApprovedPoolsAsync(context, ct);

            var result = new List<ManagedSeriesDto>(series.Count);
            foreach (var round in series)
            {
                result.Add(Projections.ManagedSeries(
                    round,
                    await AssignmentsAsync(round, ct),
                    RunnerTags.CountMatching(pools, round.RunnerTags ?? activity.RunnerTags)));
            }
            return result;
        }

        /// <summary>
        /// How many approved Runners reach this round — its own pools, or its
        /// activity's where it does not override them.
        /// </summary>
        private async Task<int> MatchingRunnersAsync(Series round, CancellationToken ct) =>
            RunnerTags.CountMatching(
                await RunnerTags.ApprovedPoolsAsync(context, ct),
                round.RunnerTags ?? await context.Activities.AsNoTracking()
                    .Where(a => a.Id == round.ActivityId)
                    .Select(a => a.RunnerTags)
                    .FirstAsync(ct));

        /// <summary>
        /// Writes a round's importance and address rules, and refuses the pair
        /// that cannot mean anything.
        /// <para>
        /// <b>Shared by create and update</b>, because two copies of a validation
        /// are two chances for one path to accept what the other refuses — and
        /// the thing being refused here is a round that would restrict a whole
        /// installation for ever.
        /// </para>
        /// <para>
        /// <b>Both dates or neither restriction.</b> A lockdown is bounded by the
        /// round that imposes it, so a round with no end imposes one that never
        /// lifts; a round with no start has nothing to say when it begins. The
        /// owner asked for the rule and it is cheap to keep.
        /// </para>
        /// </summary>
        internal static void ApplyRestrictions(Series round, SeriesInputDto input)
        {
            if (input.Importance is { } rank)
            {
                if (!SeriesImportance.IsKnown(rank))
                {
                    throw new ValidationException(
                        "That is not an importance this Server knows", "series.importance.unknown");
                }
                round.Importance = rank;
            }

            if (input.ImportanceScope is { } scope)
            {
                round.ImportanceScope = scope switch
                {
                    "activity" => SeriesImportanceScope.Activity,
                    "installation" => SeriesImportanceScope.Installation,
                    _ => throw new ValidationException(
                        "That is not a scope this Server knows", "series.importanceScope.unknown"),
                };
            }

            if (input.RestrictionsEnabled is { } enabled) round.RestrictionsEnabled = enabled;

            if (input.RunnerTags is { } runnerTags)
            {
                // Empty goes back to inheriting the activity's, rather than being
                // stored as an empty override — a round wanting the general
                // Runners while its activity is pinned writes `default` out.
                var tags = RunnerTags.Validated(runnerTags, "The round's Runner tags");
                round.RunnerTags = tags.Count == 0 ? null : tags;
            }

            if (input.AddressRules is { } rules)
            {
                round.AddressRules.Clear();
                foreach (var rule in rules)
                {
                    var network = rule.Network?.Trim() ?? "";
                    // Parsed here as well as stored as `cidr`: the database would
                    // refuse it too, but as a transaction failure carrying no
                    // field name. This says which entry is wrong.
                    if (!System.Net.IPNetwork.TryParse(network, out var parsed))
                    {
                        throw new ValidationException(
                            $"\"{network}\" is not an address range", "series.address.invalid");
                    }
                    round.AddressRules.Add(new SeriesAddressRule
                    {
                        SeriesId = round.Id,
                        Network = parsed,
                        Note = rule.Note?.Trim() is { Length: > 0 } note ? note : null,
                    });
                }
            }

            if ((round.Importance != SeriesImportance.Normal || round.AddressRules.Count > 0)
                && (round.StartDate is null || round.EndDate is null))
            {
                throw new ValidationException(
                    "A round that restricts anything needs a start and an end",
                    "series.restrictions.needDates");
            }
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
            ApplyRestrictions(series, input);

            context.Series.Add(series);
            await context.SaveChangesAsync(ct);

            var stored = await context.Series
                .Include(s => s.SeriesProblems).ThenInclude(sp => sp.Problem)
                .Include(s => s.AddressRules)
                .FirstAsync(s => s.Id == series.Id, ct);
            return Projections.ManagedSeries(
                stored, await AssignmentsAsync(stored, ct), await MatchingRunnersAsync(stored, ct));
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

            CheckMaxPoints(input.MaxPoints);

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
            return Projections.ManagedSeries(
                stored, await AssignmentsAsync(stored, ct), await MatchingRunnersAsync(stored, ct));
        }
    
        /// <summary>
        /// A point value, or nothing. <b>Never zero and never negative.</b>
        /// <para>
        /// Zero was accepted and is not a problem worth nothing — it is a
        /// problem whose every number is <c>0 / 0</c>, which a board reads as
        /// full marks because zero out of zero is the whole of it. A problem
        /// nobody should score is a problem nobody should attach.
        /// </para>
        /// <para>
        /// Checked on both write paths rather than on one: an assignment is
        /// created by attaching and changed by editing, and a rule enforced on
        /// the first alone is a rule the second removes.
        /// </para>
        /// </summary>
        private static void CheckMaxPoints(int? maxPoints)
        {
            if (maxPoints is { } value && value <= 0)
            {
                throw new ValidationException(
                    $"A problem is worth {value} points here, which is not a value anything can be scored against",
                    "assignment.maxPoints.invalid");
            }
        }

}
}

using System.Text.Json;
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
    public interface IActivityService
    {
        Task<PageDto<ActivityDto>> ListAsync(PageQuery paging, string[]? states, CancellationToken ct);
        Task<ActivityDto> GetAsync(string idOrSlug, CancellationToken ct);
        Task<PageDto<ManagedActivityDto>> ListManagedAsync(PageQuery paging, string? search, bool includeArchived, CancellationToken ct);
        Task<ManagedActivityDto> GetManagedAsync(string idOrSlug, CancellationToken ct);
        Task<ManagedActivityDto> CreateAsync(ActivityInputDto input, CancellationToken ct);
        Task<Activity> ResolveAsync(string idOrSlug, CancellationToken ct);
    }

    public class ActivityService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        TimeProvider clock
    ) : IActivityService
    {
        /// <summary>
        /// An id or a slug, as every activity path accepts.
        /// <para>
        /// UUID first, then slug. A slug is a human-readable alias and never a
        /// reference — nothing in the schema points at an activity by slug — so
        /// this is a lookup, not a foreign key.
        /// </para>
        /// </summary>
        public async Task<Activity> ResolveAsync(string idOrSlug, CancellationToken ct)
        {
            var query = context.Activities
                .Include(a => a.AttachmentRules)
                .AsQueryable();

            Activity? activity = Guid.TryParse(idOrSlug, out var id)
                ? await query.FirstOrDefaultAsync(a => a.Id == id, ct)
                : null;

            activity ??= await query.FirstOrDefaultAsync(
                a => a.Slug.ToLower() == idOrSlug.ToLower(), ct);

            return activity ?? throw new NotFoundException("Activity");
        }

        private async Task<Dictionary<Guid, GrantState>> MembershipsAsync(CancellationToken ct)
        {
            var userId = currentUser.UserId;
            if (userId is null) return [];

            return await context.Grants
                .AsNoTracking()
                .Where(g => g.UserId == userId && g.ActivityId != null)
                .ToDictionaryAsync(g => g.ActivityId!.Value, g => g.State, ct);
        }

        private async Task<List<FileReference>> DocumentsAsync(Guid activityId, CancellationToken ct) =>
            await context.FileReferences
                .AsNoTracking()
                .Include(r => r.File)
                .Where(r => r.ActivityId == activityId
                    && r.OwnerKind == FileOwnerKind.ActivityDocument
                    && r.SupersededAt == null)
                .ToListAsync(ct);

        /// <summary>
        /// The activities this reader may see.
        /// <para>
        /// <b>The Server decides what that means.</b> An activity that is closed,
        /// or hidden from people not in it, is simply not in the answer — the
        /// Client never filters on `joinPolicy` or `unlisted` itself, because a
        /// rule the Client enforced would be a rule anybody could turn off.
        /// </para>
        /// </summary>
        public async Task<PageDto<ActivityDto>> ListAsync(
            PageQuery paging, string[]? states, CancellationToken ct)
        {
            var memberships = await MembershipsAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;

            var candidates = await context.Activities
                .AsNoTracking()
                .Where(a => a.ArchivedAt == null)
                .ToListAsync(ct);

            var visible = candidates
                .Where(a => memberships.ContainsKey(a.Id)
                    || (!a.Unlisted && a.JoinPolicy != JoinPolicy.Closed))
                .ToList();

            if (states is { Length: > 0 })
            {
                var wanted = states.ToHashSet(StringComparer.OrdinalIgnoreCase);
                visible = visible.Where(a => wanted.Contains(Projections.ActivityState(a, now))).ToList();
            }

            // The decided default order, with a unique tiebreaker: without one,
            // two activities sharing a start date can swap between pages and a
            // reader sees the same row twice.
            var ordered = visible
                .OrderByDescending(a => a.StartDate ?? DateTime.MinValue)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .ThenBy(a => a.Id)
                .ToList();

            var page = ordered.Skip(paging.Skip).Take(paging.PageSize).ToList();
            var items = new List<ActivityDto>(page.Count);
            foreach (var activity in page)
            {
                items.Add(Projections.Activity(
                    activity, Membership(memberships, activity.Id), now, await DocumentsAsync(activity.Id, ct)));
            }

            return new PageDto<ActivityDto>
            {
                Items = items,
                Total = ordered.Count,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        private static string Membership(Dictionary<Guid, GrantState> memberships, Guid activityId) =>
            memberships.TryGetValue(activityId, out var state)
                ? state == GrantState.Invited ? "invited" : "enrolled"
                : "open";

        /// <summary>
        /// Answers for somebody <b>not enrolled</b> as well, with what the
        /// activity's own page needs to draw itself for them. Not the series, and
        /// not the problems — those belong to being in it.
        /// </summary>
        public async Task<ActivityDto> GetAsync(string idOrSlug, CancellationToken ct)
        {
            var activity = await ResolveAsync(idOrSlug, ct);
            var memberships = await MembershipsAsync(ct);

            var member = memberships.ContainsKey(activity.Id);
            var listed = !activity.Unlisted && activity.JoinPolicy != JoinPolicy.Closed;

            // Not 403: an activity somebody may not see must not be confirmed to
            // exist by the shape of the refusal. The address is guessable.
            if (!member && !listed && !await permissions.HasAsync(Permissions.ActivityUpdate, activity.Id, ct))
            {
                throw new NotFoundException("Activity");
            }

            return Projections.Activity(
                activity,
                Membership(memberships, activity.Id),
                clock.GetUtcNow().UtcDateTime,
                await DocumentsAsync(activity.Id, ct));
        }

        public async Task<PageDto<ManagedActivityDto>> ListManagedAsync(
            PageQuery paging, string? search, bool includeArchived, CancellationToken ct)
        {
            var allowed = await permissions.ActivitiesWithAsync(Permissions.ActivityUpdate, ct);

            var query = context.Activities
                .Include(a => a.AttachmentRules)
                .AsQueryable();

            // Null means a system grant holds it everywhere. An empty list means
            // it is held nowhere — a different answer, and conflating them is how
            // a manager of nothing sees every activity.
            if (allowed is not null)
            {
                var ids = allowed.ToHashSet();
                query = query.Where(a => ids.Contains(a.Id));
            }

            if (!includeArchived) query = query.Where(a => a.ArchivedAt == null);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLower();
                query = query.Where(a => a.Name.ToLower().Contains(needle) || a.Slug.ToLower().Contains(needle));
            }

            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(a => a.StartDate)
                .ThenBy(a => a.Name)
                .ThenBy(a => a.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            var items = new List<ManagedActivityDto>(page.Count);
            foreach (var activity in page) items.Add(await ManagedAsync(activity, ct));

            return new PageDto<ManagedActivityDto>
            {
                Items = items,
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        public async Task<ManagedActivityDto> GetManagedAsync(string idOrSlug, CancellationToken ct)
        {
            var activity = await ResolveAsync(idOrSlug, ct);
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);
            return await ManagedAsync(activity, ct);
        }

        private async Task<ManagedActivityDto> ManagedAsync(Activity activity, CancellationToken ct)
        {
            var seriesCount = await context.Series.CountAsync(s => s.ActivityId == activity.Id, ct);
            var problemCount = await context.SeriesProblems.CountAsync(sp => sp.ActivityId == activity.Id, ct);
            // Read from the grants, never stored — and staff are excluded,
            // because whoever runs an activity does not compete in it.
            var participantCount = await context.Grants.CountAsync(
                g => g.ActivityId == activity.Id && !g.IsSystem && g.State == GrantState.Active, ct);

            return Projections.ManagedActivity(
                activity, await DocumentsAsync(activity.Id, ct), seriesCount, problemCount, participantCount);
        }

        public async Task<ManagedActivityDto> CreateAsync(ActivityInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ActivityCreate, null, ct);
            var user = await currentUser.RequireAsync(ct);

            var slug = input.Slug?.Trim() ?? "";
            if (slug.Length == 0) throw new ValidationException("A slug is required", "slug.required");

            if (await context.Activities.AnyAsync(a => a.Slug.ToLower() == slug.ToLower(), ct))
            {
                throw new ConflictException("An activity with that slug already exists", "activity.slug.taken");
            }

            var policy = ParseJoinPolicy(input.JoinPolicy);
            var activity = new Activity
            {
                Slug = slug,
                Name = input.Name?.Trim() is { Length: > 0 } name ? name : slug,
                Type = input.Type ?? "contest@1",
                RankingType = input.RankingType ?? "icpc",
                TimeZone = input.TimeZone ?? "Europe/Warsaw",
                StartDate = ParseInstant(input.StartDate),
                EndDate = ParseInstant(input.EndDate),
                HasQuestions = input.Modules?.Questions ?? true,
                ScoreVisibility = ParseScoreVisibility(input.ScoreVisibility),
                JoinPolicy = policy,
                // Only kept under `password`, so switching to open and back does
                // not quietly restore a code somebody had already shared.
                JoinPassword = policy == JoinPolicy.Password ? input.JoinPassword : null,
                // Under `closed` this is what the policy already means, so it is
                // forced rather than left to disagree with it.
                Unlisted = policy == JoinPolicy.Closed || (input.Unlisted ?? false),
                HideEndedSeriesProblems = input.HideEndedSeriesProblems ?? false,
                Languages = input.Languages?.ToList() ?? ["cpp", "python"],
                MaxUploadBytes = input.MaxUploadBytes ?? 8L * 1024 * 1024,
                MaxAttachments = input.MaxAttachments ?? 1,
                MaxSubmissionsPerProblem = input.MaxSubmissionsPerProblem,
            };

            foreach (var rule in input.AttachmentVisibility ?? [])
            {
                activity.AttachmentRules.Add(new AttachmentRule
                {
                    ActivityId = activity.Id,
                    Name = rule.Name,
                    Visibility = rule.Visibility == "participant"
                        ? AttachmentVisibility.Participant
                        : AttachmentVisibility.ManagersOnly,
                });
            }

            context.Activities.Add(activity);

            // Whoever creates an activity manages it. Without this the creator
            // would need somebody else to grant them access to what they just
            // made — and `activity:update` is activity-scoped, so a system grant
            // to create does not carry.
            context.Grants.Add(new Grant
            {
                UserId = user.Id,
                ActivityId = activity.Id,
                Permissions = JsonSerializer.Serialize(Permissions.ManagerTemplate),
                CreatedFromTemplate = "manager",
                IsSystem = true,
                State = GrantState.Active,
                GrantedByUserId = user.Id,
            });

            await context.SaveChangesAsync(ct);
            return await ManagedAsync(activity, ct);
        }

        internal static DateTime? ParseInstant(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal);

        private static JoinPolicy ParseJoinPolicy(string? value) => value switch
        {
            "open" => JoinPolicy.Open,
            "password" => JoinPolicy.Password,
            _ => JoinPolicy.Closed,
        };

        private static ScoreVisibility ParseScoreVisibility(string? value) => value switch
        {
            "participantOnly" => ScoreVisibility.ParticipantOnly,
            "managersOnly" => ScoreVisibility.ManagersOnly,
            _ => ScoreVisibility.Everyone,
        };
    }
}

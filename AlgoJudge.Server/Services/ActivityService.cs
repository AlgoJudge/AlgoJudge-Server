using System.Text.Json;
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
    public interface IActivityService
    {
        Task<PageDto<ActivityDto>> ListAsync(
            PageQuery paging, IReadOnlyList<string>? states, IReadOnlyList<string>? types,
            CancellationToken ct);
        Task<ActivityDto> GetAsync(string idOrSlug, CancellationToken ct);
        Task<PageDto<ManagedActivityDto>> ListManagedAsync(PageQuery paging, string? search, bool includeArchived, CancellationToken ct);
        Task<ManagedActivityDto> GetManagedAsync(string idOrSlug, CancellationToken ct);

        /// <summary>
        /// Whether this activity has been published. A fact, not a decision: who
        /// may see an unpublished one is answered by the permission, and this
        /// exists so a caller can ask without holding it.
        /// </summary>
        Task<bool> IsPublishedAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Copies an activity's shape - its rounds, the problems assigned to
        /// them, its settings - and nothing that happened in it.
        /// </summary>
        /// <param name="startsAt">
        /// When the first round of the copy begins. Everything dated moves by the
        /// same amount, measured on the activity's own wall clock.
        /// </param>
        Task<ManagedActivityDto> DuplicateAsync(
            Guid id, string slug, DateTime startsAt, CancellationToken ct);

        /// <summary>
        /// Says this exists for the people taking part, or takes that back.
        ///
        /// <para>
        /// <b>Withdrawing does not undo what happened.</b> Rounds already opened
        /// stay open in every record of them; this stops the scheduler and hides
        /// the activity from the people it was published to, which is the most an
        /// unpublish can honestly claim.
        /// </para>
        /// </summary>
        Task<ManagedActivityDto> SetPublishedAsync(Guid id, bool published, CancellationToken ct);
        Task<ManagedActivityDto> CreateAsync(ActivityInputDto input, CancellationToken ct);
        Task<ActivityDto> EnrolAsync(string idOrSlug, EnrolInputDto input, CancellationToken ct);
        Task<Activity> ResolveAsync(string idOrSlug, CancellationToken ct);

        /// <summary>
        /// Refuses with <b>404</b> unless this activity may be seen at all —
        /// published, and either listed or joined. Asked by the activity's page
        /// and by every sub-resource beside it, so the refusal does not say which
        /// slugs exist.
        /// </summary>
        Task RequireVisibleAsync(Activity activity, CancellationToken ct);
    }

    public class ActivityService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        ISeriesLockdown lockdown,
        IEventHub events,
        IEventAudience audience,
        IGrantService grants,
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
            PageQuery paging, IReadOnlyList<string>? states, IReadOnlyList<string>? types,
            CancellationToken ct)
        {
            var memberships = await MembershipsAsync(ct);
            var now = clock.GetUtcNow().UtcDateTime;

            // **Nothing unpublished reaches this list.** The flag stops the
            // scheduler, and stopping the scheduler alone would leave a copy of
            // last year invisible in the timetable and reachable by anybody who
            // knew its address - which is worse than either state on its own.
            // Whoever may edit it reaches it through the manager's screens, which
            // is where preparing it happens.
            var candidates = await context.Activities
                .AsNoTracking()
                .Where(a => a.ArchivedAt == null && a.PublishedAt != null)
                .ToListAsync(ct);

            var visible = candidates
                .Where(a => memberships.ContainsKey(a.Id)
                    || (!a.Unlisted && a.JoinPolicy != JoinPolicy.Closed))
                .ToList();

            // **Null is every, empty is nothing.** Words this product has no name
            // for narrow to nothing rather than to everything: a filter that
            // cannot be honoured must not widen what it answers with.
            if (states is not null)
            {
                var wanted = states.ToHashSet(StringComparer.OrdinalIgnoreCase);
                visible = visible.Where(a => wanted.Contains(Projections.ActivityState(a, now))).ToList();
            }

            // **The name, not the whole discriminator.** `Activity.Type` is
            // `name@version` and the filter offers `contest`, so a literal
            // comparison would match nothing at all — which is worse than the
            // dead parameter it replaces, because the screen would look as
            // though it were working. It is also what has to keep being true the
            // day somebody publishes `contest@2`. The Client applies the same
            // rule twice already: its fake here, and `typeName` when it picks an
            // icon.
            if (types is not null)
            {
                var wanted = types.ToHashSet(StringComparer.OrdinalIgnoreCase);
                visible = visible.Where(a => wanted.Contains(a.Type.Split('@')[0])).ToList();
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

            // **The list says which of these are out of reach, and shows them
            // anyway.** A row that vanished during an examination reads as a
            // fault; a row that says why reads as a rule.
            var state = await lockdown.ForReaderAsync(ct);

            var items = new List<ActivityDto>(page.Count);
            foreach (var activity in page)
            {
                items.Add(Projections.Activity(
                    activity, Membership(memberships, activity.Id), now,
                    await DocumentsAsync(activity.Id, ct),
                    locked: await LockedAsync(activity.Id, state, ct)));
            }

            return new PageDto<ActivityDto>
            {
                Items = items,
                Total = ordered.Count,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        /// <summary>
        /// Why this activity is out of reach, or null. Shared by the list and
        /// the page so the two cannot say different things about one activity.
        /// </summary>
        private async Task<LockedDto?> LockedAsync(
            Guid activityId, LockdownState state, CancellationToken ct)
        {
            if (state.Quiet) return null;
            if (!await lockdown.IsActivityLockedAsync(activityId, state, ct)) return null;
            return new LockedDto { SeriesName = state.DisplacerFor(activityId)?.SeriesName ?? "" };
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
        /// <summary>
        /// Whether this activity may be seen at all, and <b>404</b> when it may
        /// not.
        /// <para>
        /// Two refusals, both deliberately not 403. An activity being prepared
        /// is invisible even to its members — a copy carries none, but a rebound
        /// placement could, and the only people who see one being prepared are
        /// the people preparing it. And an activity that is neither listed nor
        /// joined must not be confirmed to exist by the shape of the refusal,
        /// because the address is guessable.
        /// </para>
        /// <para>
        /// <b>Here rather than in `GetAsync` alone since 2026-09-09.</b> The
        /// activity's own page answered 404 and every sub-resource beside it —
        /// results, rounds, questions — answered 403 after resolving the same
        /// row, so the refusal itself said which slugs exist. One rule, asked by
        /// all of them.
        /// </para>
        /// </summary>
        public async Task RequireVisibleAsync(Activity activity, CancellationToken ct)
        {
            if (activity.PublishedAt is null
                && !await permissions.HasAsync(Permissions.ActivityUpdate, activity.Id, ct))
            {
                throw new NotFoundException("Activity");
            }

            var memberships = await MembershipsAsync(ct);
            var listed = !activity.Unlisted && activity.JoinPolicy != JoinPolicy.Closed;

            if (!memberships.ContainsKey(activity.Id) && !listed
                && !await permissions.HasAsync(Permissions.ActivityUpdate, activity.Id, ct))
            {
                throw new NotFoundException("Activity");
            }
        }

        public async Task<ActivityDto> GetAsync(string idOrSlug, CancellationToken ct)
        {
            var activity = await ResolveAsync(idOrSlug, ct);
            await RequireVisibleAsync(activity, ct);

            var memberships = await MembershipsAsync(ct);

            return Projections.Activity(
                activity,
                Membership(memberships, activity.Id),
                clock.GetUtcNow().UtcDateTime,
                await DocumentsAsync(activity.Id, ct),
                // **On the one activity, not on the list.** A roster per row
                // would be a query per row for something a list has no room to
                // print, and the screen that needs it is the one somebody opened.
                await MyGroupAsync(activity.Id, ct),
                // **The shell still loads while it is locked**, and that is the
                // point: it is what carries the reason. Everything under it —
                // the series, the problems, the submit path — refuses.
                await LockedAsync(activity.Id, await lockdown.ForReaderAsync(ct), ct));
        }

        /// <summary>
        /// The reader's own group here, with everyone in it.
        /// <para>
        /// Their own only. Whose roster anybody else may read is the ranking's
        /// question, and the activity's <c>ShowGroupMembers</c> answers it.
        /// </para>
        /// </summary>
        private async Task<MyGroupDto?> MyGroupAsync(Guid activityId, CancellationToken ct)
        {
            var me = currentUser.UserId;
            if (me is null) return null;

            var group = await context.Grants.AsNoTracking()
                .Where(g => g.ActivityId == activityId && g.UserId == me && g.GroupId != null)
                .Select(g => g.Group!)
                .FirstOrDefaultAsync(ct);
            if (group is null) return null;

            var members = await context.Grants.AsNoTracking()
                .Where(g => g.GroupId == group.Id)
                .Include(g => g.User)
                .ToListAsync(ct);

            return new MyGroupDto
            {
                Id = Wire.Id(group.Id),
                Name = group.Name,
                Description = group.Description,
                Members = members
                    .Where(m => m.User is not null)
                    .Select(m => Projections.DisplayName(m.User!))
                    .OrderBy(n => n, StringComparer.CurrentCulture)
                    .ToList(),
            };
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
                activity, await DocumentsAsync(activity.Id, ct), seriesCount, problemCount, participantCount,
                RunnerTags.CountMatching(
                    await RunnerTags.ApprovedPoolsAsync(context, ct), activity.RunnerTags));
        }

        public async Task<bool> IsPublishedAsync(Guid id, CancellationToken ct) =>
            await context.Activities.AsNoTracking()
                .Where(a => a.Id == id)
                .Select(a => a.PublishedAt)
                .FirstOrDefaultAsync(ct) is not null;

        public async Task<ManagedActivityDto> SetPublishedAsync(
            Guid id, bool published, CancellationToken ct)
        {
            var activity = await context.Activities.FirstOrDefaultAsync(a => a.Id == id, ct)
                ?? throw new NotFoundException("Activity");
            await permissions.RequireAsync(Permissions.ActivityUpdate, activity.Id, ct);

            // Publishing twice is not an event. Keeping the first timestamp is
            // the honest answer to "since when could people see this".
            if (published && activity.PublishedAt is null)
            {
                activity.PublishedAt = clock.GetUtcNow().UtcDateTime;
            }
            else if (!published)
            {
                activity.PublishedAt = null;
            }

            await context.SaveChangesAsync(ct);

            // **Publication is when an activity begins to exist for a reader**,
            // and unpublication is when it stops — which is exactly what the two
            // names already mean to the Client, so neither needs inventing. This
            // is also what wakes `activityCreated`: an activity has no members at
            // the moment it is created, so there is nobody to tell until now.
            var members = await audience.InActivityAsync(activity.Id, Permissions.ActivityRead, ct);
            if (members.Count > 0)
            {
                await events.SendToUsersAsync(
                    members,
                    published ? EventTypes.ActivityCreated : EventTypes.ActivityDeleted,
                    new { activityId = Wire.Id(activity.Id) }, ct);
            }

            var staff = await audience.InActivityAsync(activity.Id, Permissions.ActivityUpdate, ct);
            if (staff.Count > 0)
            {
                await events.SendToUsersAsync(
                    staff, EventTypes.ActivityChanged, new { activityId = Wire.Id(activity.Id) }, ct);
            }

            return await ManagedAsync(activity, ct);
        }

        public async Task<ManagedActivityDto> DuplicateAsync(
            Guid id, string slug, DateTime startsAt, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ActivityCreate, null, ct);
            var source = await context.Activities
                .Include(a => a.Series).ThenInclude(r => r.SeriesProblems)
                .Include(a => a.Series).ThenInclude(r => r.AddressRules)
                .Include(a => a.AttachmentRules)
                .FirstOrDefaultAsync(a => a.Id == id, ct)
                ?? throw new NotFoundException("Activity");

            // Being allowed to make activities is not being allowed to take this
            // one: a copy carries the problems somebody assigned and the settings
            // they chose.
            await permissions.RequireAsync(Permissions.ActivityUpdate, source.Id, ct);

            slug = slug.Trim();
            if (slug.Length == 0) throw new ValidationException("A slug is required", "slug.required");
            if (await context.Activities.AnyAsync(a => a.Slug.ToLower() == slug.ToLower(), ct))
            {
                throw new ConflictException(
                    "An activity with that slug already exists", "activity.slug.taken");
            }

            var shift = ShiftFor(source, startsAt);

            var copy = new Activity
            {
                Slug = slug,
                Name = source.Name,
                Type = source.Type,
                TimeZone = source.TimeZone,
                RankingType = source.RankingType,
                ScoreVisibility = source.ScoreVisibility,
                MaxUploadBytes = source.MaxUploadBytes,
                StartDate = shift(source.StartDate),
                EndDate = shift(source.EndDate),
                HasQuestions = source.HasQuestions,
                HasPrintouts = source.HasPrintouts,
                ShowGroupMembers = source.ShowGroupMembers,
                // The pool the copy is judged on. A copy that fell back to the
                // general Runners would be sent to machines the original was
                // deliberately kept off.
                RunnerTags = [.. source.RunnerTags],
                JoinPolicy = source.JoinPolicy,
                // **Not the password.** The people who took the activity this was
                // copied from know it, and a new cohort would be joinable by the
                // previous one.
                JoinPassword = null,
                Unlisted = source.Unlisted,
                HideEndedSeriesProblems = source.HideEndedSeriesProblems,
                Props = source.Props,
                MaxAttachments = source.MaxAttachments,
                MaxSubmissionsPerProblem = source.MaxSubmissionsPerProblem,
                // **Nothing here is for anybody yet**, which is the whole reason
                // the column exists: a copy has rounds and dates and is not ready
                // for the people who would otherwise land in it.
                PublishedAt = null,
                ArchivedAt = null,
            };

            foreach (var round in source.Series.OrderBy(r => r.Order))
            {
                var copied = new Series
                {
                    Activity = copy,
                    Slug = round.Slug,
                    Name = round.Name,
                    Order = round.Order,
                    StartDate = shift(round.StartDate),
                    EndDate = shift(round.EndDate),
                    RankingFreezeAt = shift(round.RankingFreezeAt),
                    RankingRevealAt = shift(round.RankingRevealAt),
                    RankingVisibleFrom = shift(round.RankingVisibleFrom),
                    RankingVisibleTo = shift(round.RankingVisibleTo),
                    HideProblemsWhilePaused = round.HideProblemsWhilePaused,
                    RevealProblemCount = round.RevealProblemCount,
                    Importance = round.Importance,
                    ImportanceScope = round.ImportanceScope,
                    RestrictionsEnabled = round.RestrictionsEnabled,
                    RunnerTags = round.RunnerTags is null ? null : [.. round.RunnerTags],
                    // **Six fields of state are left at their defaults**, named
                    // here because leaving them out is the point: a copy has
                    // never opened, never closed and never announced anything.
                    // Carrying `StartAnnouncedAt` over would make the scheduler
                    // treat the copy as already announced and stay silent about a
                    // round nobody was ever told about.
                };

                // **The room, and it travels.** Next year may be a different
                // room, and a copy is still made restricted: a manager removing
                // a rule knows the original was closed, where a manager handed
                // an open copy of a closed round is told nothing. A dropped
                // restriction fails open.
                foreach (var rule in round.AddressRules)
                {
                    copied.AddressRules.Add(new SeriesAddressRule
                    {
                        SeriesId = copied.Id,
                        Network = rule.Network,
                        Note = rule.Note,
                    });
                }

                foreach (var assignment in round.SeriesProblems.OrderBy(a => a.Order))
                {
                    copied.SeriesProblems.Add(new SeriesProblem
                    {
                        Series = copied,
                        Activity = copy,
                        ProblemId = assignment.ProblemId,
                        // A pinned version travels with the assignment. A copy
                        // that quietly followed the newest version would set
                        // different work from the activity it was copied from.
                        PinnedProblemVersionId = assignment.PinnedProblemVersionId,
                        Slug = assignment.Slug,
                        Name = assignment.Name,
                        Order = assignment.Order,
                        MaxPoints = assignment.MaxPoints,
                        Config = assignment.Config,
                        // `Spec` is what the submit form offers, `Props` what
                        // the type needs to identify the problem. Both were
                        // dropped until 2026-08-25, so a copied contest offered
                        // no language at all.
                        Spec = assignment.Spec,
                        Props = assignment.Props,
                        MaxUploadBytes = assignment.MaxUploadBytes,
                        MaxAttachments = assignment.MaxAttachments,
                        MaxSubmissions = assignment.MaxSubmissions,
                    });
                }

                copy.Series.Add(copied);
            }

            foreach (var rule in source.AttachmentRules)
            {
                copy.AttachmentRules.Add(new AttachmentRule
                {
                    Activity = copy,
                    Name = rule.Name,
                    Visibility = rule.Visibility,
                });
            }

            context.Activities.Add(copy);
            await context.SaveChangesAsync(ct);

            // **The activity's own roles travel, and the two defaults follow
            // them.** A copy whose participant role still pointed at the
            // original's would put the new cohort under a set the old activity's
            // manager goes on editing — one course quietly deciding another's.
            // A global default is carried as it is: it belongs to nobody.
            //
            // After the save above, and in two more of its own: `Activities` and
            // `Roles` point at each other, so nothing may create both ends at
            // once. EF calls that a circular dependency and refuses it.
            var own = await context.PermissionRoles
                .AsNoTracking()
                .Where(r => r.ActivityId == source.Id)
                .ToListAsync(ct);

            var moved = new Dictionary<Guid, Guid>();
            foreach (var role in own)
            {
                var duplicate = new Role
                {
                    Name = role.Name,
                    Description = role.Description,
                    ActivityId = copy.Id,
                    Permissions = role.Permissions,
                    IsBuiltIn = false,
                };
                context.PermissionRoles.Add(duplicate);
                moved[role.Id] = duplicate.Id;
            }

            if (moved.Count > 0) await context.SaveChangesAsync(ct);

            copy.ParticipantRoleId = Followed(source.ParticipantRoleId, moved);
            copy.ManagerRoleId = Followed(source.ManagerRoleId, moved);
            if (copy.ParticipantRoleId is not null || copy.ManagerRoleId is not null)
            {
                await context.SaveChangesAsync(ct);
            }

            return await ManagedAsync(copy, ct);
        }

        /// <summary>
        /// The copy's version of a role the source named: the duplicate where the
        /// role belonged to the source, and the same row where it is global.
        /// </summary>
        private static Guid? Followed(Guid? chosen, Dictionary<Guid, Guid> moved) =>
            chosen is { } id ? (moved.TryGetValue(id, out var copied) ? copied : id) : null;

        /// <summary>
        /// How far everything dated moves, as a function that leaves nulls alone.
        ///
        /// <para>
        /// <b>Measured on the activity's own wall clock, not in UTC.</b> A round
        /// starting at 09:00 in Warsaw is expected to start at 09:00 in the copy,
        /// and a fixed offset in absolute time moves it to 10:00 whenever the
        /// copy crosses a daylight-saving boundary - which a copy made in
        /// February for October does.
        /// </para>
        ///
        /// <para>
        /// The anchor is the <b>earliest round start</b>, because that is what
        /// "when does this begin" means to whoever is copying; the activity's own
        /// start stands in only when no round has one. With nothing dated at all
        /// there is nothing to move.
        /// </para>
        /// </summary>
        private static Func<DateTime?, DateTime?> ShiftFor(Activity source, DateTime startsAt)
        {
            var anchor = source.Series
                .Select(r => r.StartDate)
                .Where(d => d != null)
                .OrderBy(d => d)
                .FirstOrDefault() ?? source.StartDate;

            return ShiftBy(anchor, startsAt, Zone(source.TimeZone));
        }

        /// <summary>
        /// The same shift, from an anchor a caller chooses.
        /// <para>
        /// Shared with <see cref="SeriesService"/>, which copies one round and
        /// anchors on that round's own start, in the <b>target</b> activity's
        /// zone — the round will run there, so that is the clock its end and its
        /// freeze should keep their hour on.
        /// </para>
        /// </summary>
        internal static Func<DateTime?, DateTime?> ShiftBy(
            DateTime? anchor, DateTime startsAt, TimeZoneInfo zone)
        {
            if (anchor is null) return _ => null;

            var from = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(anchor.Value, DateTimeKind.Utc), zone);
            var to = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(startsAt, DateTimeKind.Utc), zone);
            var delta = to - from;

            return moment =>
            {
                if (moment is null) return null;
                var local = TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(moment.Value, DateTimeKind.Utc), zone) + delta;
                return TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
            };
        }

        /// <summary>
        /// The activity's zone, or UTC when this installation does not know it.
        /// Windows and Linux disagree about zone ids, and a copy refusing to
        /// happen over that would be worse than one measured in UTC.
        /// </summary>
        internal static TimeZoneInfo Zone(string id)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.Utc;
            }
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
            SubmissionLimits.Check(
                input.MaxUploadBytes, input.MaxAttachments, input.MaxSubmissionsPerProblem, "activity");
            var activity = new Activity
            {
                // **Made deliberately, so it exists deliberately.** The
                // unpublished state is for a copy, which nobody sat down and
                // wrote: somebody making an activity from nothing has already
                // decided it should be there, and asking them to say so twice
                // would be ceremony rather than a safeguard.
                PublishedAt = clock.GetUtcNow().UtcDateTime,
                Slug = slug,
                Name = input.Name?.Trim() is { Length: > 0 } name ? name : slug,
                Type = input.Type ?? "contest@1",
                RankingType = input.RankingType ?? "icpc",
                TimeZone = input.TimeZone ?? "Europe/Warsaw",
                StartDate = ParseInstant(input.StartDate),
                EndDate = ParseInstant(input.EndDate),
                HasQuestions = input.Modules?.Questions ?? true,
                HasPrintouts = input.Modules?.Printouts ?? false,
                ScoreVisibility = ParseScoreVisibility(input.ScoreVisibility),
                JoinPolicy = policy,
                // Only kept under `password`, so switching to open and back does
                // not quietly restore a code somebody had already shared.
                JoinPassword = policy == JoinPolicy.Password ? input.JoinPassword : null,
                // Under `closed` this is what the policy already means, so it is
                // forced rather than left to disagree with it.
                Unlisted = policy == JoinPolicy.Closed || (input.Unlisted ?? false),
                HideEndedSeriesProblems = input.HideEndedSeriesProblems ?? false,
                ShowGroupMembers = input.ShowGroupMembers ?? false,
                Props = Opaque.Store(input.Props, "props"),
                MaxUploadBytes = input.MaxUploadBytes ?? 8L * 1024 * 1024,
                MaxAttachments = input.MaxAttachments ?? 1,
                MaxSubmissionsPerProblem = input.MaxSubmissionsPerProblem,
                RunnerTags = RunnerTags.Validated(input.RunnerTags, "The activity's Runner tags"),
            };

            foreach (var rule in input.AttachmentVisibility ?? ConventionalAttachments)
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
            // The shipped `manager` role, not this activity's own choice: the
            // activity is being created, so it has none yet and could not have
            // one — a role of its own is written afterwards, if at all.
            var managerRole = await DefaultRoles.GlobalAsync(context, DefaultRoles.Manager, ct);
            var manages = new Grant
            {
                UserId = user.Id,
                ActivityId = activity.Id,
                IsSystem = true,
                State = GrantState.Active,
                GrantedByUserId = user.Id,
            };
            DefaultRoles.Carry(manages, managerRole, Permissions.ManagerTemplate, DefaultRoles.Manager);
            context.Grants.Add(manages);

            await context.SaveChangesAsync(ct);
            return await ManagedAsync(activity, ct);
        }

        /// <summary>
        /// Self-enrolment. A manager may always enrol somebody by hand — that is
        /// what a grant is — so this is the answer to <b>self</b>-enrolment and
        /// nothing else.
        /// </summary>
        public async Task<ActivityDto> EnrolAsync(
            string idOrSlug, EnrolInputDto input, CancellationToken ct)
        {
            var activity = await ResolveAsync(idOrSlug, ct);
            var user = await currentUser.RequireAsync(ct);

            if (activity.ArchivedAt is not null)
            {
                throw new ConflictException("An archived activity accepts no enrolment", "activity.archived");
            }

            var existing = await context.Grants
                .FirstOrDefaultAsync(g => g.UserId == user.Id && g.ActivityId == activity.Id, ct);

            if (existing is { State: GrantState.Active })
            {
                // Already in. Not an error — a link gets opened twice — so the
                // activity comes back as they already see it.
                return await ProjectAsync(activity, ct);
            }

            // An invitation is accepted rather than re-checked: the policy
            // governs who may let themselves in, and somebody already invited
            // was let in by a manager.
            if (existing is { State: GrantState.Invited })
            {
                existing.State = GrantState.Active;
                await context.SaveChangesAsync(ct);
                await grants.AnnounceEnrolmentAsync(existing, ct);
                return await ProjectAsync(activity, ct);
            }

            // **An activity being prepared takes nobody.** `GetAsync` answers
            // 404 for an unpublished one, because the address is guessable and
            // the only people who see one being prepared are the people
            // preparing it. Enrolling checked the archive and the join policy and
            // not this until 2026-09-09 — and then returned the projection it had
            // just granted access to. `DuplicateAsync` produces exactly the
            // vulnerable shape: unpublished, with the original's join policy
            // copied.
            if (activity.PublishedAt is null
                && !await permissions.HasAsync(Permissions.ActivityUpdate, activity.Id, ct))
            {
                throw new NotFoundException("Activity");
            }

            switch (activity.JoinPolicy)
            {
                case JoinPolicy.Closed:
                    throw new ForbiddenActionException(
                        "Only an organiser can enrol somebody here", "enrolment.closed");

                case JoinPolicy.Password:
                    // Compared in fixed time. It is a join code rather than a
                    // credential, but it is still a secret somebody could guess
                    // at, and a comparison that returns early tells them how far
                    // they got.
                    var given = System.Text.Encoding.UTF8.GetBytes(input.Password ?? "");
                    var wanted = System.Text.Encoding.UTF8.GetBytes(activity.JoinPassword ?? "");
                    if (wanted.Length == 0
                        || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, wanted))
                    {
                        throw new ForbiddenActionException(
                            "The join password is wrong", "enrolment.password");
                    }
                    break;
            }

            // **This activity's participant role**, which is the point of the
            // setting: a manager decides once what joining their course means,
            // and every later self-enrolment carries it without anybody choosing.
            var role = await DefaultRoles.ForEnrolmentAsync(context, activity.Id, runsIt: false, ct);
            var joined = new Grant
            {
                UserId = user.Id,
                ActivityId = activity.Id,
                State = GrantState.Active,
            };
            DefaultRoles.Carry(joined, role, Permissions.ParticipantTemplate, DefaultRoles.Participant);

            // Derived rather than assumed false. An ordinary participant role
            // carries nothing a participant does not hold, which is what puts
            // them in the ranking — but an activity may have chosen a role that
            // does, and somebody counted as a competitor while holding staff keys
            // is the one thing this flag exists to prevent.
            joined.IsSystem = Permissions.IsStaff(
                Permissions.Effective(role?.Permissions, joined.Permissions));
            context.Grants.Add(joined);
            await context.SaveChangesAsync(ct);
            await grants.AnnounceEnrolmentAsync(joined, ct);

            return await ProjectAsync(activity, ct);
        }

        private async Task<ActivityDto> ProjectAsync(Activity activity, CancellationToken ct) =>
            Projections.Activity(
                activity,
                Membership(await MembershipsAsync(ct), activity.Id),
                clock.GetUtcNow().UtcDateTime,
                await DocumentsAsync(activity.Id, ct));

        internal static DateTime? ParseInstant(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal);

        /// <summary>
        /// What a submission's attachments say when the creator said nothing.
        /// <para>
        /// <b>Absent is not empty.</b> An activity made through the panel names
        /// all three; one made through the raw API named none and got a table
        /// with no rows — and a name with no row is managers-only, so its
        /// authors could not read back their own source. Every other door that
        /// makes an activity fills this in: the seeder, <c>ParityWorld</c>, and
        /// the Client's own form, which is where these three values come from.
        /// </para>
        /// <para>
        /// <b>An explicitly empty list still means none.</b> That is why this is
        /// applied to the absent input rather than to an already-defaulted one.
        /// </para>
        /// <para>
        /// <c>log</c> is stored even though it equals the no-row default: the
        /// editor draws one switch per row, so an activity with no rows offers a
        /// manager nothing to change.
        /// </para>
        /// </summary>
        private static readonly IReadOnlyList<AttachmentRuleDto> ConventionalAttachments =
        [
            new() { Name = "source", Visibility = "participant" },
            new() { Name = "details", Visibility = "participant" },
            new() { Name = "log", Visibility = "managersOnly" },
        ];

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

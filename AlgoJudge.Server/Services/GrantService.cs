using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IGrantService
    {
        Task<PageDto<GrantDto>> ListAsync(
            PageQuery paging, string? userId, Guid? activityId, string? scope, CancellationToken ct);
        Task<GrantDto> SetAsync(GrantInputDto input, CancellationToken ct);
        Task RevokeAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Adds roles to somebody's grant on an activity, creating the grant if
        /// there is none. <b>Never removes one</b>, and never revises what a
        /// person decided.
        /// <para>
        /// The one path an assertion from outside takes — a launch, a roster
        /// read — and it is here rather than in the module that calls it so that
        /// the staff flag, the announcement and the one-grant-per-activity rule
        /// have a single implementation.
        /// </para>
        /// </summary>
        Task<EnrollmentOutcome> AddRolesAsync(
            string userId, Guid activityId, IReadOnlyList<Guid> roleIds,
            Guid? sourceProviderId, CancellationToken ct);

        /// <summary>
        /// Somebody has just joined an activity, or accepted an invitation to.
        /// <para>
        /// Announced from here because this owns who hears a grant change — the
        /// rule is four lines and a second copy of it in <c>ActivityService</c>
        /// is a second answer to "who may see the roster". Enrolling was silent
        /// until 2026-09-14, so a manager watching people arrive saw nothing
        /// arrive.
        /// </para>
        /// </summary>
        Task AnnounceEnrollmentAsync(Grant grant, CancellationToken ct);

        /// <summary>
        /// Refuses a set the caller could not hand out at this scope.
        /// <para>
        /// On the interface because a role is chosen in two places: here, where
        /// one is written, and in an activity's settings, where one is named as
        /// what enrollment carries. A second copy of the rule is a second answer
        /// to "may this person hand this out", and the copies drift.
        /// </para>
        /// </summary>
        /// <param name="already">
        /// What the thing being written already carries, exempt from the rule:
        /// the excess check is about what this write <b>adds</b>.
        /// </param>
        Task RequireGrantableRoleAsync(
            Guid? activityId, IReadOnlyList<string> wanted,
            IReadOnlyList<string>? already, CancellationToken ct);

        Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid? activityId, CancellationToken ct);

        /// <summary>The shipped role of this kind, by its key rather than its name.</summary>
        Task<Role?> BuiltInRoleAsync(string builtInKey, CancellationToken ct);
        Task<RoleDto> CreateRoleAsync(RoleInputDto input, CancellationToken ct);
        Task<RoleDto> UpdateRoleAsync(Guid id, RoleInputDto input, CancellationToken ct);
        Task DeleteRoleAsync(Guid id, CancellationToken ct);
    }

    /// <summary>What an assertion from outside did to somebody's membership.</summary>
    public enum EnrollmentOutcome
    {
        /// <summary>The grant existed and already carried every role. Nothing was written.</summary>
        Unchanged = 0,

        /// <summary>Roles were added to a grant that already existed.</summary>
        Added = 1,

        /// <summary>There was no grant, so there is one now.</summary>
        Created = 2,

        /// <summary>
        /// The grant carries the override flag, so nothing was touched:
        /// stepping down inside one activity is a decision a launch may not
        /// undo.
        /// </summary>
        SkippedOverride = 3,
    }

    public class GrantService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IEventHub events,
        IEventAudience audience,
        TimeProvider clock
    ) : IGrantService
    {

        /// <summary>
        /// Tells whoever may read grants that one changed.
        /// <para>
        /// Scoped to the activity where the grant is in one, and to the whole
        /// installation where it is a system grant — a system grant is not any
        /// activity's business, and narrowing it to one would tell the wrong
        /// people. The affected user is told too: what they may do has changed,
        /// and their own screens read that.
        /// </para>
        /// </summary>
        public Task AnnounceEnrollmentAsync(Grant grant, CancellationToken ct) =>
            AnnounceGrantAsync(grant.ActivityId, grant.UserId, new { grant = Projected(grant) }, ct);

        private async Task AnnounceGrantAsync(
            Guid? activityId, string subjectUserId, object payload, CancellationToken ct)
        {
            var readers = activityId is { } id
                ? await audience.InActivityAsync(id, Permissions.GrantReadAll, ct)
                : await audience.AnywhereAsync(Permissions.GrantReadAll, ct);

            var recipients = new HashSet<string>(readers, StringComparer.Ordinal) { subjectUserId };
            await events.SendToUsersAsync([.. recipients], EventTypes.GrantChanged, payload, ct);
        }

        /// <summary>
        /// Everybody who may read a role is told one changed.
        /// <para>
        /// <b>This is no longer only a list changing.</b> A grant points at a
        /// role, so an edit changes what its holders may do — every screen
        /// showing a permission has to reload, not just the role list.
        /// </para>
        /// </summary>
        private async Task AnnounceRoleAsync(
            RoleDto? role, string? deletedId, CancellationToken ct)
        {
            // Both keys, matching who `ListRolesAsync` admits. Telling a narrower
            // audience than may read the list is how the template list used to
            // push changes at people it then refused to serve.
            var readers = new HashSet<string>(
                await audience.AnywhereAsync(Permissions.RoleRead, ct), StringComparer.Ordinal);
            readers.UnionWith(await audience.AnywhereAsync(Permissions.GrantUpdate, ct));
            if (readers.Count == 0) return;

            await events.SendToUsersAsync([.. readers], EventTypes.RoleChanged,
                deletedId is null ? new { role } : new { deletedId }, ct);
        }
        public async Task<PageDto<GrantDto>> ListAsync(
            PageQuery paging, string? userId, Guid? activityId, string? scope, CancellationToken ct)
        {
            // Scoped as the panel's other lists are. **The narrowing had to be
            // written here**: this list never had one, so a manager whose grant
            // is on an activity was refused rather than shown the grants of the
            // activity they manage.
            var allowed = await permissions.ListScopeAsync(Permissions.GrantReadAll, activityId, ct);

            var query = Loaded(context.Grants.AsNoTracking());

            // **A system grant is not an activity's business.** Somebody holding
            // the key on activities alone reads those activities' grants and no
            // others: the installation's own grants answer to it held at system
            // scope, which is the difference between running a course and
            // running the installation.
            if (allowed is not null)
            {
                var ids = allowed.ToHashSet();
                query = query.Where(g => g.ActivityId != null && ids.Contains(g.ActivityId.Value));
            }

            if (userId is not null) query = query.Where(g => g.UserId == userId);
            if (activityId is { } id) query = query.Where(g => g.ActivityId == id);
            if (scope == "global") query = query.Where(g => g.ActivityId == null);
            if (scope == "activity") query = query.Where(g => g.ActivityId != null);

            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(g => g.CreatedAt).ThenBy(g => g.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            return new PageDto<GrantDto>
            {
                Items = page.Select(Projected).ToList(),
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        /// <summary>
        /// One grant on the wire. Internal rather than private because
        /// <see cref="ActivityGroupService"/> answers with one after a move, and
        /// two projections of the same row would drift.
        /// </summary>
        internal static GrantDto Projected(Grant grant) => new()
        {
            Id = Wire.Id(grant.Id),
            UserId = grant.UserId,
            UserName = grant.User is null ? grant.UserId : Projections.DisplayName(grant.User),
            UserLogin = grant.User?.UserName ?? grant.UserId,
            ActivityId = grant.ActivityId is { } a ? Wire.Id(a) : null,
            ActivityName = grant.Activity?.Name,
            GroupId = grant.GroupId is { } group ? Wire.Id(group) : null,
            GroupName = grant.Group?.Name,
            Permissions = Parse(grant.Permissions),
            Roles = [.. grant.Roles
                .Where(r => r.DismissedAt is null)
                .OrderBy(r => r.Role?.Name, StringComparer.Ordinal)
                .Select(ProjectedRoleLink)],
            DismissedRoles = [.. grant.Roles
                .Where(r => r.DismissedAt is not null)
                .OrderBy(r => r.Role?.Name, StringComparer.Ordinal)
                .Select(ProjectedRoleLink)],
            IsSystem = grant.IsSystem,
            StaffByHand = grant.StaffByHand,
            State = grant.State == GrantState.Invited ? "invited" : "active",
            CreatedAt = Wire.At(grant.CreatedAt),
            Source = grant.SourceProviderId is null ? "manual" : "provider",
            SourceProviderId = grant.SourceProviderId is { } p ? Wire.Id(p) : null,
            SourceProviderName = grant.SourceProvider?.DisplayName,
            // A provider's system contribution, and nothing else. An activity
            // grant is editable whoever wrote it.
            Managed = grant.ActivityId is null && grant.SourceProviderId is not null,
            OverrideSystem = grant.OverrideSystem,
        };

        private static GrantRoleDto ProjectedRoleLink(GrantRole link) => new()
        {
            RoleId = Wire.Id(link.RoleId),
            Name = link.Role?.Name ?? "",
            Permissions = Parse(link.Role?.Permissions ?? "[]"),
            ActivityId = link.Role?.ActivityId is { } owner ? Wire.Id(owner) : null,
            SourceProviderId = link.SourceProviderId is { } source ? Wire.Id(source) : null,
            SourceProviderName = link.SourceProvider?.DisplayName,
            DismissedAt = link.DismissedAt is { } at ? Wire.At(at) : null,
        };

        /// <summary>
        /// Everything a grant's wire shape needs: the person, the activity, the
        /// group, the source, and every role with its own source.
        /// <para>
        /// One place, because a reader that forgot the roles used to answer with
        /// an empty set rather than with an error — which is how a group move
        /// came to report a grant carrying no permissions at all.
        /// </para>
        /// </summary>
        internal static IQueryable<Grant> Loaded(IQueryable<Grant> grants) => grants
            .Include(g => g.User)
            .Include(g => g.Activity)
            .Include(g => g.SourceProvider)
            .Include(g => g.Group)
            .Include(g => g.Roles).ThenInclude(r => r.Role)
            .Include(g => g.Roles).ThenInclude(r => r.SourceProvider);

        private static IReadOnlyList<string> Parse(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        /// <summary>
        /// Whether this user holds <c>system:administrator</c> — in <b>any</b> of
        /// their system contributions, since there may now be several.
        /// <para>
        /// Asked about somebody else, so it cannot go through
        /// <see cref="IPermissionService"/>, which answers about the caller.
        /// </para>
        /// </summary>
        private async Task<bool> IsAdministratorAsync(string userId, CancellationToken ct)
        {
            var systemGrants = await context.Grants
                .AsNoTracking()
                .Where(g => g.UserId == userId && g.ActivityId == null && g.State == GrantState.Active)
                .Held()
                .ToListAsync(ct);

            return systemGrants.Any(g => g.Confers().Contains(Permissions.SystemAdministrator));
        }

        /// <summary>
        /// Whether somebody other than this grant still administers the
        /// installation.
        /// <para>
        /// <b>Excluded by id, not by user</b>, so the question is "afterwards"
        /// rather than "now" — and it stays right when the row being rewritten
        /// belongs to somebody else.
        /// </para>
        /// <para>
        /// Active system grants only, exactly as <see cref="PermissionService"/>
        /// reads them: an invited grant confers nothing, and the key means
        /// nothing in an activity grant. The source column is deliberately not
        /// filtered — a managed contribution can never carry this key, and one
        /// that somehow did would still be honored by the resolver, so counting
        /// it errs the safe way.
        /// </para>
        /// </summary>
        private async Task<bool> AnotherAdministratorAsync(Guid excluding, CancellationToken ct)
        {
            var others = await context.Grants
                .AsNoTracking()
                .Where(g => g.Id != excluding && g.ActivityId == null && g.State == GrantState.Active)
                .Held()
                .ToListAsync(ct);

            return others.Any(g => g.Confers().Contains(Permissions.SystemAdministrator));
        }

        /// <summary>
        /// Refuses to take the installation's last administrator away.
        /// <para>
        /// <b>There is no way back.</b> <c>aj-admin</c> has no command for
        /// grants, and the seeder restores one only where the installation has
        /// none at all — so this is repaired through the database or not at all.
        /// A revoke and a rewrite both reach here, because demoting the row to
        /// <c>invited</c> takes the installation away just as completely as
        /// deleting the key: the resolver loads active grants only.
        /// </para>
        /// </summary>
        /// <param name="grant">The row about to be removed or rewritten.</param>
        /// <param name="held">
        /// What it carries now — its role and its own entries together, since
        /// either half may be where the key is.
        /// </param>
        /// <param name="stillAdministers">Whether it administers afterwards.</param>
        private async Task RefuseLosingTheLastAdministratorAsync(
            Grant grant, IReadOnlyList<string> held, bool stillAdministers, CancellationToken ct)
        {
            if (stillAdministers) return;

            // Read the row before asking the database: almost nothing reaching
            // here is an administrator's grant, and parsing beats a query.
            if (grant.ActivityId is not null
                || grant.State != GrantState.Active
                || !held.Contains(Permissions.SystemAdministrator))
            {
                return;
            }

            if (await AnotherAdministratorAsync(grant.Id, ct)) return;

            throw new ForbiddenActionException(
                "This is the installation's last system:administrator grant. "
                    + "Grant it to somebody else first",
                "grant.administrator.last");
        }

        /// <summary>
        /// Which providers name this role — through a mapping rule or as their
        /// default. Both count: either way, deleting it leaves a provider
        /// pointing at nothing.
        /// </summary>
        private async Task<IReadOnlyList<string>> ReferencingProvidersAsync(Guid roleId, CancellationToken ct)
        {
            var byRule = await context.IdentityProviderMappingRules
                .Where(r => r.RoleId == roleId)
                .Select(r => r.Provider!.Slug)
                .ToListAsync(ct);

            var byDefault = await context.IdentityProviderDefaultRoles
                .Where(d => d.RoleId == roleId)
                .Select(d => d.Provider!.Slug)
                .ToListAsync(ct);

            return [.. byRule.Concat(byDefault).Distinct().OrderBy(s => s, StringComparer.Ordinal)];
        }

        /// <summary>
        /// Creates or replaces <b>the manual contribution</b> for one user in one
        /// scope.
        /// <para>
        /// One manual contribution per user per scope, which the database
        /// enforces. "A manager with the right to update something taken away" is
        /// this set with that entry removed, not a second grant layered over a
        /// first.
        /// </para>
        /// <para>
        /// <b>At system scope it never touches a provider's contribution.</b>
        /// Those belong to a provider's mapping and are rewritten at every
        /// sign-in, so an edit here would last exactly until that person next
        /// signed in — and a change that silently reverts is worse than one that
        /// is refused.
        /// </para>
        /// <para>
        /// <b>At activity scope there is one grant, whoever wrote it</b>, and
        /// this edits that one. A launch used to make a membership
        /// uncorrectable: the lookup matched a null source, missed the row a
        /// platform had written, inserted a second and broke on the unique index
        /// — a manager saw "concurrency.conflict" and could neither change
        /// somebody's role nor take a staff flag off them.
        /// </para>
        /// </summary>
        public async Task<GrantDto> SetAsync(GrantInputDto input, CancellationToken ct)
        {
            Guid? activityId = input.ActivityId is { } raw && Guid.TryParse(raw, out var parsed)
                ? parsed
                : null;

            await permissions.RequireAsync(Permissions.GrantUpdate, activityId, ct);
            var issuer = await currentUser.RequireAsync(ct);

            var wanted = input.Permissions?.Distinct().ToList() ?? [];

            var unknown = Permissions.Unknown(wanted);
            if (unknown.Count > 0)
            {
                throw new ValidationException(
                    "No such permission: " + string.Join(", ", unknown), "grant.permission.unknown");
            }

            // Absent leaves the links alone; a list replaces them. What is
            // already linked is what the excess rule exempts either way.
            var roles = await RolesForGrantAsync(input.RoleIds, activityId, ct);

            // **Every rule below reads the union, not the additions.** A grant
            // carries its roles' permissions as surely as its own, so an excess
            // check that looked only at what was typed in would let anybody with
            // `grant:update` hand out an administrator's role by pointing at it.
            var held = Permissions.Effective(
                roles.Select(r => (string?)r.Permissions), JsonSerializer.Serialize(wanted));

            if (!await context.Users.AnyAsync(u => u.Id == input.UserId, ct))
            {
                throw new NotFoundException("User");
            }
            if (activityId is { } scoped && !await context.Activities.AnyAsync(a => a.Id == scoped, ct))
            {
                throw new NotFoundException("Activity");
            }

            // **One grant per activity, whoever wrote it; the manual one at
            // system scope.** The source filter belongs to system scope alone,
            // where a person's permissions are the union of several rows and
            // this endpoint owns exactly one of them.
            var grant = await context.Grants
                .Include(g => g.Roles).ThenInclude(r => r.Role)
                .FirstOrDefaultAsync(
                    g => g.UserId == input.UserId
                        && g.ActivityId == activityId
                        && (activityId != null || g.SourceProviderId == null), ct);

            if (grant is null)
            {
                grant = new Grant
                {
                    UserId = input.UserId,
                    ActivityId = activityId,
                    GrantedByUserId = issuer.Id,
                };
                context.Grants.Add(grant);
            }

            // What this write adds, which is what the excess rule asks about.
            // **The difference, not the whole set**: a manager re-saving a
            // membership a platform gave an instance role they do not hold would
            // otherwise be refused forever, and removing a permission would be
            // refused for adding one.
            var alreadyLinked = grant.Roles
                .Where(r => r.DismissedAt is null && r.Role is not null)
                .Select(r => r.Role!.Permissions);
            var alreadyHeld = new HashSet<string>(
                Permissions.Effective(alreadyLinked, grant.Permissions), StringComparer.Ordinal);

            var mine = await permissions.EffectiveAsync(activityId, ct);
            if (!mine.Contains(Permissions.SystemAdministrator))
            {
                var excess = held
                    .Where(p => !mine.Contains(p) && !alreadyHeld.Contains(p))
                    .ToList();
                if (excess.Count > 0)
                {
                    throw new ForbiddenActionException(
                        "Cannot grant permissions you do not hold: " + string.Join(", ", excess),
                        "grant.excess");
                }
            }

            // **`system:administrator` is a system grant's key, and only ever
            // one.** The resolver requires `ActivityId is null` before honoring
            // it, so written into an activity grant it confers nothing at all —
            // and the danger is exactly that it looks as though it does: the
            // panel shows somebody holding it while every check disagrees,
            // silently.
            //
            // **After the rule above, and that is where it belongs.** Anybody
            // else writing this key is already refused by the excess rule, for a
            // better reason — they do not hold it. The one actor that rule
            // exempts is an administrator, and this is the case it leaves.
            //
            // **Only this key; the general rule is not enforced.** The catalog
            // declares a scope for all of them, but five of the shipped
            // `manager` role's are `Global` — the `problem:*` ones — and the
            // panel hands that role to activity grants. Refusing every misplaced
            // global key would refuse the role this product ships.
            if (activityId is not null && held.Contains(Permissions.SystemAdministrator))
            {
                throw new ValidationException(
                    "system:administrator is installation-wide; it means nothing in an activity grant",
                    "grant.permission.scope");
            }

            // The override, and the one rule about who may set it.
            //
            // An administrator's rights are not trimmable from below — that has
            // been true since the model was written — but an administrator who
            // wants to compete in one contest has to be able to step down there.
            // Both survive if the flag is **self-initiated only** for them:
            // nobody else may set it, and anybody with `grant:update` in the
            // activity may clear it. Clearing has to stay open, because the flag
            // suppresses the very permissions its holder would need to undo it.
            var wantsOverride = activityId is not null && input.OverrideSystem == true;
            if (wantsOverride && !grant.OverrideSystem
                && input.UserId != issuer.Id
                && await IsAdministratorAsync(input.UserId, ct))
            {
                throw new ForbiddenActionException(
                    "Only an administrator may set the override on their own grant",
                    "grant.override.administrator");
            }
            grant.OverrideSystem = wantsOverride;

            // **The state is half of the question.** `State` is written from the
            // input two lines below, and demoting the last administrator's grant
            // to `invited` takes the installation away as completely as dropping
            // the key. A grant this method has just constructed carries `"[]"`,
            // so creating one is never refused.
            var stillAdministers = held.Contains(Permissions.SystemAdministrator)
                && input.State != "invited";
            await RefuseLosingTheLastAdministratorAsync(grant, [.. alreadyHeld], stillAdministers, ct);

            if (input.RoleIds is not null)
            {
                LinkRoles(
                    grant, [.. roles.Select(r => r.Id)], source: null, clock.GetUtcNow().UtcDateTime);
            }
            grant.Permissions = JsonSerializer.Serialize(wanted);
            grant.State = input.State == "invited" ? GrantState.Invited : GrantState.Active;
            // Settled here, never taken from the caller: a grant carrying any
            // permission a participant does not hold is staff, always — and that
            // is what keeps a jury member out of the ranking. What a person
            // decided lives in its own column, so this can be lowered again when
            // the permissions stop implying it.
            grant.StaffByHand = input.StaffByHand == true;
            grant.IsSystem = grant.StaffByHand || Permissions.IsStaff(held);

            await context.SaveChangesAsync(ct);

            var stored = await Loaded(context.Grants.AsNoTracking())
                .FirstAsync(g => g.Id == grant.Id, ct);
            var projected = Projected(stored);
            await AnnounceGrantAsync(stored.ActivityId, stored.UserId, new { grant = projected }, ct);
            return projected;
        }

        /// <inheritdoc />
        public async Task<EnrollmentOutcome> AddRolesAsync(
            string userId, Guid activityId, IReadOnlyList<Guid> roleIds,
            Guid? sourceProviderId, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            // One row per person per activity, whoever wrote it. Looking this up
            // by source as well found nothing when a second course had already
            // granted the same activity, and the insert then broke on the unique
            // index — a launch from the second Moodle answering 500.
            var grant = await context.Grants
                .Include(g => g.Roles).ThenInclude(r => r.Role)
                .FirstOrDefaultAsync(g => g.UserId == userId && g.ActivityId == activityId, ct);

            // **An override is somebody standing down inside this activity.**
            // A launch adding roles to it would hand back exactly what they gave
            // up, on the platform's word rather than on theirs.
            if (grant is { OverrideSystem: true }) return EnrollmentOutcome.SkippedOverride;

            var created = grant is null;
            if (grant is null)
            {
                grant = new Grant { UserId = userId, ActivityId = activityId };
                context.Grants.Add(grant);
            }

            var added = false;
            foreach (var roleId in roleIds.Distinct())
            {
                var existing = grant.Roles.FirstOrDefault(r => r.RoleId == roleId);

                // **A role somebody took away stays away.** Adding it back is
                // the whole of what "a launch never removes" would otherwise
                // undo, one relaunch later, with nothing on screen to explain
                // it.
                if (existing is not null) continue;

                // **Through the set as well as the collection.** A dependent
                // reached only through a navigation is tracked as `Modified`
                // when its key is already set — every id here is — so the save
                // updates a row that does not exist and fails as a concurrency
                // conflict on a grant nobody else touched.
                var link = new GrantRole
                {
                    GrantId = grant.Id,
                    RoleId = roleId,
                    SourceProviderId = sourceProviderId,
                    AddedAt = now,
                };
                grant.Roles.Add(link);
                context.GrantRoles.Add(link);
                added = true;
            }

            if (!created && !added) return EnrollmentOutcome.Unchanged;

            // An enrollment never demotes: it adds, and `invited` is an offer
            // that an assertion from the platform answers.
            grant.State = GrantState.Active;

            var roleJsons = await RoleJsonsAsync(grant, ct);
            grant.IsSystem = grant.StaffByHand
                || Permissions.IsStaff(Permissions.Effective(roleJsons, grant.Permissions));

            await context.SaveChangesAsync(ct);

            var stored = await Loaded(context.Grants.AsNoTracking())
                .FirstAsync(g => g.Id == grant.Id, ct);
            await AnnounceGrantAsync(activityId, userId, new { grant = Projected(stored) }, ct);

            return created ? EnrollmentOutcome.Created : EnrollmentOutcome.Added;
        }

        /// <summary>
        /// The stored permission sets of every role a grant holds, including the
        /// links this request has just added, which are not in the database yet.
        /// </summary>
        private async Task<List<string?>> RoleJsonsAsync(Grant grant, CancellationToken ct)
        {
            var ids = grant.Roles
                .Where(r => r.DismissedAt is null)
                .Select(r => r.RoleId)
                .Distinct()
                .ToList();

            return await context.PermissionRoles
                .AsNoTracking()
                .Where(r => ids.Contains(r.Id))
                .Select(r => (string?)r.Permissions)
                .ToListAsync(ct);
        }

        /// <inheritdoc />
        public Task<Role?> BuiltInRoleAsync(string builtInKey, CancellationToken ct) =>
            context.PermissionRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.BuiltInKey == builtInKey, ct);

        /// <summary>
        /// Revoking removes the row — a grant has no revoked state, only
        /// `invited` and `active` — so it is a delete rather than an action. In
        /// an activity that also removes the membership, because the grant is
        /// the membership.
        /// </summary>
        public async Task RevokeAsync(Guid id, CancellationToken ct)
        {
            var grant = await context.Grants
                .Include(g => g.Roles).ThenInclude(r => r.Role)
                .FirstOrDefaultAsync(g => g.Id == id, ct)
                ?? throw new NotFoundException("Grant");

            await permissions.RequireAsync(Permissions.GrantUpdate, grant.ActivityId, ct);

            // A provider's contribution is the provider's, and revoking one here
            // would last until that person next signed in. What actually takes it
            // away is changing the mapping, unlinking the provider, or blocking
            // the account — the coarse instruments a union leaves, and the cost
            // recorded when the union was accepted.
            //
            // **System scope only.** An activity grant is somebody's membership
            // whoever wrote it; a launch may put them back, and a manager saying
            // "not in this course" is still a thing a manager may say.
            if (grant.ActivityId is null && grant.SourceProviderId is not null)
            {
                throw new ForbiddenActionException(
                    "This contribution comes from an identity provider and is rewritten at every sign-in. "
                        + "Change the provider's mapping instead",
                    "grant.managed");
            }

            // After the managed refusal above, which is cheaper and more
            // specific when both apply.
            await RefuseLosingTheLastAdministratorAsync(
                grant,
                Permissions.Effective(
                    grant.Roles.Where(r => r.DismissedAt is null).Select(r => r.Role?.Permissions),
                    grant.Permissions),
                stillAdministers: false,
                ct);

            // Read before the row goes: revoking a grant removes the very thing
            // an audience is resolved from, so afterwards the holder would not be
            // among the people told that they no longer hold it.
            var scope = grant.ActivityId;
            var subject = grant.UserId;
            var removed = Wire.Id(grant.Id);

            context.Grants.Remove(grant);
            await context.SaveChangesAsync(ct);
            await AnnounceGrantAsync(scope, subject, new { deletedId = removed }, ct);
        }

        /// <summary>
        /// The role a grant is being pointed at, checked against the scope it is
        /// being written in.
        /// <para>
        /// <b>An activity's role belongs to that activity and nowhere else.</b>
        /// Letting one be linked from elsewhere would make the scope decorative:
        /// a manager could write a role in their own activity, where they may,
        /// and then hand it out in somebody else's.
        /// </para>
        /// </summary>
        private async Task<IReadOnlyList<Role>> RolesForGrantAsync(
            IReadOnlyList<string>? raw, Guid? activityId, CancellationToken ct)
        {
            var ids = (raw ?? [])
                .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
                .Where(id => id is not null)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();
            if (ids.Count == 0) return [];

            var roles = await context.PermissionRoles.AsNoTracking()
                .Where(r => ids.Contains(r.Id))
                .ToListAsync(ct);

            if (roles.Count != ids.Count) throw new NotFoundException("Role");

            foreach (var role in roles)
            {
                if (role.ActivityId is { } owner && owner != activityId)
                {
                    throw new ValidationException(
                        activityId is null
                            ? $"\"{role.Name}\" belongs to an activity and cannot be granted at system scope"
                            : $"\"{role.Name}\" belongs to another activity",
                        "grant.role.scope");
                }
            }

            return roles;
        }

        /// <summary>
        /// Makes a grant's links say exactly this — a person's decision, so a
        /// role left out is taken away rather than ignored.
        /// <para>
        /// <b>Taking one away leaves a tombstone.</b> An LTI launch adds the
        /// roles its platform's rules name and never removes one, so without the
        /// mark a manager's correction would come back at that student's next
        /// launch. Granting the role again clears it: the mark says somebody
        /// decided against this role, and re-adding it is that decision
        /// reversed.
        /// </para>
        /// </summary>
        private void LinkRoles(
            Grant grant, IReadOnlyList<Guid> wanted, Guid? source, DateTime clock)
        {
            foreach (var roleId in wanted)
            {
                var existing = grant.Roles.FirstOrDefault(r => r.RoleId == roleId);
                if (existing is null)
                {
                    // Through the set as well, for the reason `AddRolesAsync`
                    // gives: a link added only to the collection is written as
                    // an update to a row that was never inserted.
                    var link = new GrantRole
                    {
                        GrantId = grant.Id,
                        RoleId = roleId,
                        SourceProviderId = source,
                        AddedAt = clock,
                    };
                    grant.Roles.Add(link);
                    context.GrantRoles.Add(link);
                    continue;
                }

                existing.DismissedAt = null;
                // A person taking a role back is taking it as their own: the
                // next launch must not treat it as something it may revise.
                if (source is null) existing.SourceProviderId = null;
            }

            foreach (var link in grant.Roles.Where(r => r.DismissedAt is null))
            {
                if (!wanted.Contains(link.RoleId)) link.DismissedAt = clock;
            }
        }

        /// <summary>
        /// Whether the caller may write this set into a role, and whether the
        /// set is one a role at this scope may hold.
        /// <para>
        /// <b>The excess rule, applied where it never had to be before.</b> A
        /// template could only be edited by an administrator, who is exempt from
        /// it; a role may be edited by a manager, and editing one changes what
        /// other people hold. Without this, `role:manage` in an activity would be
        /// a way to grant oneself anything by writing it into a role and linking
        /// to it.
        /// </para>
        /// </summary>
        public async Task RequireGrantableRoleAsync(
            Guid? activityId, IReadOnlyList<string> wanted,
            IReadOnlyList<string>? already, CancellationToken ct)
        {
            var unknown = Permissions.Unknown(wanted);
            if (unknown.Count > 0)
            {
                throw new ValidationException(
                    "No such permission: " + string.Join(", ", unknown), "role.permission.unknown");
            }

            // The same reason an activity grant may not carry it: the key is only
            // honored at system scope, so a role scoped to one activity that
            // held it would show a right that every check disagrees with.
            if (activityId is not null && wanted.Contains(Permissions.SystemAdministrator))
            {
                throw new ValidationException(
                    "system:administrator is installation-wide; a role belonging to an activity cannot carry it",
                    "role.permission.scope");
            }

            var mine = await permissions.EffectiveAsync(activityId, ct);
            if (mine.Contains(Permissions.SystemAdministrator)) return;

            // **What is being added, not what is already there.** Applied to the
            // whole set, this refused a manager any edit at all to a role that
            // already carried a key they lack — including removing that key —
            // and it refused an activity's settings to be saved unchanged.
            var held = new HashSet<string>(already ?? [], StringComparer.Ordinal);
            var excess = wanted.Where(p => !mine.Contains(p) && !held.Contains(p)).ToList();
            if (excess.Count > 0)
            {
                throw new ForbiddenActionException(
                    "Cannot put into a role permissions you do not hold: " + string.Join(", ", excess),
                    "role.excess");
            }
        }

        /// <summary>
        /// Brings every grant pointing at this role back into agreement with it.
        /// <para>
        /// <b><see cref="Grant.IsSystem"/> is derived, and a role edit is the one
        /// thing that can change it without touching the grant.</b> Adding a
        /// staff key to the participant role makes every participant staff, which
        /// decides who is counted in an activity and who appears in a ranking. A
        /// board silently emptied by an edit somewhere else is exactly the kind of
        /// failure a live role invites, so the recompute is not optional.
        /// </para>
        /// <para>
        /// <b>It moves in both directions.</b> It used to raise the flag and
        /// never lower it, because the column held two things at once — what the
        /// permissions imply and what a person decided — and a role edit could
        /// see only the first. The decision has its own column now, so undoing
        /// an edit undoes the flag it raised: a course whose learners all
        /// vanished from the ranking because a key was added and removed again
        /// comes back.
        /// </para>
        /// <para>
        /// Only the rows whose answer actually moved are announced: an edit
        /// reaching a hundred grants should not put a hundred events on the wire
        /// to say that nothing about them changed.
        /// </para>
        /// </summary>
        private async Task<IReadOnlyList<Grant>> RestateLinkedGrantsAsync(Role role, CancellationToken ct)
        {
            var linked = await context.Grants
                .Include(g => g.Activity)
                .Include(g => g.Roles).ThenInclude(r => r.Role)
                .Where(g => g.Roles.Any(r => r.RoleId == role.Id && r.DismissedAt == null))
                .ToListAsync(ct);

            var moved = new List<Grant>();
            foreach (var grant in linked)
            {
                var effective = Permissions.Effective(
                    grant.Roles
                        .Where(r => r.DismissedAt is null)
                        .Select(r => r.RoleId == role.Id ? role.Permissions : r.Role?.Permissions),
                    grant.Permissions);

                var isSystem = grant.StaffByHand || Permissions.IsStaff(effective);
                if (isSystem == grant.IsSystem) continue;

                grant.IsSystem = isSystem;
                moved.Add(grant);
            }

            return moved;
        }

        public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid? activityId, CancellationToken ct)
        {
            // **Anywhere, not at the installation scope.** Every path that makes
            // a manager writes an *activity* grant — the seeder,
            // `ActivityService.CreateAsync` for whoever created it, the panel and
            // LTI enrollment alike — so asking for this key with a null activity
            // refused the Grants page and the Participants tab to every manager
            // there is.
            //
            // `grant:update` answers too, because applying a role is the reason
            // to read one, and reading discloses nothing: the excess rule still
            // refuses to hand on a permission the caller does not hold. It is
            // kept beside `role:read` rather than replaced by it, because the
            // manager grants written before roles existed carry the second and
            // not the first.
            var mine = await permissions.AnywhereAsync(ct);
            if (!mine.Contains(Permissions.RoleRead) && !mine.Contains(Permissions.GrantUpdate))
            {
                throw new AccessDeniedException(Permissions.RoleRead);
            }

            // **An activity's roles answer to that activity.** Asking "anywhere"
            // and then reading whatever activity the caller named let a manager
            // of one course read the names, permissions and reach of another
            // course's roles — so a named activity is checked at its own scope.
            if (activityId is { } scope)
            {
                var here = await permissions.EffectiveAsync(scope, ct);
                if (!here.Contains(Permissions.RoleRead) && !here.Contains(Permissions.GrantUpdate))
                {
                    throw new AccessDeniedException(Permissions.RoleRead);
                }
            }

            // The installation's roles, plus the asked-for activity's own. An
            // activity's role is only ever grantable there, so listing every
            // activity's would offer a manager roles they cannot use.
            var roles = await context.PermissionRoles
                .AsNoTracking()
                .Include(r => r.Activity)
                .Where(r => r.ActivityId == null || r.ActivityId == activityId)
                .OrderByDescending(r => r.ActivityId == null)
                .ThenByDescending(r => r.IsBuiltIn)
                .ThenBy(r => r.Name)
                .ToListAsync(ct);

            var ids = roles.Select(r => r.Id).ToList();

            // Through the links, so a contribution a provider wrote counts.
            // Those were copies until 2026-09-19 and counted as nothing, which
            // is how a role eight hundred people held could report that editing
            // it reached nobody.
            var counts = await context.GrantRoles
                .AsNoTracking()
                .Where(r => r.DismissedAt == null && ids.Contains(r.RoleId))
                .GroupBy(r => r.RoleId)
                .Select(g => new { RoleId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.RoleId, g => g.Count, ct);

            var mapped = await context.IdentityProviderMappingRules
                .AsNoTracking()
                .Where(r => r.RoleId != null && ids.Contains(r.RoleId!.Value))
                .Select(r => new { RoleId = r.RoleId!.Value, r.Provider!.Slug })
                .Union(context.IdentityProviderDefaultRoles
                    .AsNoTracking()
                    .Where(d => ids.Contains(d.RoleId))
                    .Select(d => new { d.RoleId, d.Provider!.Slug }))
                .ToListAsync(ct);

            var mappedBy = mapped
                .GroupBy(m => m.RoleId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)[.. g.Select(m => m.Slug).Distinct().Order(StringComparer.Ordinal)]);

            return [.. roles.Select(r => ProjectRole(
                r, counts.GetValueOrDefault(r.Id), mappedBy.GetValueOrDefault(r.Id, [])))];
        }

        private static RoleDto ProjectRole(Role role, int grants, IReadOnlyList<string> mappedBy) => new()
        {
            Id = Wire.Id(role.Id),
            Name = role.Name,
            Description = role.Description,
            Permissions = Parse(role.Permissions),
            IsBuiltIn = role.IsBuiltIn,
            BuiltInKey = role.BuiltInKey,
            ActivityId = role.ActivityId is { } a ? Wire.Id(a) : null,
            ActivityName = role.Activity?.Name,
            Grants = grants,
            MappedBy = mappedBy,
        };

        /// <summary>
        /// Whether a role of this name already exists at this scope. Scoped
        /// rather than global, because two activities naming a role `jury` are
        /// not in conflict — the database says the same, in two filtered indexes.
        /// </summary>
        private async Task RefuseADuplicateNameAsync(
            string name, Guid? activityId, Guid? excluding, CancellationToken ct)
        {
            var taken = await context.PermissionRoles.AnyAsync(
                r => r.Name == name && r.ActivityId == activityId && r.Id != excluding, ct);

            if (taken)
            {
                throw new ConflictException($"A role named \"{name}\" already exists", "role.name.taken");
            }
        }

        public async Task<RoleDto> CreateRoleAsync(RoleInputDto input, CancellationToken ct)
        {
            Guid? activityId = input.ActivityId is { } raw && Guid.TryParse(raw, out var parsed)
                ? parsed
                : null;

            await RequireRoleWriteAsync(activityId, ct);

            if (activityId is { } scoped && !await context.Activities.AnyAsync(a => a.Id == scoped, ct))
            {
                throw new NotFoundException("Activity");
            }

            var name = input.Name?.Trim() ?? "";
            if (name.Length == 0) throw new ValidationException("A name is required", "role.name.required");
            await RefuseADuplicateNameAsync(name, activityId, null, ct);

            var wanted = input.Permissions.Distinct().ToList();
            await RequireGrantableRoleAsync(activityId, wanted, already: null, ct);

            var role = new Role
            {
                Name = name,
                Description = input.Description,
                ActivityId = activityId,
                Permissions = JsonSerializer.Serialize(wanted),
                IsBuiltIn = false,
            };
            context.PermissionRoles.Add(role);
            await context.SaveChangesAsync(ct);

            var created = ProjectRole(role, 0, []);
            await AnnounceRoleAsync(created, null, ct);
            return created;
        }

        /// <summary>
        /// Rewrites a role, and with it what everybody pointing at it may do.
        /// <para>
        /// <b>This is the method the whole change exists for, and the one that
        /// fails open.</b> Three things stand between it and an accident: the
        /// scope it is authorized at, the excess rule, and the recompute of every
        /// linked grant's staff flag. The count the panel shows before saving is
        /// the fourth, and the only one a person sees.
        /// </para>
        /// </summary>
        public async Task<RoleDto> UpdateRoleAsync(Guid id, RoleInputDto input, CancellationToken ct)
        {
            var role = await context.PermissionRoles
                .Include(r => r.Activity)
                .FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new NotFoundException("Role");

            await RequireRoleWriteAsync(role.ActivityId, ct);

            // **The shipped administrator's role is fixed.** Everything else an
            // administrator may edit; this one carries the key the installation
            // is administered with, and an empty set here takes the installation
            // away from everybody linked to it at once — which is a thing that
            // happened, from the panel, in one save.
            if (role.BuiltInKey == DefaultRoles.Admin)
            {
                throw new ConflictException(
                    "The administrator's role is fixed. Grant or revoke it instead",
                    "role.builtIn.fixed");
            }

            // **A role does not move between scopes.** Making a global role an
            // activity's would strip it from every grant elsewhere that points at
            // it; the other way would hand one activity's decisions to the whole
            // installation. Either is a new role and a re-link, deliberately.
            var asked = input.ActivityId is { } raw && Guid.TryParse(raw, out var parsed)
                ? (Guid?)parsed
                : null;
            if (input.ActivityId is not null && asked != role.ActivityId)
            {
                throw new ValidationException(
                    "A role cannot be moved between the installation and an activity",
                    "role.scope.fixed");
            }

            var name = input.Name?.Trim() ?? "";
            if (name.Length == 0) throw new ValidationException("A name is required", "role.name.required");
            await RefuseADuplicateNameAsync(name, role.ActivityId, id, ct);

            var wanted = input.Permissions.Distinct().ToList();
            var before = Parse(role.Permissions);
            await RequireGrantableRoleAsync(role.ActivityId, wanted, before, ct);

            // **The other half of "unreachable through a mapping".** The provider
            // service refuses a rule pointing at a role that carries
            // `system:administrator`; without this, the same end is reached by
            // writing the rule first and adding the permission afterwards.
            if (wanted.Contains(Permissions.SystemAdministrator))
            {
                var mapped = await ReferencingProvidersAsync(role.Id, ct);
                if (mapped.Count > 0)
                {
                    throw new ForbiddenActionException(
                        $"\"{role.Name}\" is mapped by {string.Join(", ", mapped)}, "
                            + $"and no claim may ever grant {Permissions.SystemAdministrator}",
                        "role.mapped.administrator");
                }
            }

            // **Taking the last administrator away is refused here too.** A role
            // is what most administrators hold their key through, so emptying
            // one can end an installation's administration as completely as
            // revoking a grant — and neither `aj-admin` nor the seeder can undo
            // it while another administrator exists.
            if (before.Contains(Permissions.SystemAdministrator)
                && !wanted.Contains(Permissions.SystemAdministrator))
            {
                await RefuseLosingTheLastAdministratorThroughRoleAsync(role, ct);
            }

            // Nothing follows a rename any more: a rule, a default and an
            // activity's enrollment set all name the role by id. Rewriting every
            // rule that shared the old name was how one course's manager could
            // rewrite what a directory group bought installation-wide.
            role.Name = name;
            role.Description = input.Description;
            role.Permissions = JsonSerializer.Serialize(wanted);

            var restated = await RestateLinkedGrantsAsync(role, ct);
            await context.SaveChangesAsync(ct);

            var linked = await context.GrantRoles
                .CountAsync(r => r.RoleId == role.Id && r.DismissedAt == null, ct);
            var updated = ProjectRole(role, linked, await ReferencingProvidersAsync(role.Id, ct));
            await AnnounceRoleAsync(updated, null, ct);

            // The rows whose staff flag moved are announced individually as well:
            // a participant count and a ranking change with them, and a screen
            // reading either would otherwise go on showing yesterday's answer.
            foreach (var grant in restated)
            {
                await AnnounceGrantAsync(
                    grant.ActivityId, grant.UserId, new { grant = Projected(grant) }, ct);
            }

            return updated;
        }

        public async Task DeleteRoleAsync(Guid id, CancellationToken ct)
        {
            var role = await context.PermissionRoles.FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new NotFoundException("Role");

            await permissions.RequireAsync(Permissions.RoleManage, role.ActivityId, ct);

            if (role.IsBuiltIn)
            {
                throw new ConflictException("A built-in role cannot be deleted", "role.builtIn");
            }

            // **A grant links it, so deleting one takes rights away.** The
            // foreign key refuses this too, at the end of the statement; the
            // refusal is written here so it arrives as a sentence rather than as
            // a constraint violation.
            //
            // A dismissed link is a record of a role somebody took away, so it
            // holds nothing and must never stand between an administrator and a
            // deletion. Those rows go with the role.
            var held = await context.GrantRoles
                .CountAsync(r => r.RoleId == role.Id && r.DismissedAt == null, ct);
            if (held > 0)
            {
                throw new ConflictException(
                    $"\"{role.Name}\" is held by {held} grant(s). Move them to another role first",
                    "role.inUse");
            }

            // An identity provider's rule names it, and the contribution is
            // re-derived at every sign-in. Deleting it would leave a rule
            // granting nothing, silently, at the next sign-in.
            var referencing = await ReferencingProvidersAsync(role.Id, ct);
            if (referencing.Count > 0)
            {
                throw new ConflictException(
                    $"\"{role.Name}\" is mapped by: {string.Join(", ", referencing)}",
                    "role.mapped");
            }

            // An activity enrolls into it. Deleting it would take that slot back
            // to the shipped role without anybody choosing that.
            var enrolling = await context.ActivityEnrollmentRoles
                .Where(r => r.RoleId == role.Id)
                .Select(r => r.Activity!.Name)
                .Distinct()
                .ToListAsync(ct);
            if (enrolling.Count > 0)
            {
                throw new ConflictException(
                    $"\"{role.Name}\" is what {string.Join(", ", enrolling)} enrolls into",
                    "role.enrolling");
            }

            var dismissed = await context.GrantRoles
                .Where(r => r.RoleId == role.Id)
                .ToListAsync(ct);
            context.GrantRoles.RemoveRange(dismissed);

            var removedRole = Wire.Id(role.Id);
            context.PermissionRoles.Remove(role);
            await context.SaveChangesAsync(ct);
            await AnnounceRoleAsync(null, removedRole, ct);
        }

        /// <summary>
        /// Who may write a role at this scope.
        /// <para>
        /// <b>Two keys, because the two scopes are two different powers.</b> The
        /// installation's roles are what every manager and every participant in
        /// every activity holds their permissions through, so writing them is an
        /// administrator's — <c>role:manage</c>, global. An activity's roles are
        /// its manager's, through a key of its own.
        /// </para>
        /// <para>
        /// One key with both scopes was a way out of an activity: a directory
        /// group mapped onto the <c>manager</c> role granted it at system scope,
        /// so anybody in that group could rewrite the installation's roles.
        /// </para>
        /// </summary>
        private Task RequireRoleWriteAsync(Guid? activityId, CancellationToken ct) =>
            activityId is { } scope
                ? permissions.RequireAsync(Permissions.RoleManageActivity, scope, ct)
                : permissions.RequireAsync(Permissions.RoleManage, null, ct);

        /// <summary>
        /// Refuses an edit that would leave nobody administering the
        /// installation, when the key is held through this role.
        /// </summary>
        private async Task RefuseLosingTheLastAdministratorThroughRoleAsync(
            Role role, CancellationToken ct)
        {
            var throughThisRole = await context.Grants
                .AsNoTracking()
                .Where(g => g.ActivityId == null && g.State == GrantState.Active
                    && g.Roles.Any(r => r.RoleId == role.Id && r.DismissedAt == null))
                .Select(g => g.Id)
                .ToListAsync(ct);

            if (throughThisRole.Count == 0) return;

            var elsewhere = await context.Grants
                .AsNoTracking()
                .Where(g => g.ActivityId == null && g.State == GrantState.Active
                    && !throughThisRole.Contains(g.Id))
                .Held()
                .ToListAsync(ct);

            if (elsewhere.Any(g => g.Confers().Contains(Permissions.SystemAdministrator))) return;

            throw new ForbiddenActionException(
                $"\"{role.Name}\" is how the installation's last administrator holds "
                    + "system:administrator. Grant it to somebody else first",
                "role.administrator.last");
        }
    }
}

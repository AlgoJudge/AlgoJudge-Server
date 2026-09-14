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
        /// Somebody has just joined an activity, or accepted an invitation to.
        /// <para>
        /// Announced from here because this owns who hears a grant change — the
        /// rule is four lines and a second copy of it in <c>ActivityService</c>
        /// is a second answer to "who may see the roster". Enrolling was silent
        /// until 2026-09-14, so a manager watching people arrive saw nothing
        /// arrive.
        /// </para>
        /// </summary>
        Task AnnounceEnrolmentAsync(Grant grant, CancellationToken ct);

        Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid? activityId, CancellationToken ct);
        Task<RoleDto> CreateRoleAsync(RoleInputDto input, CancellationToken ct);
        Task<RoleDto> UpdateRoleAsync(Guid id, RoleInputDto input, CancellationToken ct);
        Task DeleteRoleAsync(Guid id, CancellationToken ct);
    }

    public class GrantService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IEventHub events,
        IEventAudience audience
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
        public Task AnnounceEnrolmentAsync(Grant grant, CancellationToken ct) =>
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

            var query = context.Grants
                .AsNoTracking()
                .Include(g => g.User)
                .Include(g => g.Activity)
                .Include(g => g.SourceProvider)
                .Include(g => g.Group)
                .Include(g => g.Role)
                .AsQueryable();

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
            RoleId = grant.RoleId is { } role ? Wire.Id(role) : null,
            RoleName = grant.Role?.Name,
            RolePermissions = Parse(grant.Role?.Permissions ?? "[]"),
            IsSystem = grant.IsSystem,
            CopiedFromRoleName = grant.CopiedFromRoleName,
            State = grant.State == GrantState.Invited ? "invited" : "active",
            CreatedAt = Wire.At(grant.CreatedAt),
            Source = grant.SourceProviderId is null ? "manual" : "provider",
            SourceProviderId = grant.SourceProviderId is { } p ? Wire.Id(p) : null,
            SourceProviderName = grant.SourceProvider?.DisplayName,
            Managed = grant.SourceProviderId is not null,
            OverrideSystem = grant.OverrideSystem,
        };

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
                .Select(g => new { g.Permissions, Role = g.Role != null ? g.Role.Permissions : null })
                .ToListAsync(ct);

            return systemGrants.Any(g => Permissions
                .Effective(g.Role, g.Permissions)
                .Contains(Permissions.SystemAdministrator));
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
        /// that somehow did would still be honoured by the resolver, so counting
        /// it errs the safe way.
        /// </para>
        /// </summary>
        private async Task<bool> AnotherAdministratorAsync(Guid excluding, CancellationToken ct)
        {
            var others = await context.Grants
                .AsNoTracking()
                .Where(g => g.Id != excluding && g.ActivityId == null && g.State == GrantState.Active)
                .Select(g => new { g.Permissions, Role = g.Role != null ? g.Role.Permissions : null })
                .ToListAsync(ct);

            return others.Any(g => Permissions
                .Effective(g.Role, g.Permissions)
                .Contains(Permissions.SystemAdministrator));
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
        private async Task<IReadOnlyList<string>> ReferencingProvidersAsync(string name, CancellationToken ct)
        {
            var byRule = await context.IdentityProviderMappingRules
                .Where(r => r.RoleName == name)
                .Select(r => r.Provider!.Slug)
                .ToListAsync(ct);

            var byDefault = await context.IdentityProviders
                .Where(p => p.DefaultRoleName == name)
                .Select(p => p.Slug)
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
        /// <b>It never touches a managed contribution.</b> Those belong to a
        /// provider's mapping and are rewritten at every sign-in, so an edit here
        /// would last exactly until that person next signed in — and a change
        /// that silently reverts is worse than one that is refused. The lookup
        /// below therefore matches on a null source rather than filtering
        /// afterwards: there is no path through this method that could find one.
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

            var role = await RoleForGrantAsync(input.RoleId, activityId, ct);

            // **Every rule below reads the union, not the additions.** A grant
            // carries its role's permissions as surely as its own, so an excess
            // check that looked only at what was typed in would let anybody with
            // `grant:update` hand out an administrator's role by pointing at it.
            var held = Permissions.Effective(role?.Permissions, JsonSerializer.Serialize(wanted));

            // Nobody may grant a permission they do not themselves hold. Without
            // this the model is decorative: anybody who could edit a grant could
            // write `system:administrator` into it.
            var mine = await permissions.EffectiveAsync(activityId, ct);
            if (!mine.Contains(Permissions.SystemAdministrator))
            {
                var excess = held.Where(p => !mine.Contains(p)).ToList();
                if (excess.Count > 0)
                {
                    throw new ForbiddenActionException(
                        "Cannot grant permissions you do not hold: " + string.Join(", ", excess),
                        "grant.excess");
                }
            }

            // **`system:administrator` is a system grant's key, and only ever
            // one.** `PermissionService.IsAdministratorAsync` requires
            // `ActivityId is null` before honouring it, so written into an
            // activity grant it confers nothing at all — and the danger is
            // exactly that it looks as though it does: the panel shows somebody
            // holding it while every check disagrees, silently.
            //
            // **After the rule above, and that is where it belongs.** Anybody
            // else writing this key is already refused by the excess rule, for a
            // better reason — they do not hold it. The one actor that rule
            // exempts is an administrator, and this is the case it leaves.
            //
            // **Only this key; the general rule is not enforced.** The catalogue
            // declares a scope for all 52, but five of the shipped `manager`
            // template's are `Global` — the `problem:*` ones — and the panel
            // applies that template to activity grants. Refusing every misplaced
            // global key would refuse the template this product ships.
            //
            // Those five are no longer inert there: since 2026-09-09 the problem
            // library asks for them **anywhere** rather than at system scope, so
            // an activity grant carries them. What the declaration means is
            // therefore documentation, and only this key's scope is a rule.
            if (activityId is not null && held.Contains(Permissions.SystemAdministrator))
            {
                throw new ValidationException(
                    "system:administrator is installation-wide; it means nothing in an activity grant",
                    "grant.permission.scope");
            }

            if (!await context.Users.AnyAsync(u => u.Id == input.UserId, ct))
            {
                throw new NotFoundException("User");
            }
            if (activityId is { } scoped && !await context.Activities.AnyAsync(a => a.Id == scoped, ct))
            {
                throw new NotFoundException("Activity");
            }

            var grant = await context.Grants
                .Include(g => g.Role)
                .FirstOrDefaultAsync(
                    g => g.UserId == input.UserId
                        && g.ActivityId == activityId
                        && g.SourceProviderId == null, ct);

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
            var before = Permissions.Effective(grant.Role?.Permissions, grant.Permissions);
            await RefuseLosingTheLastAdministratorAsync(grant, before, stillAdministers, ct);

            grant.RoleId = role?.Id;
            grant.Permissions = JsonSerializer.Serialize(wanted);
            // The label describes where a *copied* set started, so a link erases
            // it: two fields both claiming to say which role this is would
            // eventually disagree.
            grant.CopiedFromRoleName = role is null ? input.CopiedFromRoleName : null;
            grant.State = input.State == "invited" ? GrantState.Invited : GrantState.Active;
            // Settled here, never taken from the caller: a grant carrying any
            // permission a participant does not hold is staff, always — and that
            // is what keeps a jury member out of the ranking.
            grant.IsSystem = Permissions.IsStaff(held) || input.IsSystem == true;

            await context.SaveChangesAsync(ct);

            var stored = await context.Grants
                .AsNoTracking()
                .Include(g => g.User)
                .Include(g => g.Activity)
                .Include(g => g.SourceProvider)
                .Include(g => g.Group)
                .Include(g => g.Role)
                .FirstAsync(g => g.Id == grant.Id, ct);
            var projected = Projected(stored);
            await AnnounceGrantAsync(stored.ActivityId, stored.UserId, new { grant = projected }, ct);
            return projected;
        }

        /// <summary>
        /// Revoking removes the row — a grant has no revoked state, only
        /// `invited` and `active` — so it is a delete rather than an action. In
        /// an activity that also removes the membership, because the grant is
        /// the membership.
        /// </summary>
        public async Task RevokeAsync(Guid id, CancellationToken ct)
        {
            var grant = await context.Grants
                .Include(g => g.Role)
                .FirstOrDefaultAsync(g => g.Id == id, ct)
                ?? throw new NotFoundException("Grant");

            await permissions.RequireAsync(Permissions.GrantUpdate, grant.ActivityId, ct);

            // A managed contribution is the provider's, and revoking one here
            // would last until that person next signed in. What actually takes it
            // away is changing the mapping, unlinking the provider, or blocking
            // the account — the coarse instruments a union leaves, and the cost
            // recorded when the union was accepted.
            if (grant.SourceProviderId is not null)
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
                Permissions.Effective(grant.Role?.Permissions, grant.Permissions),
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
        private async Task<Role?> RoleForGrantAsync(string? raw, Guid? activityId, CancellationToken ct)
        {
            if (raw is null || !Guid.TryParse(raw, out var roleId)) return null;

            var role = await context.PermissionRoles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == roleId, ct)
                ?? throw new NotFoundException("Role");

            if (role.ActivityId is { } owner && owner != activityId)
            {
                throw new ValidationException(
                    activityId is null
                        ? $"\"{role.Name}\" belongs to an activity and cannot be granted at system scope"
                        : $"\"{role.Name}\" belongs to another activity",
                    "grant.role.scope");
            }

            return role;
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
        private async Task RefuseARoleTheCallerCouldNotGrantAsync(
            Guid? activityId, IReadOnlyList<string> wanted, CancellationToken ct)
        {
            var unknown = Permissions.Unknown(wanted);
            if (unknown.Count > 0)
            {
                throw new ValidationException(
                    "No such permission: " + string.Join(", ", unknown), "role.permission.unknown");
            }

            // The same reason an activity grant may not carry it: the key is only
            // honoured at system scope, so a role scoped to one activity that
            // held it would show a right that every check disagrees with.
            if (activityId is not null && wanted.Contains(Permissions.SystemAdministrator))
            {
                throw new ValidationException(
                    "system:administrator is installation-wide; a role belonging to an activity cannot carry it",
                    "role.permission.scope");
            }

            var mine = await permissions.EffectiveAsync(activityId, ct);
            if (mine.Contains(Permissions.SystemAdministrator)) return;

            var excess = wanted.Where(p => !mine.Contains(p)).ToList();
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
        /// <b>It raises the flag and never lowers it</b>, because the column
        /// holds two things: what the permissions imply, and a decision somebody
        /// made by hand about this person — a jury member holding nothing but a
        /// participant's keys is marked systemic on purpose. A role edit knows
        /// the first and cannot see the second, so it enforces the direction that
        /// matters and leaves the other where it was made. Clearing the flag
        /// stays a per-grant act.
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
                .Where(g => g.RoleId == role.Id && !g.IsSystem)
                .ToListAsync(ct);

            var moved = new List<Grant>();
            foreach (var grant in linked)
            {
                if (!Permissions.IsStaff(Permissions.Effective(role.Permissions, grant.Permissions)))
                {
                    continue;
                }

                grant.IsSystem = true;
                moved.Add(grant);
            }

            return moved;
        }

        public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(Guid? activityId, CancellationToken ct)
        {
            // **Anywhere, not at the installation scope.** Every path that makes
            // a manager writes an *activity* grant — the seeder,
            // `ActivityService.CreateAsync` for whoever created it, the panel and
            // LTI enrolment alike — so asking for this key with a null activity
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
            var counts = await context.Grants
                .AsNoTracking()
                .Where(g => g.RoleId != null && ids.Contains(g.RoleId!.Value))
                .GroupBy(g => g.RoleId!.Value)
                .Select(g => new { RoleId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.RoleId, g => g.Count, ct);

            return [.. roles.Select(r => ProjectRole(r, counts.GetValueOrDefault(r.Id)))];
        }

        private static RoleDto ProjectRole(Role role, int grants) => new()
        {
            Id = Wire.Id(role.Id),
            Name = role.Name,
            Description = role.Description,
            Permissions = Parse(role.Permissions),
            IsBuiltIn = role.IsBuiltIn,
            ActivityId = role.ActivityId is { } a ? Wire.Id(a) : null,
            ActivityName = role.Activity?.Name,
            Grants = grants,
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

            // At the scope the role will live in: `role:manage` at system scope
            // writes the installation's roles, and held in an activity grant it
            // writes that activity's. One key, and the scope is the whole of the
            // difference between correcting one course and correcting all of them.
            await permissions.RequireAsync(Permissions.RoleManage, activityId, ct);

            if (activityId is { } scoped && !await context.Activities.AnyAsync(a => a.Id == scoped, ct))
            {
                throw new NotFoundException("Activity");
            }

            var name = input.Name?.Trim() ?? "";
            if (name.Length == 0) throw new ValidationException("A name is required", "role.name.required");
            await RefuseADuplicateNameAsync(name, activityId, null, ct);

            var wanted = input.Permissions.Distinct().ToList();
            await RefuseARoleTheCallerCouldNotGrantAsync(activityId, wanted, ct);

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

            var created = ProjectRole(role, 0);
            await AnnounceRoleAsync(created, null, ct);
            return created;
        }

        /// <summary>
        /// Rewrites a role, and with it what everybody pointing at it may do.
        /// <para>
        /// <b>This is the method the whole change exists for, and the one that
        /// fails open.</b> Three things stand between it and an accident: the
        /// scope it is authorised at, the excess rule, and the recompute of every
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

            await permissions.RequireAsync(Permissions.RoleManage, role.ActivityId, ct);

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
            await RefuseARoleTheCallerCouldNotGrantAsync(role.ActivityId, wanted, ct);

            // **The other half of "unreachable through a mapping".** The provider
            // service refuses a rule pointing at a role that carries
            // `system:administrator`; without this, the same end is reached by
            // writing the rule first and adding the permission afterwards.
            if (wanted.Contains(Permissions.SystemAdministrator))
            {
                var mapped = await ReferencingProvidersAsync(role.Name, ct);
                if (mapped.Count > 0)
                {
                    throw new ForbiddenActionException(
                        $"\"{role.Name}\" is mapped by {string.Join(", ", mapped)}, "
                            + $"and no claim may ever grant {Permissions.SystemAdministrator}",
                        "role.mapped.administrator");
                }
            }

            // A rename has to reach the mapping rules that name it, or a provider
            // would go on referring to a role that no longer answers and quietly
            // grant nothing at the next sign-in. Grants need no such care: they
            // hold the id.
            if (role.Name != name)
            {
                foreach (var rule in await context.IdentityProviderMappingRules
                    .Where(r => r.RoleName == role.Name).ToListAsync(ct))
                {
                    rule.RoleName = name;
                }
                foreach (var provider in await context.IdentityProviders
                    .Where(p => p.DefaultRoleName == role.Name).ToListAsync(ct))
                {
                    provider.DefaultRoleName = name;
                }
            }

            role.Name = name;
            role.Description = input.Description;
            role.Permissions = JsonSerializer.Serialize(wanted);

            var restated = await RestateLinkedGrantsAsync(role, ct);
            await context.SaveChangesAsync(ct);

            var linked = await context.Grants.CountAsync(g => g.RoleId == role.Id, ct);
            var updated = ProjectRole(role, linked);
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

            // **A grant points at it, so deleting one takes rights away.** The
            // foreign key refuses this too, at the end of the statement; the
            // refusal is written here so it arrives as a sentence rather than as
            // a constraint violation.
            var held = await context.Grants.CountAsync(g => g.RoleId == role.Id, ct);
            if (held > 0)
            {
                throw new ConflictException(
                    $"\"{role.Name}\" is held by {held} grant(s). Move them to another role first",
                    "role.inUse");
            }

            // An identity provider's mapping rule names it, and the contribution
            // is re-derived from that name at every sign-in. Deleting it would
            // leave a rule granting nothing, silently, at the next sign-in.
            var referencing = await ReferencingProvidersAsync(role.Name, ct);
            if (referencing.Count > 0)
            {
                throw new ConflictException(
                    $"\"{role.Name}\" is mapped by: {string.Join(", ", referencing)}",
                    "role.mapped");
            }

            var removedRole = Wire.Id(role.Id);
            context.PermissionRoles.Remove(role);
            await context.SaveChangesAsync(ct);
            await AnnounceRoleAsync(null, removedRole, ct);
        }
    }
}

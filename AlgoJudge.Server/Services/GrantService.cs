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

        Task<IReadOnlyList<PermissionTemplateDto>> ListTemplatesAsync(CancellationToken ct);
        Task<PermissionTemplateDto> CreateTemplateAsync(PermissionTemplateInputDto input, CancellationToken ct);
        Task<PermissionTemplateDto> UpdateTemplateAsync(Guid id, PermissionTemplateInputDto input, CancellationToken ct);
        Task DeleteTemplateAsync(Guid id, CancellationToken ct);
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
        /// A template is the installation's, so its audience is everybody who may
        /// read one. Templates are **copied** into a grant and never referenced,
        /// so this changes what a future grant starts from and nobody's access.
        /// </summary>
        private async Task AnnounceTemplateAsync(
            PermissionTemplateDto? template, string? deletedId, CancellationToken ct)
        {
            var readers = await audience.AnywhereAsync(Permissions.TemplateRead, ct);
            if (readers.Count == 0) return;

            await events.SendToUsersAsync(readers, EventTypes.PermissionTemplateChanged,
                deletedId is null ? new { template } : new { deletedId }, ct);
        }
        public async Task<PageDto<GrantDto>> ListAsync(
            PageQuery paging, string? userId, Guid? activityId, string? scope, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.GrantReadAll, activityId, ct);

            var query = context.Grants
                .AsNoTracking()
                .Include(g => g.User)
                .Include(g => g.Activity)
                .Include(g => g.SourceProvider)
                .AsQueryable();

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
                Items = page.Select(Project).ToList(),
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        private static GrantDto Project(Grant grant) => new()
        {
            Id = Wire.Id(grant.Id),
            UserId = grant.UserId,
            UserName = grant.User is null ? grant.UserId : Projections.DisplayName(grant.User),
            UserLogin = grant.User?.UserName ?? grant.UserId,
            ActivityId = grant.ActivityId is { } a ? Wire.Id(a) : null,
            ActivityName = grant.Activity?.Name,
            Permissions = Parse(grant.Permissions),
            IsSystem = grant.IsSystem,
            CreatedFromTemplate = grant.CreatedFromTemplate,
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
                .Select(g => g.Permissions)
                .ToListAsync(ct);

            return systemGrants.Any(p => Parse(p).Contains(Permissions.SystemAdministrator));
        }

        /// <summary>
        /// Which providers name this template — through a mapping rule or as
        /// their default. Both count: either way, deleting it leaves a provider
        /// pointing at nothing.
        /// </summary>
        private async Task<IReadOnlyList<string>> ReferencingProvidersAsync(string name, CancellationToken ct)
        {
            var byRule = await context.IdentityProviderMappingRules
                .Where(r => r.TemplateName == name)
                .Select(r => r.Provider!.Slug)
                .ToListAsync(ct);

            var byDefault = await context.IdentityProviders
                .Where(p => p.DefaultTemplateName == name)
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

            // Nobody may grant a permission they do not themselves hold. Without
            // this the model is decorative: anybody who could edit a grant could
            // write `system:administrator` into it.
            var mine = await permissions.EffectiveAsync(activityId, ct);
            if (!mine.Contains(Permissions.SystemAdministrator))
            {
                var excess = wanted.Where(p => !mine.Contains(p)).ToList();
                if (excess.Count > 0)
                {
                    throw new ForbiddenActionException(
                        "Cannot grant permissions you do not hold: " + string.Join(", ", excess),
                        "grant.excess");
                }
            }

            if (!await context.Users.AnyAsync(u => u.Id == input.UserId, ct))
            {
                throw new NotFoundException("User");
            }
            if (activityId is { } scoped && !await context.Activities.AnyAsync(a => a.Id == scoped, ct))
            {
                throw new NotFoundException("Activity");
            }

            var grant = await context.Grants.FirstOrDefaultAsync(
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

            grant.Permissions = JsonSerializer.Serialize(wanted);
            grant.CreatedFromTemplate = input.CreatedFromTemplate;
            grant.State = input.State == "invited" ? GrantState.Invited : GrantState.Active;
            // Settled here, never taken from the caller: a grant carrying any
            // permission a participant does not hold is staff, always — and that
            // is what keeps a jury member out of the ranking.
            grant.IsSystem = Permissions.IsStaff(wanted) || input.IsSystem == true;

            await context.SaveChangesAsync(ct);

            var stored = await context.Grants
                .AsNoTracking()
                .Include(g => g.User)
                .Include(g => g.Activity)
                .Include(g => g.SourceProvider)
                .FirstAsync(g => g.Id == grant.Id, ct);
            var projected = Project(stored);
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
            var grant = await context.Grants.FirstOrDefaultAsync(g => g.Id == id, ct)
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

        public async Task<IReadOnlyList<PermissionTemplateDto>> ListTemplatesAsync(CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.TemplateRead, null, ct);

            var templates = await context.PermissionTemplates
                .AsNoTracking()
                .OrderByDescending(t => t.IsBuiltIn).ThenBy(t => t.Name)
                .ToListAsync(ct);

            return templates.Select(ProjectTemplate).ToList();
        }

        private static PermissionTemplateDto ProjectTemplate(PermissionTemplate template) => new()
        {
            Id = Wire.Id(template.Id),
            Name = template.Name,
            Description = template.Description,
            Permissions = Parse(template.Permissions),
            IsBuiltIn = template.IsBuiltIn,
        };

        public async Task<PermissionTemplateDto> CreateTemplateAsync(
            PermissionTemplateInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.TemplateManage, null, ct);

            var name = input.Name?.Trim() ?? "";
            if (name.Length == 0) throw new ValidationException("A name is required", "template.name.required");
            if (await context.PermissionTemplates.AnyAsync(t => t.Name == name, ct))
            {
                throw new ConflictException($"A template named \"{name}\" already exists", "template.name.taken");
            }

            var unknown = Permissions.Unknown(input.Permissions);
            if (unknown.Count > 0)
            {
                throw new ValidationException(
                    "No such permission: " + string.Join(", ", unknown), "template.permission.unknown");
            }

            var template = new PermissionTemplate
            {
                Name = name,
                Description = input.Description,
                Permissions = JsonSerializer.Serialize(input.Permissions.Distinct()),
                IsBuiltIn = false,
            };
            context.PermissionTemplates.Add(template);
            await context.SaveChangesAsync(ct);
            var created = ProjectTemplate(template);
            await AnnounceTemplateAsync(created, null, ct);
            return created;
        }

        public async Task<PermissionTemplateDto> UpdateTemplateAsync(
            Guid id, PermissionTemplateInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.TemplateManage, null, ct);

            var template = await context.PermissionTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new NotFoundException("Permission template");

            var name = input.Name?.Trim() ?? "";
            if (name.Length == 0) throw new ValidationException("A name is required", "template.name.required");
            if (await context.PermissionTemplates.AnyAsync(t => t.Name == name && t.Id != id, ct))
            {
                throw new ConflictException($"A template named \"{name}\" already exists", "template.name.taken");
            }

            var unknown = Permissions.Unknown(input.Permissions);
            if (unknown.Count > 0)
            {
                throw new ValidationException(
                    "No such permission: " + string.Join(", ", unknown), "template.permission.unknown");
            }

            // A rename has to reach the mapping rules that name it, or a provider
            // would go on referring to a template that no longer answers and
            // quietly grant nothing at the next sign-in. This is not the same as
            // a grant following its template — a grant holds a copy and keeps it.
            if (template.Name != name)
            {
                foreach (var rule in await context.IdentityProviderMappingRules
                    .Where(r => r.TemplateName == template.Name).ToListAsync(ct))
                {
                    rule.TemplateName = name;
                }
                foreach (var provider in await context.IdentityProviders
                    .Where(p => p.DefaultTemplateName == template.Name).ToListAsync(ct))
                {
                    provider.DefaultTemplateName = name;
                }
            }

            template.Name = name;
            template.Description = input.Description;
            template.Permissions = JsonSerializer.Serialize(input.Permissions.Distinct());

            // Editing a template touches nobody who already used it: choosing one
            // copies its permissions into the grant, and nothing points back
            // here afterwards. That is the whole reason it is a template rather
            // than a role.
            await context.SaveChangesAsync(ct);
            var updated = ProjectTemplate(template);
            await AnnounceTemplateAsync(updated, null, ct);
            return updated;
        }

        public async Task DeleteTemplateAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.TemplateManage, null, ct);

            var template = await context.PermissionTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new NotFoundException("Permission template");

            if (template.IsBuiltIn)
            {
                throw new ConflictException("A built-in template cannot be deleted", "template.builtIn");
            }

            // **The one place something points at a template.** A grant does not
            // — choosing one copies its permissions and nothing points back — but
            // an identity provider's mapping rule names it, and the contribution
            // is re-derived from that name at every sign-in. Deleting it would
            // leave a rule granting nothing, silently, at the next sign-in.
            var referencing = await ReferencingProvidersAsync(template.Name, ct);
            if (referencing.Count > 0)
            {
                throw new ConflictException(
                    $"\"{template.Name}\" is mapped by: {string.Join(", ", referencing)}",
                    "template.mapped");
            }

            var removedTemplate = Wire.Id(template.Id);
            context.PermissionTemplates.Remove(template);
            await context.SaveChangesAsync(ct);
            await AnnounceTemplateAsync(null, removedTemplate, ct);
        }
    }
}

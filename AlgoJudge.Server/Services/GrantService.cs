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
        IPermissionService permissions
    ) : IGrantService
    {
        public async Task<PageDto<GrantDto>> ListAsync(
            PageQuery paging, string? userId, Guid? activityId, string? scope, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.GrantReadAll, activityId, ct);

            var query = context.Grants
                .AsNoTracking()
                .Include(g => g.User)
                .Include(g => g.Activity)
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
        /// Creates or replaces the grant for one user in one scope.
        /// <para>
        /// One grant per user per scope, which the database enforces. "A manager
        /// with the right to update something taken away" is this set with that
        /// entry removed, not a second grant layered over a first.
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

            var grant = await context.Grants
                .FirstOrDefaultAsync(g => g.UserId == input.UserId && g.ActivityId == activityId, ct);

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
                .FirstAsync(g => g.Id == grant.Id, ct);
            return Project(stored);
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

            context.Grants.Remove(grant);
            await context.SaveChangesAsync(ct);
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
            return ProjectTemplate(template);
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

            template.Name = name;
            template.Description = input.Description;
            template.Permissions = JsonSerializer.Serialize(input.Permissions.Distinct());

            // Editing a template touches nobody who already used it: choosing one
            // copies its permissions into the grant, and nothing points back
            // here afterwards. That is the whole reason it is a template rather
            // than a role.
            await context.SaveChangesAsync(ct);
            return ProjectTemplate(template);
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

            context.PermissionTemplates.Remove(template);
            await context.SaveChangesAsync(ct);
        }
    }
}

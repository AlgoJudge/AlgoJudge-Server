using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Resolves permissions from grants.
    /// <para>
    /// Three rules, in this order, from <c>docs/specs/PERMISSIONS.md</c>:
    /// <list type="number">
    /// <item><c>system:administrator</c> bypasses every check at every scope, and
    /// is checked <b>first</b> — it is not one entry among many.</item>
    /// <item>Otherwise the effective set is the <b>union</b> of the system grant
    /// and the activity grant.</item>
    /// <item>Nobody may grant a permission they do not themselves hold. That one
    /// is enforced where a grant is written, not here.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Grants are read once per request and cached for its lifetime. This is a
    /// scoped service, so the cache dies with the request; a longer-lived one
    /// would keep answering with rights that had been revoked.
    /// </para>
    /// </summary>
    public class PermissionService(
        ApplicationDbContext context,
        ICurrentUserService currentUser
    ) : IPermissionService
    {
        private List<Grant>? grants;

        /// <summary>
        /// Every grant this user holds, loaded once.
        /// <para>
        /// An <c>invited</c> grant confers nothing: it is an offer, and the user
        /// is not in the activity until they accept. Filtering it out here rather
        /// than at each call site is what stops one endpoint forgetting.
        /// </para>
        /// </summary>
        private async Task<List<Grant>> GrantsAsync(CancellationToken ct)
        {
            if (grants is not null) return grants;

            var user = await currentUser.GetAsync(ct);
            if (user is null) return grants = [];

            grants = await context.Grants
                .AsNoTracking()
                .Where(g => g.UserId == user.Id && g.State == GrantState.Active)
                .ToListAsync(ct);
            return grants;
        }

        private static IReadOnlyList<string> Parse(Grant grant)
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(grant.Permissions) ?? [];
            }
            catch (JsonException)
            {
                // A grant whose permissions do not parse grants nothing. The
                // alternative — throwing — would make one corrupt row lock every
                // user out of every screen, and the alternative to that would be
                // to ignore the error and treat it as an administrator.
                return [];
            }
        }

        private async Task<bool> IsAdministratorAsync(CancellationToken ct)
        {
            foreach (var grant in await GrantsAsync(ct))
            {
                // The bypass is only meaningful at the system scope. An activity
                // grant carrying the string would otherwise make a manager of one
                // course an administrator of the installation.
                if (grant.ActivityId is null && Parse(grant).Contains(Permissions.SystemAdministrator))
                {
                    return true;
                }
            }
            return false;
        }

        public async Task<IReadOnlySet<string>> EffectiveAsync(Guid? activityId = null, CancellationToken ct = default)
        {
            if (await IsAdministratorAsync(ct))
            {
                return Permissions.Catalogue.Select(d => d.Key).ToHashSet();
            }

            var effective = new HashSet<string>();
            foreach (var grant in await GrantsAsync(ct))
            {
                // The system grant carries into every activity; an activity grant
                // applies to its own activity only.
                if (grant.ActivityId is null || (activityId is not null && grant.ActivityId == activityId))
                {
                    effective.UnionWith(Parse(grant));
                }
            }
            return effective;
        }

        public async Task<IReadOnlySet<string>> AnywhereAsync(CancellationToken ct = default)
        {
            if (await IsAdministratorAsync(ct))
            {
                return Permissions.Catalogue.Select(d => d.Key).ToHashSet();
            }

            var everywhere = new HashSet<string>();
            foreach (var grant in await GrantsAsync(ct))
            {
                everywhere.UnionWith(Parse(grant));
            }
            return everywhere;
        }

        public async Task<bool> HasAsync(string permission, Guid? activityId = null, CancellationToken ct = default)
        {
            if (await IsAdministratorAsync(ct)) return true;
            return (await EffectiveAsync(activityId, ct)).Contains(permission);
        }

        public async Task<bool> HasAnyAsync(IEnumerable<string> permissions, Guid? activityId = null, CancellationToken ct = default)
        {
            if (await IsAdministratorAsync(ct)) return true;
            var effective = await EffectiveAsync(activityId, ct);
            return permissions.Any(effective.Contains);
        }

        public async Task RequireAsync(string permission, Guid? activityId = null, CancellationToken ct = default)
        {
            if (!await HasAsync(permission, activityId, ct))
            {
                throw new AccessDeniedException(permission);
            }
        }

        public async Task<IReadOnlyCollection<Guid>?> ActivitiesWithAsync(string permission, CancellationToken ct = default)
        {
            if (await IsAdministratorAsync(ct)) return null;

            var all = await GrantsAsync(ct);

            // A system grant holding it holds it everywhere, so there is no list
            // to return — null means "not restricted", which is a different
            // answer from an empty list and callers must not conflate them.
            if (all.Any(g => g.ActivityId is null && Parse(g).Contains(permission))) return null;

            return all
                .Where(g => g.ActivityId is not null && Parse(g).Contains(permission))
                .Select(g => g.ActivityId!.Value)
                .Distinct()
                .ToList();
        }
    }
}

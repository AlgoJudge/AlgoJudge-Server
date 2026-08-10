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
    /// The rule, in this order, from <c>docs/specs/PERMISSIONS.md</c> as restated
    /// on 2026-08-09:
    /// </para>
    /// <list type="number">
    /// <item><b>An activity grant carrying the override flag is authoritative
    /// inside its activity</b>, and nothing else applies there — not even
    /// <c>system:administrator</c>. Checked <b>first</b>, because checking the
    /// administrator bypass before it is exactly how the override would be
    /// lost.</item>
    /// <item><c>system:administrator</c>, held in any system contribution,
    /// bypasses every other check at every other scope.</item>
    /// <item>Otherwise the effective set is the <b>union</b> — of every system
    /// contribution, and of the activity grant when one scope is asked about.</item>
    /// <item><b>Nothing subtracts, anywhere.</b> The earlier rule said "minus the
    /// union of their denies"; the denies were removed and the subtraction
    /// outlived them until the override replaced it.</item>
    /// <item>Nobody may grant, or map onto, a permission they do not themselves
    /// hold. Enforced where a grant is written, not here.</item>
    /// </list>
    /// <para>
    /// <b>System scope is several rows now</b>, one per source: the manual
    /// contribution plus one per linked identity provider. The union below did
    /// not have to change for that — it already unioned every system grant, and
    /// there simply used to be at most one.
    /// </para>
    /// <para>
    /// Grants are read once per request and cached for its lifetime. This is a
    /// scoped service, so the cache dies with the request; a longer-lived one
    /// would keep answering with rights that had been revoked. Whether it should
    /// live longer — a TTL, a stored sum, or this — is an open question awaiting
    /// a spike, and deliberately not settled by whoever edits this next.
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

        /// <summary>
        /// The activity grant carrying the override flag for this activity, if
        /// there is one. Its presence is what makes the rest of the model stop
        /// applying inside that activity.
        /// </summary>
        private async Task<Grant?> OverrideAsync(Guid activityId, CancellationToken ct) =>
            (await GrantsAsync(ct))
                .FirstOrDefault(g => g.ActivityId == activityId && g.OverrideSystem);

        public async Task<IReadOnlySet<string>> EffectiveAsync(Guid? activityId = null, CancellationToken ct = default)
        {
            // **First, and that is the whole point of it.** An override means the
            // activity grant is the answer inside that activity: a system manager
            // who stepped down to compete is a competitor here, and an
            // administrator who did the same is one too. Asking about the
            // administrator bypass before this would quietly restore what the
            // flag was set to give up.
            if (activityId is { } scope && await OverrideAsync(scope, ct) is { } overridden)
            {
                return Parse(overridden).ToHashSet();
            }

            if (await IsAdministratorAsync(ct))
            {
                return Permissions.Catalogue.Select(d => d.Key).ToHashSet();
            }

            var effective = new HashSet<string>();
            foreach (var grant in await GrantsAsync(ct))
            {
                // Every system contribution carries into every activity — there
                // may be several, one per source, and they union. An activity
                // grant applies to its own activity only.
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

        // Both of these used to short-circuit on the administrator bypass before
        // asking `EffectiveAsync`. They no longer may: the bypass is not the
        // first rule any more, and a shortcut past the override would let an
        // administrator who stepped down inside one activity keep every right
        // there — through whichever of the two call sites forgot.
        public async Task<bool> HasAsync(string permission, Guid? activityId = null, CancellationToken ct = default) =>
            (await EffectiveAsync(activityId, ct)).Contains(permission);

        public async Task<bool> HasAnyAsync(IEnumerable<string> permissions, Guid? activityId = null, CancellationToken ct = default)
        {
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
            var all = await GrantsAsync(ct);

            // The activities that took it away from themselves. An override is
            // the whole answer inside its activity, so one that does not carry
            // this permission withholds it however widely it is held elsewhere.
            var withheld = all
                .Where(g => g.ActivityId is not null && g.OverrideSystem && !Parse(g).Contains(permission))
                .Select(g => g.ActivityId!.Value)
                .ToHashSet();

            var everywhere = await IsAdministratorAsync(ct)
                || all.Any(g => g.ActivityId is null && Parse(g).Contains(permission));

            if (everywhere)
            {
                // Null means "not restricted", which is a different answer from
                // an empty list and callers must not conflate them.
                if (withheld.Count == 0) return null;

                // "Everywhere except these" is a thing null cannot say, so the
                // list is materialised. Only reachable when this person actually
                // holds an override that withholds this permission — rare, and
                // bounded by the number of activities — which is why the common
                // path above still answers without touching the database.
                return await context.Activities
                    .Where(a => !withheld.Contains(a.Id))
                    .Select(a => a.Id)
                    .ToListAsync(ct);
            }

            return all
                .Where(g => g.ActivityId is not null && Parse(g).Contains(permission))
                .Select(g => g.ActivityId!.Value)
                .Distinct()
                .ToList();
        }
    }
}

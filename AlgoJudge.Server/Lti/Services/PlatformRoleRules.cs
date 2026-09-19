using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>What a launch's roles resolve to inside one activity.</summary>
    /// <param name="RoleIds">The roles to add, already resolved through the slots.</param>
    /// <param name="Permissions">
    /// What those roles carry together, so the caller can decide what the launch
    /// may do before anybody is signed in.
    /// </param>
    public record ResolvedRoles(IReadOnlyList<Guid> RoleIds, IReadOnlyList<string> Permissions);

    public interface IPlatformRoleRules
    {
        /// <summary>
        /// The roles this platform's rules give somebody arriving with these
        /// roles, in this activity.
        /// </summary>
        Task<ResolvedRoles> ResolveAsync(
            Guid providerId, Guid activityId, IReadOnlyList<string> roles, CancellationToken ct);

        /// <summary>
        /// Writes the rules a new platform starts with, if it has none. They
        /// reproduce what the Server did before the mapping was configuration:
        /// a learner is enrolled as the activity enrolls participants, and the
        /// three roles that run a course as it enrolls managers.
        /// </summary>
        Task EnsureDefaultsAsync(Guid providerId, CancellationToken ct);
    }

    /// <summary>
    /// The platform's half of the allowlist.
    /// <para>
    /// A platform carries a provider row, so its rules live in the same table a
    /// sign-in provider's do and are written through the same guards. What
    /// differs is the target: a platform's rule may aim at the activity's own
    /// enrollment sets, because it is applied inside an activity and a sign-in
    /// is not.
    /// </para>
    /// <para>
    /// <b>Configuration, not a constant.</b> Which LTI role means which role
    /// here was compiled in until 2026-09-19 — an installation whose
    /// non-editing teachers should not run a course had nowhere to say so.
    /// </para>
    /// </summary>
    public class PlatformRoleRules(
        ApplicationDbContext core,
        IProviderMappingService mapping
    ) : IPlatformRoleRules
    {
        /// <summary>
        /// What a platform starts with. `Learner` is left to the participants
        /// slot rather than named, so an activity that chose its own roles is
        /// obeyed without anybody rewriting the platform's rules.
        /// </summary>
        private static readonly (string Value, MappingTarget Target)[] Defaults =
        [
            ("Learner", MappingTarget.ActivityParticipants),
            ("Instructor", MappingTarget.ActivityManagers),
            ("ContentDeveloper", MappingTarget.ActivityManagers),
            ("Mentor", MappingTarget.ActivityManagers),
        ];

        public async Task EnsureDefaultsAsync(Guid providerId, CancellationToken ct)
        {
            var provider = await core.IdentityProviders
                .Include(p => p.MappingRules)
                .Include(p => p.DefaultRoles)
                .FirstOrDefaultAsync(p => p.Id == providerId, ct);

            if (provider is null || provider.MappingRules.Count > 0) return;

            await mapping.ReplaceRulesAsync(
                provider,
                [.. Defaults
                    .GroupBy(d => d.Value, StringComparer.Ordinal)
                    .Select(g => new MappingRuleDto
                    {
                        ClaimValue = g.Key,
                        Targets = [.. g.Select(d => new MappingTargetDto
                        {
                            Kind = d.Target == MappingTarget.ActivityManagers
                                ? "activityManagers"
                                : "activityParticipants",
                        })],
                    })],
                allowSlots: true,
                ct);

            await core.SaveChangesAsync(ct);
        }

        public async Task<ResolvedRoles> ResolveAsync(
            Guid providerId, Guid activityId, IReadOnlyList<string> roles, CancellationToken ct)
        {
            var values = LtiRoles.Values(roles);
            if (values.Count == 0) return new ResolvedRoles([], []);

            var rules = await core.IdentityProviderMappingRules
                .AsNoTracking()
                .Where(r => r.ProviderId == providerId && values.Contains(r.ClaimValue))
                .ToListAsync(ct);

            var wanted = new List<Guid>();

            foreach (var rule in rules)
            {
                switch (rule.Target)
                {
                    case MappingTarget.Role when rule.RoleId is { } roleId:
                        Add(wanted, roleId);
                        break;

                    case MappingTarget.ActivityParticipants:
                    case MappingTarget.ActivityManagers:
                        var slot = rule.Target == MappingTarget.ActivityManagers
                            ? EnrollmentSlot.Managers
                            : EnrollmentSlot.Participants;
                        foreach (var role in await DefaultRoles.InSlotAsync(core, activityId, slot, ct))
                        {
                            Add(wanted, role.Id);
                        }
                        break;
                }
            }

            if (wanted.Count == 0) return new ResolvedRoles([], []);

            // **A role carrying `system:administrator` is skipped**, for the
            // reason a mapping rule may not name one: the key is refused where a
            // rule is written, and a role may be edited afterwards. An activity
            // role cannot carry it at all, so this reaches an installation role
            // named by a rule.
            var named = await core.PermissionRoles
                .AsNoTracking()
                .Where(r => wanted.Contains(r.Id))
                .ToListAsync(ct);

            var granted = named
                .Where(r => !Authorization.Permissions.Parse(r.Permissions)
                    .Contains(Authorization.Permissions.SystemAdministrator))
                .ToList();

            return new ResolvedRoles(
                [.. granted.Select(r => r.Id)],
                Authorization.Permissions.Effective(granted.Select(r => (string?)r.Permissions), null));

            static void Add(List<Guid> into, Guid id)
            {
                if (!into.Contains(id)) into.Add(id);
            }
        }
    }
}

using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IProviderMappingService
    {
        /// <summary>
        /// Replaces a provider's allowlist wholesale, with the two guards that
        /// keep a claim from minting privilege.
        /// </summary>
        /// <param name="allowSlots">
        /// Whether a rule may aim at the activity's enrollment sets rather than
        /// at a named role. True for a platform, whose rules are applied inside
        /// an activity; false for a provider, whose contribution is system scope
        /// and has no activity to resolve one against.
        /// </param>
        Task ReplaceRulesAsync(
            IdentityProvider provider, IReadOnlyList<MappingRuleDto> wanted,
            bool allowSlots, CancellationToken ct);

        /// <summary>Replaces the roles a provider grants when nothing matched.</summary>
        Task ReplaceDefaultRolesAsync(
            IdentityProvider provider, IReadOnlyList<string> roleIds, CancellationToken ct);

        /// <summary>The wire shape of one provider's rules, grouped by claim value.</summary>
        IReadOnlyList<MappingRuleDto> Projected(IdentityProvider provider);
    }

    /// <summary>
    /// The allowlist an operator writes, and what it is allowed to say.
    /// <para>
    /// Shared by the providers screen and the LTI platforms screen, because both
    /// write the same table through the same rules. A second copy of the guards
    /// would be a second answer to "may this mapping hand this out", and the
    /// copies drift.
    /// </para>
    /// </summary>
    public class ProviderMappingService(
        ApplicationDbContext context,
        IPermissionService permissions
    ) : IProviderMappingService
    {
        public IReadOnlyList<MappingRuleDto> Projected(IdentityProvider provider) =>
            [.. provider.MappingRules
                .GroupBy(r => r.ClaimValue, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new MappingRuleDto
                {
                    ClaimValue = g.Key,
                    Targets = [.. g
                        .OrderBy(r => r.Target)
                        .ThenBy(r => r.Role?.Name, StringComparer.Ordinal)
                        .Select(r => new MappingTargetDto
                        {
                            Kind = KindOf(r.Target),
                            RoleId = r.RoleId is { } id ? Wire.Id(id) : null,
                            RoleName = r.Role?.Name,
                        })],
                })];

        public async Task ReplaceRulesAsync(
            IdentityProvider provider, IReadOnlyList<MappingRuleDto> wanted,
            bool allowSlots, CancellationToken ct)
        {
            // Roles this provider already maps are exempt from the excess rule:
            // the rule is about what this write adds. Without it an operator who
            // does not hold every key of a role somebody else mapped could not
            // save any change at all — not even disabling the provider.
            var already = provider.MappingRules
                .Where(r => r.RoleId is not null)
                .Select(r => r.RoleId!.Value)
                .Concat(provider.DefaultRoles.Select(d => d.RoleId))
                .ToHashSet();

            var rules = new List<(string Value, MappingTarget Target, Guid? RoleId)>();
            var seen = new HashSet<(string, MappingTarget, Guid?)>();

            foreach (var rule in wanted)
            {
                var value = (rule.ClaimValue ?? "").Trim();
                if (value.Length == 0)
                {
                    throw new ValidationException(
                        "A rule needs a claim value", "provider.rule.claimValue.required");
                }
                if (rule.Targets is null || rule.Targets.Count == 0)
                {
                    throw new ValidationException(
                        $"The rule for \"{value}\" grants nothing", "provider.rule.target.required");
                }

                foreach (var target in rule.Targets)
                {
                    var kind = TargetOf(target.Kind);
                    if (kind != MappingTarget.Role && !allowSlots)
                    {
                        throw new ValidationException(
                            "A sign-in provider's rule names a role: its contribution is "
                                + "installation-wide, and there is no activity to resolve a slot against",
                            "provider.rule.target.invalid");
                    }

                    Guid? roleId = null;
                    if (kind == MappingTarget.Role)
                    {
                        if (target.RoleId is null || !Guid.TryParse(target.RoleId, out var parsed))
                        {
                            throw new ValidationException(
                                "A rule needs a role", "provider.rule.role.required");
                        }
                        roleId = parsed;
                        await RequireMappableAsync(parsed, already, ct);
                    }

                    if (!seen.Add((value, kind, roleId)))
                    {
                        // Two identical lines are not a merge; they are a
                        // question about ordering this model deliberately has no
                        // answer to, asked twice.
                        throw new ValidationException(
                            $"The claim value \"{value}\" grants the same thing twice",
                            "provider.rule.duplicate");
                    }

                    rules.Add((value, kind, roleId));
                }
            }

            // **Matched up rather than emptied and refilled.** Clearing the
            // collection and adding fresh objects made every update of a
            // provider that had rules answer 500: every entity here assigns its
            // own key, so a rule reached through a tracked parent already
            // carries a non-default id and EF writes `UPDATE` for a row that
            // does not exist. Reusing the row for a rule that is staying also
            // keeps its `CreatedAt` and avoids a delete and an insert of the
            // same unique triple inside one `SaveChanges`.
            var existing = provider.MappingRules
                .ToDictionary(r => (r.ClaimValue, r.Target, r.RoleId));

            foreach (var (value, target, roleId) in rules)
            {
                if (existing.Remove((value, target, roleId))) continue;

                var rule = new IdentityProviderMappingRule
                {
                    ProviderId = provider.Id,
                    ClaimValue = value,
                    Target = target,
                    RoleId = roleId,
                };
                // Stated to the context, not only to the collection: that is
                // what marks it `Added` in spite of the key it arrived with.
                provider.MappingRules.Add(rule);
                context.IdentityProviderMappingRules.Add(rule);
            }

            foreach (var gone in existing.Values)
            {
                provider.MappingRules.Remove(gone);
                context.IdentityProviderMappingRules.Remove(gone);
            }
        }

        public async Task ReplaceDefaultRolesAsync(
            IdentityProvider provider, IReadOnlyList<string> roleIds, CancellationToken ct)
        {
            var already = provider.DefaultRoles.Select(d => d.RoleId)
                .Concat(provider.MappingRules.Where(r => r.RoleId is not null).Select(r => r.RoleId!.Value))
                .ToHashSet();

            var wanted = new List<Guid>();
            foreach (var raw in roleIds)
            {
                if (!Guid.TryParse(raw, out var id))
                {
                    throw new ValidationException(
                        "That is not a role id", "provider.defaultRole.unknown");
                }
                if (wanted.Contains(id)) continue;
                await RequireMappableAsync(id, already, ct);
                wanted.Add(id);
            }

            foreach (var gone in provider.DefaultRoles.Where(d => !wanted.Contains(d.RoleId)).ToList())
            {
                provider.DefaultRoles.Remove(gone);
                context.IdentityProviderDefaultRoles.Remove(gone);
            }

            foreach (var id in wanted)
            {
                if (provider.DefaultRoles.Any(d => d.RoleId == id)) continue;
                var row = new IdentityProviderDefaultRole { ProviderId = provider.Id, RoleId = id };
                provider.DefaultRoles.Add(row);
                context.IdentityProviderDefaultRoles.Add(row);
            }
        }

        /// <summary>
        /// The two guards, in one place so neither can be applied without the
        /// other.
        /// <para>
        /// Both refusals name the permission at fault. A validation message that
        /// says only "not allowed" turns a five-second correction into an
        /// afternoon of guessing which entry in a role of thirty is the problem.
        /// </para>
        /// </summary>
        private async Task RequireMappableAsync(
            Guid roleId, HashSet<Guid> already, CancellationToken ct)
        {
            var role = await context.PermissionRoles
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == roleId, ct)
                ?? throw new ValidationException("No such role", "provider.rule.role.unknown");

            // Installation roles only. A mapping is the installation's, and an
            // activity's role is not the installation's to hand out — an
            // activity's manager writes those.
            if (role.ActivityId is not null)
            {
                throw new ValidationException(
                    $"\"{role.Name}\" belongs to an activity, and a mapping is the installation's",
                    "provider.rule.role.scope");
            }

            var granted = Permissions.Parse(role.Permissions);

            // Unreachable in every configuration, and not merely absent from the
            // roles that ship. An installation may invent a role, and one
            // carrying this key would otherwise turn a directory group into a
            // way of becoming an administrator here.
            if (granted.Contains(Permissions.SystemAdministrator))
            {
                throw new ForbiddenActionException(
                    $"\"{role.Name}\" grants {Permissions.SystemAdministrator}, which no claim may ever grant",
                    "provider.rule.administrator");
            }

            if (already.Contains(roleId)) return;

            // The same rule that governs writing a grant. Without it, holding
            // `provider:manage` would be a way of granting yourself anything: map
            // a group you are in onto a role you could not otherwise assign,
            // then sign in through the provider.
            var mine = await permissions.EffectiveAsync(null, ct);
            if (mine.Contains(Permissions.SystemAdministrator)) return;

            var excess = granted.Where(p => !mine.Contains(p)).ToList();
            if (excess.Count > 0)
            {
                throw new ForbiddenActionException(
                    "Cannot map onto permissions you do not hold: " + string.Join(", ", excess),
                    "provider.rule.excess");
            }
        }

        private static string KindOf(MappingTarget target) => target switch
        {
            MappingTarget.ActivityParticipants => "activityParticipants",
            MappingTarget.ActivityManagers => "activityManagers",
            _ => "role",
        };

        private static MappingTarget TargetOf(string? kind) => kind switch
        {
            null or "" or "role" => MappingTarget.Role,
            "activityParticipants" => MappingTarget.ActivityParticipants,
            "activityManagers" => MappingTarget.ActivityManagers,
            _ => throw new ValidationException(
                "A target is role, activityParticipants or activityManagers",
                "provider.rule.target.invalid"),
        };
    }
}

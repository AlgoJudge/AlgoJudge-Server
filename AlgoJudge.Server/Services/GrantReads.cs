using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database.Models;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// One grant as every reader needs it: its scope, its flags, the permissions
    /// of every role it links, and its own entries.
    /// </summary>
    public record HeldGrant
    {
        public required string UserId { get; init; }
        public Guid? ActivityId { get; init; }

        /// <summary>Null for a person's own decision; a provider at system scope.</summary>
        public Guid? SourceProviderId { get; init; }

        public bool OverrideSystem { get; init; }
        public bool IsSystem { get; init; }
        public GrantState State { get; init; }

        /// <summary>The stored set of every role this grant links and holds.</summary>
        public required IReadOnlyList<string?> RoleJsons { get; init; }

        /// <summary>The grant's own entries, beside its roles.</summary>
        public string? Own { get; init; }
    }

    /// <summary>
    /// What a grant confers, in one place.
    /// <para>
    /// <b>This exists because five readers answered without it.</b> The live
    /// ranking push, the hold on an automatic account deletion, the blocker on an
    /// account merge, the seeder's "does anybody administer this installation"
    /// and the group-move projection each read a grant's own entries and never
    /// its roles. Every enrollment written since roles arrived carries its
    /// permissions in the roles and nothing in the entries, so all five answered
    /// "this grant confers nothing" about grants that conferred everything — and
    /// the deletion hold is the one that mattered most, because a provider's
    /// webhook would have anonymized an administrator on it.
    /// </para>
    /// <para>
    /// A projection rather than a method on the entity, so the shape a reader
    /// needs is the shape the query returns — and so the <c>Include</c> that was
    /// forgotten five times cannot be forgotten again.
    /// </para>
    /// </summary>
    public static class GrantReads
    {
        /// <summary>
        /// The projection. Dismissed links are left out: a dismissed role is a
        /// record of something taken away, not something held.
        /// </summary>
        public static IQueryable<HeldGrant> Held(this IQueryable<Grant> grants) =>
            grants.Select(g => new HeldGrant
            {
                UserId = g.UserId,
                ActivityId = g.ActivityId,
                SourceProviderId = g.SourceProviderId,
                OverrideSystem = g.OverrideSystem,
                IsSystem = g.IsSystem,
                State = g.State,
                RoleJsons = g.Roles
                    .Where(r => r.DismissedAt == null)
                    .Select(r => r.Role!.Permissions)
                    .ToList(),
                Own = g.Permissions,
            });

        /// <summary>
        /// What this grant confers: every linked role unioned with its own
        /// entries.
        /// <para>
        /// <b><c>system:administrator</c> is never honored from a provider's
        /// contribution.</b> The key is refused where a mapping is written and
        /// skipped where one is applied, and this is the third answer to the same
        /// question: a role may be edited after a rule names it, and a copy used
        /// to be able to strip the key while a link cannot. So the resolver
        /// strips it, and "unreachable through a mapping, in every configuration"
        /// stays true whatever the other two missed.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> Confers(this HeldGrant grant)
        {
            var effective = Permissions.Effective(grant.RoleJsons, grant.Own);
            return grant.SourceProviderId is null
                ? effective
                : [.. effective.Where(key => key != Permissions.SystemAdministrator)];
        }

        /// <summary>Whether this grant makes its holder staff in its activity.</summary>
        public static bool IsStaff(this HeldGrant grant) => Permissions.IsStaff(grant.Confers());
    }
}

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// The one place a permission question is answered.
    /// <para>
    /// Every method takes an optional activity. Null means the system scope, and
    /// a system grant carries into every activity — so asking about an activity
    /// is asking about the union of the two, never about the activity grant
    /// alone.
    /// </para>
    /// </summary>
    public interface IPermissionService
    {
        /// <summary>Whether the caller holds it, in this scope.</summary>
        Task<bool> HasAsync(string permission, Guid? activityId = null, CancellationToken ct = default);

        /// <summary>Whether the caller holds any of them, in this scope.</summary>
        Task<bool> HasAnyAsync(IEnumerable<string> permissions, Guid? activityId = null, CancellationToken ct = default);

        /// <summary>
        /// Throws <see cref="Utils.AccessDeniedException"/> unless the caller
        /// holds it.
        /// <para>
        /// The default for an endpoint. <see cref="HasAsync"/> is for deciding
        /// what a projection contains, not for deciding whether to answer — a
        /// refusal written as a filter is a refusal some later endpoint forgets.
        /// </para>
        /// </summary>
        Task RequireAsync(string permission, Guid? activityId = null, CancellationToken ct = default);

        /// <summary>
        /// Everything the caller holds in this scope, the system grant included.
        /// A system administrator gets the whole catalogue, because that is what
        /// the bypass means to a grant editor drawing what may be handed on.
        /// </summary>
        Task<IReadOnlySet<string>> EffectiveAsync(Guid? activityId = null, CancellationToken ct = default);

        /// <summary>
        /// Everything the caller holds <b>anywhere</b> — the system grant unioned
        /// with every activity grant.
        /// <para>
        /// A different question from <see cref="EffectiveAsync"/> and deliberately
        /// a separate call: it decides whether the manager panel has anything in
        /// it for this person, and somebody who manages one course and nothing
        /// else still needs the panel that course lives in.
        /// </para>
        /// </summary>
        Task<IReadOnlySet<string>> AnywhereAsync(CancellationToken ct = default);

        /// <summary>
        /// The activities the caller holds this permission in. Null means every
        /// activity, which is what a system grant answers.
        /// </summary>
        Task<IReadOnlyCollection<Guid>?> ActivitiesWithAsync(string permission, CancellationToken ct = default);
    }
}

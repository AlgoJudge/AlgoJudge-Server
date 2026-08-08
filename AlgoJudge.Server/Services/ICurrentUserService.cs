using AlgoJudge.Server.Database.Models;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// Who is asking. One notion of the current user, resolved from the request's
    /// claims and cached for its lifetime.
    /// </summary>
    public interface ICurrentUserService
    {
        /// <summary>The signed-in account, or null when nobody is signed in.</summary>
        Task<User?> GetAsync(CancellationToken ct = default);

        /// <summary>
        /// The signed-in account's id without touching the database — it is in
        /// the claims already, and most authorization questions need nothing else.
        /// </summary>
        string? UserId { get; }

        /// <summary>Throws <see cref="Utils.UnauthenticatedException"/> when nobody is signed in.</summary>
        Task<User> RequireAsync(CancellationToken ct = default);
    }
}

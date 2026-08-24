using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// An account that has run out does not sign in.
    /// <para>
    /// <b>The framework's own hook, so that nothing is written.</b>
    /// <see cref="SignInManager{TUser}.PreSignInCheck"/> asks this before every
    /// password sign-in; answering here leaves <see cref="User.ExpiresAt"/> the
    /// single source of truth. Writing a lockout from the date instead would put
    /// a manager and the clock on one field, and both ways they disagree are
    /// silent — unblocking would defeat the expiry, moving the date would leave
    /// a stale block.
    /// </para>
    /// <para>
    /// <b>It is half of the answer, not the whole of it.</b> This closes the
    /// door; <see cref="BlockedGate"/> is what stops somebody who was already
    /// through it when the date passed.
    /// </para>
    /// </summary>
    public class ExpiringSignInManager(
        UserManager<User> users,
        IHttpContextAccessor contexts,
        IUserClaimsPrincipalFactory<User> claims,
        IOptions<IdentityOptions> options,
        ILogger<SignInManager<User>> logger,
        IAuthenticationSchemeProvider schemes,
        IUserConfirmation<User> confirmation,
        TimeProvider clock
    ) : SignInManager<User>(users, contexts, claims, options, logger, schemes, confirmation)
    {
        public override async Task<bool> CanSignInAsync(User user) =>
            !user.HasExpired(clock.GetUtcNow()) && await base.CanSignInAsync(user);
    }
}

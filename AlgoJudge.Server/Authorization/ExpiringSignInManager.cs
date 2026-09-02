using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// An account that has run out, that nobody approved, or whose address this
    /// instance requires confirmed does not sign in.
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
        TimeProvider clock,
        IInstanceService instances
    ) : SignInManager<User>(users, contexts, claims, options, logger, schemes, confirmation)
    {
        public override async Task<bool> CanSignInAsync(User user)
        {
            if (user.HasExpired(clock.GetUtcNow()))
            {
                return false;
            }

            // **Nobody decided this account may be used.** Every way an account
            // comes into being stamps `ApprovedAt` — a provider's first sign-in,
            // an account staff created, a temporary login handed out — except
            // one: somebody registering themselves at `/identity/register`. So
            // this refuses exactly the accounts local registration lets in, and
            // `POST /panel/users/{id}/approve` is what lets them through.
            if (user.ApprovedAt is null)
            {
                return false;
            }

            // **Only where an address is what ties the account to a person.**
            // A temporary login has no address at all and is the permanent
            // exception to end-user passwords; refusing it here would break the
            // slips-of-paper case for a rule about mailboxes.
            //
            // Federated sign-in does not come through here — the controller
            // calls `SignInAsync` directly — and that is right: an identity is
            // keyed on issuer plus `sub`, never the address, and editing one's
            // own address clears `EmailConfirmed`. Gating a provider sign-in on
            // it would let somebody lock themselves out of an account whose
            // owner the issuer is still vouching for.
            if (!user.IsTemporary && !user.EmailConfirmed)
            {
                var instance = await instances.EnsureAsync(CancellationToken.None);
                if (instance.RequireConfirmedEmail)
                {
                    return false;
                }
            }

            return await base.CanSignInAsync(user);
        }
    }
}

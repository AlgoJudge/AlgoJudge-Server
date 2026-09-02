using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// A blocked or expired account stops working <b>now</b>, not when its
    /// cookie next happens to be revalidated.
    /// <para>
    /// <b>Blocking was `LockoutEnd`, and `LockoutEnd` is checked at sign-in.</b>
    /// Somebody already signed in carried on until Identity revalidated the
    /// security stamp — the framework's default is every thirty minutes — so
    /// "blocked" meant "blocked from signing in again". A manager blocking an
    /// account mid-contest, and §11 merging one away, both need it to mean
    /// blocked from doing anything.
    /// </para>
    /// <para>
    /// The alternative was <c>SecurityStampValidationInterval = Zero</c>, which
    /// buys the same immediacy with a database read per request for everybody.
    /// This costs one read on the paths that were going to load the user anyway.
    /// </para>
    /// <para>
    /// <b>Expiry rides the same check and answers a different code.</b> A
    /// temporary account handed out for one contest carries a date; the manager
    /// screen has drawn it as "expired" since before anything enforced it, so
    /// the two states have to stay tellable apart on the way out as well.
    /// </para>
    /// </summary>
    public static class BlockedGate
    {
        /// <summary>
        /// Identity's own endpoints stay open: signing out is what somebody
        /// holding a dead session should still be able to do, and the sign-in
        /// path refuses on its own terms with its own message.
        /// </summary>
        private const string Identity = "/identity";

        public static IApplicationBuilder UseBlockedGate(this IApplicationBuilder app) =>
            app.Use(async (context, next) =>
            {
                if (context.User?.Identity?.IsAuthenticated != true
                    || (context.Request.Path.Value ?? "").StartsWith(
                        Identity, StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                var current = context.RequestServices.GetRequiredService<ICurrentUserService>();
                var user = await current.GetAsync(context.RequestAborted);

                // Not found is not this gate's answer: an id in a cookie whose
                // row is gone is the deletion path's business, and saying
                // "blocked" about it would be a guess.
                var now = DateTimeOffset.UtcNow;
                if (user is null
                    || (!user.IsBlocked(now) && !user.HasExpired(now)
                        && user.ApprovedAt is not null))
                {
                    await next();
                    return;
                }

                // Blocked first: somebody stopped this account on purpose, and
                // that is the more useful thing to be told.
                if (user.IsBlocked(now))
                {
                    throw new ForbiddenActionException(
                        user.BlockedReason is { Length: > 0 } reason
                            ? $"This account is blocked: {reason}"
                            : "This account is blocked",
                        "account.blocked");
                }

                if (user.HasExpired(now))
                {
                    throw new ForbiddenActionException(
                        "This account has expired", "account.expired");
                }

                // **The half a sign-in check cannot do**, same as expiry: an
                // account that registered itself before this rule existed still
                // holds a cookie.
                //
                // Approval only. Whether the instance requires a confirmed
                // address is asked at sign-in and not here: it is a second row
                // to read on every authenticated request, and unlike blocking
                // and expiry nothing changes it behind somebody's back.
                throw new ForbiddenActionException(
                    "This account is waiting for approval", "account.pendingApproval");
            });
    }
}

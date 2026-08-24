using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// A blocked account stops working <b>now</b>, not when its cookie next
    /// happens to be revalidated.
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
                if (user is null || !user.IsBlocked(DateTimeOffset.UtcNow))
                {
                    await next();
                    return;
                }

                throw new ForbiddenActionException(
                    user.BlockedReason is { Length: > 0 } reason
                        ? $"This account is blocked: {reason}"
                        : "This account is blocked",
                    "account.blocked");
            });
    }
}

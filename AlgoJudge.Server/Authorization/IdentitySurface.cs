using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// Closes the parts of <c>MapIdentityApi</c> this product has decided not to
    /// have.
    /// <para>
    /// <c>MapIdentityApi</c> maps its whole surface unconditionally — register,
    /// forgot-password, reset-password, resend-confirmation — and there is no
    /// option to omit any of it. Two of those contradict decisions already taken:
    /// </para>
    /// <list type="bullet">
    /// <item><b>There is no self-service registration.</b> Accounts are created
    /// by an organiser or arrive by SSO, and blocking local sign-ups is an
    /// instance setting shipped <b>on</b>. Left alone, an installation that
    /// declares registration closed accepts registrations anyway, and the new
    /// account can sign in — the setting would be a label rather than a rule.</item>
    /// <item><b>There is no mail sender in v1</b>, so there is no password reset
    /// and no confirmation to resend. An endpoint that exists and cannot work is
    /// worse than one that refuses: it invites a screen to promise something
    /// nothing will deliver.</item>
    /// </list>
    /// <para>
    /// <b>Middleware, not an endpoint filter.</b> A filter runs after the body
    /// has been bound, so a request whose JSON does not satisfy the framework's
    /// own model never reaches it — `resetPassword` with a partial body failed at
    /// binding and was answered before the refusal applied. What is wanted is a
    /// refusal in front of the endpoint, which means before binding.
    /// </para>
    /// </summary>
    public static class IdentitySurface
    {
        private const string Group = "/identity";

        /// <summary>Needs a mail sender the product does not have.</summary>
        private static readonly string[] NeedsMail =
        [
            "/forgotPassword",
            "/resetPassword",
            "/resendConfirmationEmail",
        ];

        public static IApplicationBuilder UseIdentitySurfaceRules(this IApplicationBuilder app) =>
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? "";

                if (!path.StartsWith(Group, StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                if (path.EndsWith("/register", StringComparison.OrdinalIgnoreCase))
                {
                    var instances = context.RequestServices.GetRequiredService<IInstanceService>();
                    var instance = await instances.EnsureAsync(context.RequestAborted);

                    if (!instance.LocalRegistrationEnabled)
                    {
                        throw new ForbiddenActionException(
                            "This instance does not accept sign-ups", "registration.closed");
                    }
                }

                foreach (var blocked in NeedsMail)
                {
                    if (path.EndsWith(blocked, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ForbiddenActionException(
                            "This instance sends no mail, so this is unavailable", "mail.unavailable");
                    }
                }

                await next();
            });
    }
}

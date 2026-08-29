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

        /// <summary>
        /// Cannot answer for an account this product deliberately allows.
        /// <para>
        /// <c>MapIdentityApi</c> builds this response with
        /// <c>GetEmailAsync(user) ?? throw new NotSupportedException("Users must
        /// have an email.")</c>, so it answers <b>500</b> to a perfectly valid
        /// session whenever the account has no address. This product allows
        /// exactly that — see <see cref="OptionalEmailValidator"/>, because a
        /// room of bulk logins handed out on paper has no mailboxes — and the
        /// **seeded administrator is such an account**. Reproduced against the
        /// development stack on 2026-08-29; it had once been read as the key
        /// ring having failed.
        /// </para>
        /// <para>
        /// <b>There is no switch to turn off.</b> `MapIdentityApi` has a single
        /// overload and no options type, and the throw is inline in its handler.
        /// `RequireUniqueEmail` does not help: it is about a **duplicate**
        /// address, not an absent one, and this product already splits those two
        /// halves. So the choice is between publishing an endpoint that 500s for
        /// a whole class of accounts and refusing it.
        /// </para>
        /// <para>
        /// <b>Nothing is lost.</b> `GET /api/v1/account` is this product's own
        /// answer and carries `SessionDto` for every account it allows. The
        /// `POST` half changes an address or a password, and an administrator's
        /// password is set with `aj-admin password`.
        /// </para>
        /// </summary>
        private static readonly string[] Unanswerable =
        [
            "/manage/info",
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

                foreach (var blocked in Unanswerable)
                {
                    if (path.EndsWith(blocked, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ForbiddenActionException(
                            "This endpoint cannot answer for an account without an address; "
                            + "read /account instead, which answers for every account",
                            "identity.info.unavailable");
                    }
                }

                await next();
            });
    }
}

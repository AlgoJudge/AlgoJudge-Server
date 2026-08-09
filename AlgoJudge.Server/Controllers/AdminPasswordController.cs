using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AlgoJudge.Server.Controllers
{
    /// <summary>
    /// The way into a fresh installation, and the way back into a locked one.
    /// <para>
    /// A seeded installation has an <c>admin</c> account whose password is
    /// twenty random characters <b>nobody has ever been told</b> — not logged,
    /// not returned, not derivable. That is deliberate: a well-known default
    /// administrator password is the single most reliable way an installation is
    /// taken over, and the alternative to a default is a password nobody knows
    /// and a documented way to set one. This is that way.
    /// </para>
    /// <para>
    /// Guarded by <see cref="AdminSurface"/> like everything under
    /// <c>/admin</c>: the loopback interface <b>and</b> the configured token.
    /// Anonymous, because the case it exists for is precisely that nobody can
    /// sign in.
    /// </para>
    /// </summary>
    [ApiController]
    [Route("admin/password")]
    [AllowAnonymous]
    public class AdminPasswordController(
        UserManager<User> users,
        ApplicationDbContext context,
        ILogger<AdminPasswordController> logger
    ) : ControllerBase
    {
        /// <summary>
        /// Sets the password of the account named <c>admin</c>, and nothing else.
        /// <para>
        /// One account by name rather than a parameter. Everything else about an
        /// account is a manager's business through the panel, reached with a
        /// session; this exists only for the case where there is no session to
        /// be had, and widening it to "any account" would make it a way to take
        /// over any account.
        /// </para>
        /// </summary>
        [HttpPost]
        [ProducesResponseType<AdminPasswordDto>(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        public async Task<AdminPasswordDto> Set(
            [FromBody] AdminPasswordInputDto input, CancellationToken ct)
        {
            if (!Peer.IsLoopback(HttpContext)) throw new NotFoundException("Endpoint");

            var wanted = input.Password ?? "";
            if (wanted.Length == 0)
            {
                throw new ValidationException("A password is required", "admin.password.required");
            }

            var admin = await users.FindByNameAsync(Seeder.AdminLogin)
                ?? throw new NotFoundException("Account");

            // The same two steps a manager's reset takes (`UserService`), so the
            // password policy configured in `Program.cs` applies here as well —
            // an operator setting `12345` on the account that bypasses every
            // check would be a worse outcome than a refused request.
            var token = await users.GeneratePasswordResetTokenAsync(admin);
            var reset = await users.ResetPasswordAsync(admin, token, wanted);
            if (!reset.Succeeded)
            {
                // Nothing has changed at this point, which is what makes a
                // refusal safe: the old password still works, so a typo does not
                // leave an installation with no way in.
                throw new ValidationException(
                    string.Join("; ", reset.Errors.Select(e => e.Description)), "admin.password");
            }

            // **And let them in.** Ten wrong guesses lock an account for an
            // hour, and the reason somebody is at this endpoint is often exactly
            // that they have been guessing. A reset that left the lockout
            // standing would have done half the job and looked like it had
            // failed.
            admin.AccessFailedCount = 0;
            admin.LockoutEnd = null;
            await context.SaveChangesAsync(ct);

            // Said, because an administrator's password changing is worth a line
            // in the record. **The password is not in it**, and neither is the
            // token that authorized the call.
            logger.LogWarning(
                "The password of {Login} was set through the admin surface", Seeder.AdminLogin);

            return new AdminPasswordDto { Username = admin.UserName ?? Seeder.AdminLogin };
        }
    }
}

using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Identity;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// No account is <b>created</b> holding the login <c>admin</c>.
    /// <para>
    /// <b>A validator rather than a check at three call sites</b>, and that is
    /// the whole reason this class exists. Accounts are created down four
    /// separate paths — a manager creating one, a room full of temporary logins,
    /// the seed, and <c>MapIdentityApi</c>'s <c>/register</c> — and the last of
    /// those is framework code this product does not own and cannot add a check
    /// to. All four go through <c>UserManager.CreateAsync</c>, which runs this
    /// <b>before</b> the row is written.
    /// </para>
    /// <para>
    /// <b>Renaming is guarded elsewhere</b>, in <c>AccountService</c>, and it has
    /// to be: <c>UserManager.SetUserNameAsync</c> writes the new name to the
    /// database itself, so by the time <c>UpdateAsync</c> ran this the rename
    /// would already have happened and a refusal here would report a change that
    /// had been made. A validator can only defend the paths that validate before
    /// they write.
    /// </para>
    /// <para>
    /// It matters because the name has started to mean something.
    /// <c>POST /admin/password</c> resets <i>the account named <c>admin</c></i>,
    /// so whoever holds that name holds the endpoint. Uniqueness alone does not
    /// say so: it would let the name be taken the moment nobody had it.
    /// </para>
    /// </summary>
    public class ReservedLoginValidator : IUserValidator<User>
    {
        private const string Code = "ReservedUserName";

        public async Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user)
        {
            var login = (await manager.GetUserNameAsync(user) ?? "").Trim();
            var id = await manager.GetUserIdAsync(user);

            // Case-insensitively, because Identity normalizes to upper case and
            // `Admin` and `ADMIN` are the same login by the time anything looks
            // one up. Comparing the raw string would reserve one spelling.
            var wantsIt = string.Equals(login, Seeder.AdminLogin, StringComparison.OrdinalIgnoreCase);

            var holder = await manager.FindByNameAsync(Seeder.AdminLogin);

            // **Nobody holds it yet, so this is the seed taking it.** The seed
            // runs at startup, before `app.Run()` serves a single request, so
            // there is no moment in which a registration could win this race —
            // and refusing here instead would mean the rule forbade the account
            // it exists to protect.
            if (holder is null) return IdentityResult.Success;

            var isHolder = string.Equals(
                await manager.GetUserIdAsync(holder), id, StringComparison.Ordinal);

            if (wantsIt && !isHolder)
            {
                return Failed(
                    $"The login {Seeder.AdminLogin} is reserved for this installation's administrator.");
            }

            return IdentityResult.Success;
        }

        private static IdentityResult Failed(string description) =>
            IdentityResult.Failed(new IdentityError { Code = Code, Description = description });
    }
}

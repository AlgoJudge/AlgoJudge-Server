using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Identity;

namespace AlgoJudge.Server.Authorization
{
    /// <summary>
    /// An address is unique when there is one, and optional when there is not.
    /// <para>
    /// ASP.NET Core's own validator reads <c>RequireUniqueEmail</c> as "every
    /// account must have an address, and it must be unique" — the two are one
    /// setting. That makes a <b>temporary account impossible</b>: a room full of
    /// bulk logins handed out on paper has no mailboxes behind it, and there is
    /// no mail sender in v1 to use one for anything.
    /// </para>
    /// <para>
    /// Turning <c>RequireUniqueEmail</c> off instead would give up the half that
    /// matters — two accounts on one address is how a password reset, once there
    /// is one, delivers to the wrong person. So the two halves are separated
    /// here: absent is allowed, present is unique.
    /// </para>
    /// </summary>
    public class OptionalEmailValidator : IUserValidator<User>
    {
        public async Task<IdentityResult> ValidateAsync(UserManager<User> manager, User user)
        {
            var errors = new List<IdentityError>();

            var login = await manager.GetUserNameAsync(user);
            if (string.IsNullOrWhiteSpace(login))
            {
                errors.Add(manager.ErrorDescriber.InvalidUserName(login));
            }
            else
            {
                var owner = await manager.FindByNameAsync(login);
                if (owner is not null && !string.Equals(
                        await manager.GetUserIdAsync(owner), await manager.GetUserIdAsync(user),
                        StringComparison.Ordinal))
                {
                    errors.Add(manager.ErrorDescriber.DuplicateUserName(login));
                }
            }

            var email = await manager.GetEmailAsync(user);
            if (!string.IsNullOrWhiteSpace(email))
            {
                if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
                {
                    errors.Add(manager.ErrorDescriber.InvalidEmail(email));
                }
                else
                {
                    var owner = await manager.FindByEmailAsync(email);
                    if (owner is not null && !string.Equals(
                            await manager.GetUserIdAsync(owner), await manager.GetUserIdAsync(user),
                            StringComparison.Ordinal))
                    {
                        errors.Add(manager.ErrorDescriber.DuplicateEmail(email));
                    }
                }
            }

            return errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]);
        }
    }
}

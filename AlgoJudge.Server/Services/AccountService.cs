using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IAccountService
    {
        Task<SessionDto> UpdateProfileAsync(ProfileInputDto input, CancellationToken ct);
        Task ChangePasswordAsync(string current, string replacement, CancellationToken ct);
        Task<IReadOnlyList<AccountLinkDto>> LinksAsync(CancellationToken ct);
        Task<byte[]> ExportAsync(CancellationToken ct);
        Task DeleteAsync(string password, CancellationToken ct);
    }

    public class AccountService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        UserManager<User> users,
        IAccountDeletionService deletion,
        IInstanceService instances,
        TimeProvider clock
    ) : IAccountService
    {
        /// <summary>
        /// Refuses what belongs to an identity provider.
        /// <para>
        /// The rule — "an SSO account may change none of its own profile fields"
        /// — was decided on 2026-08-04 and, until phase 2, was enforced only by
        /// the Client greying its inputs. A rule applied by a screen is a rule
        /// anybody with a terminal can ignore, so it lives here now.
        /// </para>
        /// </summary>
        private static void RequireLocal(User user, string what)
        {
            if (Projections.IsLocal(user)) return;

            throw new ForbiddenActionException(
                $"This account is managed by an identity provider; {what} there",
                "account.federated");
        }

        public async Task<SessionDto> UpdateProfileAsync(ProfileInputDto input, CancellationToken ct)
        {
            var user = await currentUser.RequireAsync(ct);
            RequireLocal(user, "change your details");

            if (input.Username is { } username && username.Trim() != user.UserName)
            {
                var wanted = username.Trim();
                if (wanted.Length == 0)
                {
                    throw new ValidationException("A login is required", "account.username.required");
                }

                // **Before `SetUserNameAsync`, and that is not a style choice.**
                // That method writes the new name to the database itself — the
                // EF store saves on update — so a rule enforced by an
                // `IUserValidator` would run on the `UpdateAsync` below, after
                // the rename had already happened, and refuse a change it had
                // just let through. Renaming is the one account path a validator
                // cannot guard, so it is guarded here.
                if (string.Equals(wanted, Seeder.AdminLogin, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConflictException(
                        $"The login {Seeder.AdminLogin} is reserved for this installation's administrator",
                        "account.username.reserved");
                }
                if (string.Equals(user.UserName, Seeder.AdminLogin, StringComparison.OrdinalIgnoreCase))
                {
                    // `POST /admin/password` resets the account *named* `admin`.
                    // An administrator who renames themselves leaves it pointing
                    // at nothing, and no session anywhere able to put it back.
                    throw new ConflictException(
                        $"This installation's administrator keeps the login {Seeder.AdminLogin}",
                        "account.username.reserved");
                }

                if (await users.FindByNameAsync(wanted) is not null)
                {
                    throw new ConflictException("That login is taken", "account.username.taken");
                }
                await users.SetUserNameAsync(user, wanted);
            }

            if (input.Email is { } email && email.Trim() != user.Email)
            {
                await users.SetEmailAsync(user, email.Trim());
                // A changed address is an unconfirmed address. Carrying the old
                // confirmation forward would let somebody confirm one mailbox and
                // then move the account to another.
                user.EmailConfirmed = false;
            }

            if (input.FirstName is not null) user.FirstName = input.FirstName.Trim();
            if (input.LastName is not null) user.LastName = input.LastName.Trim();

            await users.UpdateAsync(user);
            return Projections.Session(user);
        }

        public async Task ChangePasswordAsync(string current, string replacement, CancellationToken ct)
        {
            var user = await currentUser.RequireAsync(ct);
            RequireLocal(user, "change your password");

            var changed = await users.ChangePasswordAsync(user, current, replacement);
            if (!changed.Succeeded)
            {
                // Identity distinguishes "the current password is wrong" from
                // "the new one is too short"; both are the caller's input, and
                // its own messages are better than any this could invent.
                throw new ValidationException(
                    string.Join("; ", changed.Errors.Select(e => e.Description)), "account.password");
            }
        }

        /// <summary>
        /// Everything held about the signed-in person, as a document they can
        /// keep.
        /// <para>
        /// What is here rather than what a schema walk would produce: the account,
        /// where they are enrolled, what they submitted and how it went. A dump
        /// of every row touching their id would include other people's activities
        /// and the contents of problems, which is not theirs to take.
        /// </para>
        /// </summary>
        /// <summary>
        /// The ways this person can sign in, and where each of them is managed.
        /// <para>
        /// Its own read rather than a field on the session: the session is
        /// fetched on every page load and this is wanted by one screen, so
        /// joining two more tables into it would charge every reader for what
        /// the account page asks once.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<AccountLinkDto>> LinksAsync(CancellationToken ct)
        {
            var user = await currentUser.RequireAsync(ct);

            return await context.UserIdentities
                .AsNoTracking()
                .Where(i => i.UserId == user.Id)
                .OrderBy(i => i.LinkedAt)
                .Select(i => new AccountLinkDto
                {
                    ProviderSlug = i.Provider!.Slug,
                    DisplayName = i.Provider!.DisplayName,
                    AccountUrl = i.Provider!.AccountUrl,
                    DeletionUrl = i.Provider!.DeletionUrl,
                    LinkedAt = Wire.At(i.LinkedAt),
                })
                .ToListAsync(ct);
        }

        public async Task<byte[]> ExportAsync(CancellationToken ct)
        {
            var user = await currentUser.RequireAsync(ct);

            var grants = await context.Grants.AsNoTracking()
                .Where(g => g.UserId == user.Id)
                .Include(g => g.Activity)
                .ToListAsync(ct);

            var submissions = await context.Submissions.AsNoTracking()
                .Where(s => s.UserId == user.Id)
                .Include(s => s.SeriesProblem)!.ThenInclude(sp => sp!.Activity)
                .Include(s => s.Jobs).ThenInclude(j => j.Result)
                .ToListAsync(ct);

            var questions = await context.Questions.AsNoTracking()
                .Where(q => q.AuthorUserId == user.Id)
                .ToListAsync(ct);

            var document = new
            {
                exportedAt = Wire.At(clock.GetUtcNow().UtcDateTime),
                account = new
                {
                    id = user.Id,
                    username = user.UserName,
                    email = user.Email,
                    emailConfirmed = user.EmailConfirmed,
                    firstName = user.FirstName,
                    lastName = user.LastName,
                    createdAt = Wire.At(user.CreatedAt),
                    lastSeenAt = Wire.At(user.LastSeenAt),
                    isTemporary = user.IsTemporary,
                },
                activities = grants.Select(g => new
                {
                    activity = g.Activity?.Name,
                    slug = g.Activity?.Slug,
                    state = g.State.ToString(),
                    joinedAt = Wire.At(g.CreatedAt),
                }),
                submissions = submissions.Select(s => new
                {
                    id = Wire.Id(s.Id),
                    activity = s.SeriesProblem?.Activity?.Slug,
                    problem = s.SeriesProblem?.Slug,
                    submittedAt = Wire.At(s.CreatedDate),
                    language = s.Language,
                    attempts = s.Jobs.Count,
                    score = Scoring.Current(s)?.Result?.Score,
                    verdict = Scoring.Current(s)?.Result?.Verdict,
                }),
                questions = questions.Select(q => new
                {
                    topic = q.Topic,
                    body = q.Body,
                    askedAt = Wire.At(q.CreatedAt),
                    answered = q.AnswerBody is not null,
                }),
            };

            return JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }

        /// <summary>
        /// Deletion is <b>anonymisation</b>, immediate and with no grace period.
        /// <para>
        /// <see cref="Submission"/> and <see cref="Result"/> reference this row's
        /// id and it has to stay resolvable — a contest's history cannot develop
        /// holes because somebody left. So the row survives, emptied.
        /// </para>
        /// <para>
        /// Anonymising the user row is <b>not enough</b>: identity is also in the
        /// text they wrote. Their questions go with them.
        /// </para>
        /// </summary>
        public async Task DeleteAsync(string password, CancellationToken ct)
        {
            var instance = await instances.EnsureAsync(ct);
            if (!instance.AccountDeletionEnabled)
            {
                throw new ForbiddenActionException(
                    "This instance does not offer self-service account removal",
                    "account.deletion.closed");
            }

            var user = await currentUser.RequireAsync(ct);

            // This channel is the local account's, and it asks for a password an
            // account owned by a provider does not have. An SSO account leaves by
            // de-registering its link instead — a different act with a different
            // outcome, since it may still have another way in.
            RequireLocal(user, "remove your account");

            if (!await users.CheckPasswordAsync(user, password))
            {
                throw new ValidationException("The password is wrong", "account.password.wrong");
            }

            // The emptying itself lives in one place, shared with the two
            // channels an SSO account uses. Three copies of "what anonymising
            // means" would be three answers the day a field is added.
            await deletion.AnonymiseAsync(user, ct);

            await context.SaveChangesAsync(ct);
            await users.UpdateAsync(user);
        }
    }
}

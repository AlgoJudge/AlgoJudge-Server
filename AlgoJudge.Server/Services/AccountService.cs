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
        Task<byte[]> ExportAsync(CancellationToken ct);
        Task DeleteAsync(string password, CancellationToken ct);
    }

    public class AccountService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        UserManager<User> users,
        TimeProvider clock
    ) : IAccountService
    {
        public async Task<SessionDto> UpdateProfileAsync(ProfileInputDto input, CancellationToken ct)
        {
            var user = await currentUser.RequireAsync(ct);

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
            var user = await currentUser.RequireAsync(ct);

            if (!await users.CheckPasswordAsync(user, password))
            {
                throw new ValidationException("The password is wrong", "account.password.wrong");
            }

            var suffix = user.Id.Length >= 4 ? user.Id[^4..] : user.Id;

            user.UserName = $"deleted-{suffix}";
            user.NormalizedUserName = user.UserName.ToUpperInvariant();
            user.Email = null;
            user.NormalizedEmail = null;
            user.EmailConfirmed = false;
            user.FirstName = null;
            user.LastName = null;
            user.Note = null;
            user.Tags = null;
            user.PasswordHash = null;
            user.SecurityStamp = Guid.NewGuid().ToString();
            user.Anonymized = true;

            // The text they wrote carries their name as surely as the name field
            // does — a question signed with a class and a surname is not
            // anonymous because the account row is.
            var authored = await context.Questions
                .Where(q => q.AuthorUserId == user.Id)
                .ToListAsync(ct);
            foreach (var question in authored)
            {
                question.Topic = "[deleted]";
                question.Body = "[deleted]";
            }

            // Sessions end with the account, so nothing signed in survives it.
            var sessions = await context.UserSessions
                .Where(s => s.UserId == user.Id && s.EndedAt == null)
                .ToListAsync(ct);
            foreach (var session in sessions) session.EndedAt = clock.GetUtcNow().UtcDateTime;

            await context.SaveChangesAsync(ct);
            await users.UpdateAsync(user);
        }
    }
}

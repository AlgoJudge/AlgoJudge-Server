using System.Text.Json;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Services.Models;
using AlgoJudge.Server.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IUserService
    {
        Task<IReadOnlyList<ManagedUserSummaryDto>> SearchAsync(string? query, CancellationToken ct);
        Task<PageDto<ManagedUserDto>> ListAsync(
            PageQuery paging, string? search, bool includeBlocked, bool temporaryOnly, CancellationToken ct);
        Task<CreatedCredentialDto> CreateAsync(UserInputDto input, CancellationToken ct);
        Task<IReadOnlyList<CreatedCredentialDto>> CreateTemporaryAsync(BulkUserInputDto input, CancellationToken ct);
        Task<ManagedUserDto> UpdateAsync(string id, UserUpdateInputDto input, CancellationToken ct);
        Task<ManagedUserDto> SetBlockedAsync(string id, bool blocked, string? reason, CancellationToken ct);
        Task<ManagedUserDto> ApproveAsync(string id, CancellationToken ct);
        Task<CreatedCredentialDto> ResetPasswordAsync(string id, CancellationToken ct);
        Task<IReadOnlyList<UserSessionDto>> SessionsAsync(string userId, CancellationToken ct);
    }

    public class UserService(
        ApplicationDbContext context,
        UserManager<User> users,
        IPermissionService permissions,
        ICurrentUserService currentUser,
        IEventHub events,
        TimeProvider clock
    ) : IUserService
    {
        public async Task<IReadOnlyList<ManagedUserSummaryDto>> SearchAsync(string? query, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserReadAll, null, ct);

            var needle = (query ?? "").Trim().ToLower();
            if (needle.Length == 0) return [];

            var found = await context.Users
                .AsNoTracking()
                .Where(u => !u.Anonymized && (
                    u.UserName!.ToLower().Contains(needle)
                    || (u.Email != null && u.Email.ToLower().Contains(needle))
                    || (u.FirstName != null && u.FirstName.ToLower().Contains(needle))
                    || (u.LastName != null && u.LastName.ToLower().Contains(needle))))
                .OrderBy(u => u.UserName)
                .Take(20)
                .ToListAsync(ct);

            return found.Select(u => new ManagedUserSummaryDto
            {
                Id = u.Id,
                Username = u.UserName ?? u.Id,
                Name = Projections.DisplayName(u),
                Email = u.Email,
            }).ToList();
        }

        public async Task<PageDto<ManagedUserDto>> ListAsync(
            PageQuery paging, string? search, bool includeBlocked, bool temporaryOnly, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserReadAll, null, ct);
            var now = clock.GetUtcNow().UtcDateTime;

            var query = context.Users.AsNoTracking().AsQueryable();

            if (!includeBlocked) query = query.Where(u => u.LockoutEnd == null || u.LockoutEnd <= now);
            if (temporaryOnly) query = query.Where(u => u.IsTemporary);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim().ToLower();
                query = query.Where(u =>
                    u.UserName!.ToLower().Contains(needle)
                    || (u.Email != null && u.Email.ToLower().Contains(needle))
                    || (u.FirstName != null && u.FirstName.ToLower().Contains(needle))
                    || (u.LastName != null && u.LastName.ToLower().Contains(needle)));
            }

            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            var ids = page.Select(u => u.Id).ToList();
            var grantCounts = await context.Grants
                .AsNoTracking()
                .Where(g => ids.Contains(g.UserId))
                .GroupBy(g => g.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.UserId, g => g.Count, ct);

            return new PageDto<ManagedUserDto>
            {
                Items = page.Select(u => Project(u, grantCounts.GetValueOrDefault(u.Id))).ToList(),
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        private static ManagedUserDto Project(User user, int grantCount) => new()
        {
            Id = user.Id,
            Username = user.UserName ?? user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Email = user.Email,
            EmailConfirmed = user.EmailConfirmed,
            ApprovedAt = Wire.At(user.ApprovedAt),
            Note = user.Note,
            Tags = ParseTags(user.Tags),
            IsTemporary = user.IsTemporary,
            ExpiresAt = Wire.At(user.ExpiresAt),
            // Blocking is LockoutEnd and nothing else. Reading it back as a date
            // rather than a boolean keeps the one fact in one place.
            BlockedAt = user.LockoutEnd is { } end ? Wire.At(end.UtcDateTime) : null,
            BlockedReason = user.BlockedReason,
            CreatedAt = Wire.At(user.CreatedAt),
            LastSeenAt = Wire.At(user.LastSeenAt),
            GrantCount = grantCount,
        };

        private static IReadOnlyList<string> ParseTags(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        public async Task<CreatedCredentialDto> CreateAsync(UserInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserCreate, null, ct);

            var login = input.Username?.Trim() ?? "";
            if (login.Length == 0) throw new ValidationException("A login is required", "user.username.required");
            if (await users.FindByNameAsync(login) is not null)
            {
                throw new ConflictException("That username is taken", "user.username.taken");
            }

            var password = NewPassword();
            var user = new User
            {
                UserName = login,
                Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim(),
                FirstName = input.FirstName?.Trim(),
                LastName = input.LastName?.Trim(),
                // Created by staff, so approved by the act of creating it.
                ApprovedAt = clock.GetUtcNow().UtcDateTime,
                EmailConfirmed = false,
            };

            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                throw new ValidationException(
                    string.Join("; ", created.Errors.Select(e => e.Description)), "user.create");
            }

            return new CreatedCredentialDto { UserId = user.Id, Username = login, Password = password };
        }

        /// <summary>
        /// Bulk accounts for a room full of people, handed out on paper.
        /// <para>
        /// The permanent exception to "no end-user passwords in the Server": the
        /// password is generated here, returned <b>once</b>, and only a hash is
        /// kept.
        /// </para>
        /// </summary>
        public async Task<IReadOnlyList<CreatedCredentialDto>> CreateTemporaryAsync(
            BulkUserInputDto input, CancellationToken ct)
        {
            Guid? activityId = input.ActivityId is { } raw && Guid.TryParse(raw, out var parsed) ? parsed : null;
            await permissions.RequireAsync(Permissions.UserCreateTemporary, activityId, ct);
            var issuer = await currentUser.RequireAsync(ct);

            if (input.Count is < 1 or > 500)
            {
                throw new ValidationException(
                    "Create between 1 and 500 accounts at a time", "user.bulk.count");
            }

            var prefix = input.Prefix?.Trim().ToLowerInvariant() ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(prefix, "^[a-z0-9][a-z0-9-]*$"))
            {
                throw new ValidationException(
                    "The prefix may hold letters, digits and dashes only", "user.bulk.prefix");
            }

            var wanted = input.Permissions?.ToList() ?? [.. Permissions.ParticipantTemplate];
            if (activityId is not null)
            {
                var mine = await permissions.EffectiveAsync(activityId, ct);
                if (!mine.Contains(Permissions.SystemAdministrator))
                {
                    // The same rule as any other grant: nobody hands on what they
                    // do not hold, and enrolling in bulk is still granting.
                    var excess = wanted.Where(p => !mine.Contains(p)).ToList();
                    if (excess.Count > 0)
                    {
                        throw new ForbiddenActionException(
                            "Cannot grant permissions you do not hold: " + string.Join(", ", excess),
                            "grant.excess");
                    }
                }
            }

            var expires = ActivityService.ParseInstant(input.ExpiresAt);
            var tags = input.Tags is null ? null : JsonSerializer.Serialize(input.Tags);

            // Numbered from whatever this prefix has already reached, so running
            // it twice gives 001-020 then 021-040 rather than a wall of conflicts.
            var taken = await context.Users
                .Where(u => u.UserName!.StartsWith(prefix + "-"))
                .Select(u => u.UserName!)
                .ToListAsync(ct);

            var highest = taken
                .Select(name => int.TryParse(name[(prefix.Length + 1)..], out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();

            var credentials = new List<CreatedCredentialDto>(input.Count);

            for (var i = 1; i <= input.Count; i++)
            {
                var login = $"{prefix}-{highest + i:D3}";
                var password = NewPassword();

                var user = new User
                {
                    UserName = login,
                    IsTemporary = true,
                    ExpiresAt = expires,
                    Tags = tags,
                    ApprovedAt = clock.GetUtcNow().UtcDateTime,
                };

                var created = await users.CreateAsync(user, password);
                if (!created.Succeeded)
                {
                    throw new ConflictException(
                        $"Could not create {login}: "
                        + string.Join("; ", created.Errors.Select(e => e.Description)),
                        "user.bulk.create");
                }

                if (activityId is { } scoped)
                {
                    context.Grants.Add(new Grant
                    {
                        UserId = user.Id,
                        ActivityId = scoped,
                        Permissions = JsonSerializer.Serialize(wanted),
                        CreatedFromTemplate = "participant",
                        IsSystem = Permissions.IsStaff(wanted),
                        GrantedByUserId = issuer.Id,
                    });
                }

                credentials.Add(new CreatedCredentialDto
                {
                    UserId = user.Id, Username = login, Password = password,
                });
            }

            await context.SaveChangesAsync(ct);
            return credentials;
        }

        /// <summary>
        /// Twelve characters, from the one generator the Server has.
        /// <para>
        /// Handed to somebody on paper, so it is short enough to type; the seed
        /// asks the same generator for twenty, because that one is never read by
        /// anybody and length is free.
        /// </para>
        /// </summary>
        private static string NewPassword() => Passwords.Generate(12);

        public async Task<ManagedUserDto> UpdateAsync(string id, UserUpdateInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserUpdate, null, ct);

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
                ?? throw new NotFoundException("User");

            if (input.FirstName is not null) user.FirstName = input.FirstName.Trim();
            if (input.LastName is not null) user.LastName = input.LastName.Trim();
            if (input.Note is not null) user.Note = input.Note;
            if (input.Tags is not null) user.Tags = JsonSerializer.Serialize(input.Tags);

            if (input.Email is not null && input.Email.Trim() != user.Email)
            {
                user.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
                user.NormalizedEmail = user.Email?.ToUpperInvariant();
                user.EmailConfirmed = false;
            }

            await context.SaveChangesAsync(ct);
            var projected = Project(user, await context.Grants.CountAsync(g => g.UserId == id, ct));
            await events.SendToUserAsync(id, EventTypes.UserChanged, new { user = projected }, ct);
            return projected;
        }

        /// <summary>
        /// Blocking stops sign-in; it does not touch what they may do once in.
        /// Expressed as <c>LockoutEnd</c> and never as a second boolean.
        /// </summary>
        public async Task<ManagedUserDto> SetBlockedAsync(
            string id, bool blocked, string? reason, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserBlock, null, ct);

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
                ?? throw new NotFoundException("User");

            // Far enough out to be indefinite. A block is lifted by a person,
            // not by a clock — there is no "blocked until" in the product.
            user.LockoutEnd = blocked ? DateTimeOffset.MaxValue : null;
            user.BlockedReason = blocked ? reason : null;
            user.LockoutEnabled = true;

            await context.SaveChangesAsync(ct);
            return Project(user, await context.Grants.CountAsync(g => g.UserId == id, ct));
        }

        public async Task<ManagedUserDto> ApproveAsync(string id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserUpdate, null, ct);

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
                ?? throw new NotFoundException("User");

            // Approving is a decision a person made, and it is not the same fact
            // as a confirmed address.
            user.ApprovedAt ??= clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(ct);
            return Project(user, await context.Grants.CountAsync(g => g.UserId == id, ct));
        }

        public async Task<CreatedCredentialDto> ResetPasswordAsync(string id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserUpdate, null, ct);

            var user = await context.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
                ?? throw new NotFoundException("User");

            var password = NewPassword();
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var reset = await users.ResetPasswordAsync(user, token, password);
            if (!reset.Succeeded)
            {
                throw new ValidationException(
                    string.Join("; ", reset.Errors.Select(e => e.Description)), "user.password.reset");
            }

            // Handed over once. There is no mail sender, so a manager reads it
            // off the screen and passes it on — which is why it comes back here
            // rather than being sent anywhere.
            return new CreatedCredentialDto
            {
                UserId = user.Id, Username = user.UserName ?? user.Id, Password = password,
            };
        }

        public async Task<IReadOnlyList<UserSessionDto>> SessionsAsync(string userId, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserReadAll, null, ct);

            var sessions = await context.UserSessions
                .AsNoTracking()
                .Where(s => s.UserId == userId && s.EndedAt == null)
                .OrderByDescending(s => s.StartedAt)
                .ToListAsync(ct);

            var mine = currentUser.UserId;

            return sessions.Select(s => new UserSessionDto
            {
                Id = Wire.Id(s.Id),
                // Counted live from the connection registry, never stored: a
                // number written to a row survives a crash and then tells the
                // screen somebody is present who left hours ago.
                Connections = events.ConnectionsFor(s.UserId),
                StartedAt = Wire.At(s.StartedAt),
                LastRequestAt = Wire.At(s.LastRequestAt),
                LastRequestPath = s.LastRequestPath,
                // Text on the wire, `inet` in the row: an address is a string to
                // whoever reads the screen and a comparable value to whoever
                // asks which network it was on.
                IpAddress = s.IpAddress?.ToString(),
                UserAgent = s.UserAgent,
                ExpiresAt = Wire.At(s.ExpiresAt),
                IsCurrent = s.UserId == mine,
            }).ToList();
        }
    }
}

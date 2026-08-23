using System.Security.Cryptography;
using System.Text;
using AlgoJudge.Server.Api;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IAccountDeletionService
    {
        /// <summary>The OIDC account holder, de-registering one link or all of them.</summary>
        Task<DeletionRequestDto> FromHolderAsync(Guid? providerId, CancellationToken ct);

        /// <summary>The provider's back channel. Idempotent on <c>requestId</c>.</summary>
        Task<DeletionRequestDto> FromProviderAsync(
            IdentityProvider provider, ProviderDeletionInputDto input, CancellationToken ct);

        Task<PageDto<DeletionRequestDto>> ListAsync(
            Models.PageQuery paging, string? state, CancellationToken ct);

        Task<DeletionRequestDto> HaltAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Carries out everything whose window has closed. The sweeper's single
        /// unit of work, visible to the tests so it can be run against a clock
        /// somebody turns rather than waited for.
        /// </summary>
        Task<int> SweepAsync(CancellationToken ct);

        /// <summary>Empties an account in place. Shared, so the paths cannot drift.</summary>
        Task AnonymiseAsync(User user, CancellationToken ct);
    }

    /// <summary>
    /// Removing an account, from any of the three directions it can be asked.
    /// <para>
    /// <b>Deletion is anonymisation</b>, always: <c>Submission</c> and
    /// <c>Result</c> name a <c>userId</c> that has to stay resolvable, so a
    /// contest's history cannot develop holes because somebody left. The row
    /// survives, emptied.
    /// </para>
    /// <para>
    /// And a request does not always reach that far. What it removes first is a
    /// <b>way of signing in</b>; the account is emptied only if that was the last
    /// one — no other provider link, and no local credential.
    /// </para>
    /// </summary>
    public class AccountDeletionService(
        ApplicationDbContext context,
        ICurrentUserService currentUser,
        IPermissionService permissions,
        IInstanceService instances,
        TimeProvider clock,
        ILogger<AccountDeletionService> log
    ) : IAccountDeletionService
    {
        /// <summary>
        /// What an administrator gets to stop a machine request. Long enough to
        /// cover a night and a morning, which is the case it exists for.
        /// </summary>
        public static readonly TimeSpan ProviderWindow = TimeSpan.FromHours(24);

        public async Task<DeletionRequestDto> FromHolderAsync(Guid? providerId, CancellationToken ct)
        {
            var instance = await instances.EnsureAsync(ct);
            if (!instance.AccountDeletionEnabled)
            {
                throw new ForbiddenActionException(
                    "This instance does not offer self-service account removal",
                    "account.deletion.closed");
            }

            var user = await currentUser.RequireAsync(ct);

            var links = await context.UserIdentities
                .Where(i => i.UserId == user.Id && (providerId == null || i.ProviderId == providerId))
                .ToListAsync(ct);

            if (links.Count == 0)
            {
                // The local channel is `POST /account/delete`, which asks for a
                // password. Sending somebody here who has no link at all would
                // answer a question they did not ask.
                throw new ForbiddenActionException(
                    "This account signs in with a password; use the account deletion form instead",
                    "account.deletion.notFederated");
            }

            var request = new AccountDeletionRequest
            {
                Channel = DeletionChannel.Holder,
                ProviderId = providerId,
                UserId = user.Id,
                RequestedAt = clock.GetUtcNow().UtcDateTime,
                ExecuteAfter = clock.GetUtcNow().UtcDateTime,
            };
            context.AccountDeletionRequests.Add(request);

            // Immediate: the person is here and asked. The window exists for the
            // machine channel, where nobody is.
            await CarryOutAsync(request, links, ct);
            await context.SaveChangesAsync(ct);

            return Project(request);
        }

        public async Task<DeletionRequestDto> FromProviderAsync(
            IdentityProvider provider, ProviderDeletionInputDto input, CancellationToken ct)
        {
            var requestId = (input.RequestId ?? "").Trim();
            var subject = (input.Subject ?? "").Trim();

            if (requestId.Length == 0 || subject.Length == 0)
            {
                throw new ValidationException(
                    "subject and requestId are required", "deletion.request.incomplete");
            }

            // **Idempotent, and answered the same way every time.** A webhook is
            // retried on any hiccup, and a second delivery must not open a second
            // window — nor look like a failure to whatever is retrying.
            var existing = await context.AccountDeletionRequests
                .FirstOrDefaultAsync(r => r.ProviderId == provider.Id && r.RequestId == requestId, ct);
            if (existing is not null) return Project(existing);

            var link = await context.UserIdentities
                .FirstOrDefaultAsync(i => i.ProviderId == provider.Id && i.Subject == subject, ct);

            var now = clock.GetUtcNow().UtcDateTime;
            var request = new AccountDeletionRequest
            {
                Channel = DeletionChannel.Provider,
                ProviderId = provider.Id,
                Subject = subject,
                UserId = link?.UserId,
                RequestId = requestId,
                RequestedAt = ParseRequestedAt(input.RequestedAt) ?? now,
                // The window an administrator gets. Measured from arrival rather
                // than from the timestamp the provider sent: a clock we do not
                // own must not be able to shorten it to nothing.
                ExecuteAfter = now + ProviderWindow,
            };

            if (link is null)
            {
                // Nothing here matched. Recorded rather than refused — a 404
                // would tell a provider whether a given person has an account
                // here, which is not its business to learn by asking.
                request.State = DeletionState.Completed;
                request.ResolvedAt = now;
                request.Detail = "No account is linked to that subject";
            }

            context.AccountDeletionRequests.Add(request);
            await context.SaveChangesAsync(ct);

            return Project(request);
        }

        public async Task<int> SweepAsync(CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            var due = await context.AccountDeletionRequests
                .Where(r => r.State == DeletionState.Pending && r.ExecuteAfter <= now)
                .ToListAsync(ct);

            foreach (var request in due)
            {
                var links = request.UserId is null
                    ? []
                    : await context.UserIdentities
                        .Where(i => i.UserId == request.UserId
                            && (request.ProviderId == null || i.ProviderId == request.ProviderId))
                        .ToListAsync(ct);

                await CarryOutAsync(request, links, ct);
            }

            if (due.Count > 0)
            {
                await context.SaveChangesAsync(ct);
                log.LogInformation("Carried out {Count} account deletion request(s)", due.Count);
            }
            return due.Count;
        }

        /// <summary>
        /// The cascade, in the order it was decided.
        /// <list type="number">
        /// <item>the link goes, and with it that provider's contribution — a
        /// directory that no longer knows somebody must not go on granting them
        /// permissions;</item>
        /// <item>if any other link or a local credential remains, that is the
        /// whole of it: the account survives with one fewer way in;</item>
        /// <item>an account holding <b>system-scope permissions is not emptied
        /// automatically</b> and goes to an administrator instead — a webhook
        /// that can silence an administrator is an attack vector, not a
        /// feature;</item>
        /// <item>otherwise the account is anonymised.</item>
        /// </list>
        /// </summary>
        private async Task CarryOutAsync(
            AccountDeletionRequest request, IReadOnlyList<UserIdentity> links, CancellationToken ct)
        {
            request.ResolvedAt = clock.GetUtcNow().UtcDateTime;

            if (request.UserId is null)
            {
                request.State = DeletionState.Completed;
                return;
            }

            foreach (var link in links)
            {
                context.UserIdentities.Remove(link);

                var contribution = await context.Grants.FirstOrDefaultAsync(
                    g => g.UserId == request.UserId
                        && g.ActivityId == null
                        && g.SourceProviderId == link.ProviderId, ct);
                if (contribution is not null) context.Grants.Remove(contribution);
            }

            var removed = links.Select(l => l.Id).ToHashSet();
            var remaining = await context.UserIdentities
                .CountAsync(i => i.UserId == request.UserId && !removed.Contains(i.Id), ct);

            var user = await context.Users.FirstAsync(u => u.Id == request.UserId, ct);

            if (remaining > 0 || user.PasswordHash is not null)
            {
                request.State = DeletionState.Completed;
                request.Detail = "The link was removed; the account still has another way to sign in";
                return;
            }

            if (await HoldsSystemPermissionsAsync(request.UserId, removed, ct))
            {
                request.State = DeletionState.NeedsAttention;
                request.Detail =
                    "The account holds system-scope permissions and was not emptied. "
                    + "An administrator has to decide.";
                return;
            }

            await AnonymiseAsync(user, ct);
            request.State = DeletionState.Completed;
            request.Detail = "The account was anonymised";
        }

        /// <summary>
        /// Whether anything system-scope survives once the contributions this
        /// request removes are discounted. An empty set does not count: a row
        /// granting nothing is not a reason to stop.
        /// </summary>
        private async Task<bool> HoldsSystemPermissionsAsync(
            string userId, IReadOnlySet<Guid> removedLinks, CancellationToken ct)
        {
            var grants = await context.Grants
                .AsNoTracking()
                .Where(g => g.UserId == userId && g.ActivityId == null)
                .Select(g => new { g.Permissions, g.SourceProviderId })
                .ToListAsync(ct);

            var gone = await context.UserIdentities
                .AsNoTracking()
                .Where(i => removedLinks.Contains(i.Id))
                .Select(i => i.ProviderId)
                .ToListAsync(ct);

            return grants.Any(g =>
                (g.SourceProviderId is null || !gone.Contains(g.SourceProviderId.Value))
                && Parse(g.Permissions).Count > 0);
        }

        private static IReadOnlyList<string> Parse(string json)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }

        public async Task AnonymiseAsync(User user, CancellationToken ct)
        {
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

            // **Every session, not only the open ones, and the address goes with
            // the closure.** This closed what was open and left the addresses
            // behind — so an account that had been "deleted" still said where
            // that person had connected from, on every row they had ever made.
            // An anonymisation that leaves personal data behind is not one.
            //
            // The rows stay, as they do when `AddressSweeper` reaches them: what
            // is deleted is the person, not the record that somebody signed in.
            // **The submissions stay and their origin does not.** A submission
            // survives erasure by design — it is somebody's mark in a contest —
            // but where it was sent from is a fact about the person, not about
            // the work.
            var sent = await context.Submissions
                .Where(s => s.UserId == user.Id && (s.IpAddress != null || s.DeviceId != null))
                .ToListAsync(ct);
            foreach (var submission in sent)
            {
                submission.IpAddress = null;
                submission.DeviceId = null;
            }

            var sessions = await context.UserSessions
                .Where(s => s.UserId == user.Id)
                .ToListAsync(ct);
            foreach (var session in sessions)
            {
                session.EndedAt ??= clock.GetUtcNow().UtcDateTime;
                session.IpAddress = null;
                session.UserAgent = null;
            }
        }

        public async Task<PageDto<DeletionRequestDto>> ListAsync(
            Models.PageQuery paging, string? state, CancellationToken ct)
        {
            // `user:update` rather than a key of its own: this queue exists to be
            // decided about, and deciding about an account is what that permission
            // already means. No shipped template but the administrator's carries
            // it, which is who D-17 sends these to.
            await permissions.RequireAsync(Permissions.UserUpdate, null, ct);

            var query = context.AccountDeletionRequests
                .AsNoTracking()
                .Include(r => r.Provider)
                .Include(r => r.User)
                .AsQueryable();

            query = state switch
            {
                "pending" => query.Where(r => r.State == DeletionState.Pending),
                "attention" => query.Where(r => r.State == DeletionState.NeedsAttention),
                "open" => query.Where(r =>
                    r.State == DeletionState.Pending || r.State == DeletionState.NeedsAttention),
                _ => query,
            };

            var total = await query.CountAsync(ct);
            var page = await query
                .OrderByDescending(r => r.RequestedAt).ThenBy(r => r.Id)
                .Skip(paging.Skip).Take(paging.PageSize)
                .ToListAsync(ct);

            return new PageDto<DeletionRequestDto>
            {
                Items = page.Select(Project).ToList(),
                Total = total,
                Page = paging.Page,
                PageSize = paging.PageSize,
            };
        }

        public async Task<DeletionRequestDto> HaltAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.UserUpdate, null, ct);
            var actor = await currentUser.RequireAsync(ct);

            var request = await context.AccountDeletionRequests.FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new NotFoundException("Deletion request");

            if (request.State != DeletionState.Pending)
            {
                // A window that has closed cannot be reopened: what it was
                // holding has already happened, and saying otherwise would offer
                // an undo that does not exist.
                throw new ConflictException(
                    "This request is no longer waiting and cannot be stopped", "deletion.notPending");
            }

            request.State = DeletionState.Halted;
            request.HaltedByUserId = actor.Id;
            request.ResolvedAt = clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(ct);

            return Project(request);
        }

        private static DeletionRequestDto Project(AccountDeletionRequest r) => new()
        {
            Id = Wire.Id(r.Id),
            Channel = r.Channel == DeletionChannel.Provider ? "provider" : "holder",
            State = r.State switch
            {
                DeletionState.Completed => "completed",
                DeletionState.Halted => "halted",
                DeletionState.NeedsAttention => "attention",
                _ => "pending",
            },
            ProviderId = r.ProviderId is { } p ? Wire.Id(p) : null,
            ProviderName = r.Provider?.DisplayName,
            UserId = r.UserId,
            UserLogin = r.User?.UserName,
            RequestedAt = Wire.At(r.RequestedAt),
            ExecuteAfter = Wire.At(r.ExecuteAfter),
            ResolvedAt = Wire.At(r.ResolvedAt),
            Detail = r.Detail,
        };

        private static DateTime? ParseRequestedAt(string? raw) =>
            DateTimeOffset.TryParse(raw, out var parsed) ? parsed.UtcDateTime : null;

        /// <summary>
        /// Compares a presented secret with the stored one in constant time.
        /// <para>
        /// Not because a timing attack on this is likely, but because the cheap
        /// version of this comparison is <c>==</c>, and the difference between
        /// them is one method call.
        /// </para>
        /// </summary>
        public static bool SecretMatches(string? presented, string? stored)
        {
            if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(stored)) return false;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(stored));
        }
    }
}

using System.Security.Claims;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <param name="Admitted">Whether this sign-in may proceed.</param>
    /// <param name="User">The account, when there is one. Null on a refusal that created none.</param>
    /// <param name="Reason">A code a screen can act on: `provider.disabled`, `provider.unmapped`, …</param>
    public record FederatedSignIn(bool Admitted, User? User, string? Reason);

    public interface IFederatedSignInService
    {
        Task<FederatedSignIn> CompleteAsync(
            Guid providerId, ClaimsPrincipal principal, CancellationToken ct);
    }

    /// <summary>
    /// What happens when a token comes back from a provider.
    /// <para>
    /// <b>The order is the decision, and it is the part most likely to be built
    /// backwards.</b> Validate, resolve the mapping, <i>apply the permission
    /// change</i>, and only then decide whether to admit. A sign-in that is going
    /// to be refused still has to withdraw what the provider no longer grants —
    /// because the refusal means "this directory no longer says you are staff",
    /// and leaving yesterday's contribution in place would keep them staff for
    /// ever by the simple method of never signing in again.
    /// </para>
    /// <para>
    /// Every path through this is idempotent: the same token twice leaves the
    /// same rows. Nothing here increments, appends to a set, or assumes it is
    /// the first to run.
    /// </para>
    /// </summary>
    public class FederatedSignInService(
        ApplicationDbContext context,
        IClaimMappingService mapping,
        UserManager<User> users,
        TimeProvider clock,
        ILogger<FederatedSignInService> log
    ) : IFederatedSignInService
    {
        public async Task<FederatedSignIn> CompleteAsync(
            Guid providerId, ClaimsPrincipal principal, CancellationToken ct)
        {
            var provider = await context.IdentityProviders
                .Include(p => p.MappingRules)
                .FirstOrDefaultAsync(p => p.Id == providerId, ct);

            if (provider is null || !provider.Enabled)
            {
                // Nothing is written: a disabled provider is not a statement
                // about anybody's permissions, it is a statement about the
                // registration. Turning one off to reconfigure it must not
                // silently demote everybody who ever signed in through it.
                return new FederatedSignIn(false, null, "provider.disabled");
            }

            var subject = Subject(principal);
            if (string.IsNullOrEmpty(subject))
            {
                return new FederatedSignIn(false, null, "provider.subject.missing");
            }

            var mapped = await mapping.ResolveAsync(provider, principal, ct);

            var link = await context.UserIdentities
                .Include(i => i.User)
                .FirstOrDefaultAsync(i => i.ProviderId == provider.Id && i.Subject == subject, ct);

            return link is null
                ? await FirstSignInAsync(provider, principal, subject, mapped, ct)
                : await ReturningAsync(provider, link, mapped, ct);
        }

        /// <summary>
        /// A subject this Server has seen before.
        /// <para>
        /// The permission change is applied whichever way this ends, which is the
        /// whole of D-33.
        /// </para>
        /// </summary>
        private async Task<FederatedSignIn> ReturningAsync(
            IdentityProvider provider, UserIdentity link, MappedContribution mapped, CancellationToken ct)
        {
            var user = link.User ?? await context.Users.FirstAsync(u => u.Id == link.UserId, ct);

            bool changed;
            FederatedSignIn answer;

            if (mapped.Any)
            {
                changed = await WriteContributionAsync(provider, link.UserId, mapped.Permissions, ct);
                answer = new FederatedSignIn(true, user, null);
            }
            else if (provider.UnmappedBehavior == UnmappedBehavior.DefaultTemplate)
            {
                var fallback = await DefaultPermissionsAsync(provider, ct);
                changed = await WriteContributionAsync(provider, link.UserId, fallback, ct);
                answer = new FederatedSignIn(true, user, null);
            }
            else
            {
                // **Withdrawn, and then refused.** The sign-in fails and the state
                // still moves; that is deliberate and is why this is logged.
                changed = await WithdrawContributionAsync(provider, link.UserId, ct);
                answer = new FederatedSignIn(false, user, "provider.unmapped");
            }

            if (answer.Admitted)
            {
                link.LastSignInAt = clock.GetUtcNow().UtcDateTime;
            }

            await context.SaveChangesAsync(ct);
            await RecordAsync(provider, link.Subject, link.UserId, mapped,
                answer.Admitted ? FederatedSignInOutcome.Admitted : FederatedSignInOutcome.Refused,
                changed, answer.Reason, ct);

            return answer;
        }

        /// <summary>
        /// A subject nobody here has seen.
        /// <para>
        /// Under <c>deny</c> with nothing matched, <b>no account is created</b> —
        /// there is nothing to withdraw on a first sign-in, and provisioning
        /// somebody the mapping refuses would leave an account that can never be
        /// used and that an administrator has to explain.
        /// </para>
        /// </summary>
        private async Task<FederatedSignIn> FirstSignInAsync(
            IdentityProvider provider,
            ClaimsPrincipal principal,
            string subject,
            MappedContribution mapped,
            CancellationToken ct)
        {
            var permissions = mapped.Any
                ? mapped.Permissions
                : provider.UnmappedBehavior == UnmappedBehavior.DefaultTemplate
                    ? await DefaultPermissionsAsync(provider, ct)
                    : null;

            if (permissions is null)
            {
                await RecordAsync(provider, subject, null, mapped,
                    FederatedSignInOutcome.Refused, false, "provider.unmapped", ct);
                return new FederatedSignIn(false, null, "provider.unmapped");
            }

            var user = await ProvisionAsync(provider, principal, subject, ct);

            context.UserIdentities.Add(new UserIdentity
            {
                UserId = user.Id,
                ProviderId = provider.Id,
                Subject = subject,
                LastSignInAt = clock.GetUtcNow().UtcDateTime,
            });

            await WriteContributionAsync(provider, user.Id, permissions, ct);
            await context.SaveChangesAsync(ct);

            await RecordAsync(provider, subject, user.Id, mapped,
                FederatedSignInOutcome.Provisioned, true, null, ct);

            return new FederatedSignIn(true, user, null);
        }

        /// <summary>
        /// Creates the account a provider vouched for.
        /// <para>
        /// <b>It never attaches to an existing account by name or by address.</b>
        /// That is the whole reason the federated key is issuer plus <c>sub</c>:
        /// a provider that could hand us a `preferred_username` matching somebody
        /// else's login would otherwise be handing itself that person's account.
        /// A taken login is decorated until it is free.
        /// </para>
        /// <para>
        /// It carries no password, which is what makes it not a local account —
        /// so the profile is read-only here and belongs to the provider.
        /// </para>
        /// </summary>
        private async Task<User> ProvisionAsync(
            IdentityProvider provider, ClaimsPrincipal principal, string subject, CancellationToken ct)
        {
            var wanted = Sanitise(
                First(principal, "preferred_username")
                ?? First(principal, ClaimTypes.Name)
                ?? First(principal, "email")
                ?? $"{provider.Slug}-{subject}");

            var login = await FreeLoginAsync(wanted, subject, ct);

            var user = new User
            {
                UserName = login,
                Email = First(principal, "email") ?? First(principal, ClaimTypes.Email),
                EmailConfirmed = string.Equals(
                    First(principal, "email_verified"), "true", StringComparison.OrdinalIgnoreCase),
                FirstName = First(principal, "given_name") ?? First(principal, ClaimTypes.GivenName),
                LastName = First(principal, "family_name") ?? First(principal, ClaimTypes.Surname),
                // Approved on arrival, because the provider is the decision. An
                // account that a trusted directory created and that then sat
                // `pending` would need an approval nobody was told to make, which
                // turns a launch gate into a support queue.
                ApprovedAt = clock.GetUtcNow().UtcDateTime,
            };

            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                // Not a validation failure: the caller sent nothing. Either the
                // provider emitted something this product cannot store, or the
                // login race below lost — both are ours to fix, not theirs.
                throw new InvalidOperationException(
                    "Could not create the account this provider vouched for: "
                        + string.Join("; ", created.Errors.Select(e => e.Description)));
            }

            log.LogInformation(
                "Provisioned {Login} from provider {Provider}", user.UserName, provider.Slug);
            return user;
        }

        private async Task<string> FreeLoginAsync(string wanted, string subject, CancellationToken ct)
        {
            if (!await context.Users.AnyAsync(u => u.NormalizedUserName == wanted.ToUpperInvariant(), ct)
                && !string.Equals(wanted, Seeder.AdminLogin, StringComparison.OrdinalIgnoreCase))
            {
                return wanted;
            }

            // Deterministic rather than a counter: the same subject arriving
            // twice, in a race, lands on the same candidate and one of the two
            // insertions loses on the unique index instead of both succeeding.
            var suffix = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(subject)))[..6].ToLowerInvariant();

            return $"{wanted}-{suffix}";
        }

        /// <summary>
        /// Writes this provider's contribution, replacing whatever it said before.
        /// Answers whether anything actually moved, for the log.
        /// </summary>
        private async Task<bool> WriteContributionAsync(
            IdentityProvider provider, string userId, IReadOnlySet<string> permissions, CancellationToken ct)
        {
            var ordered = permissions.OrderBy(p => p, StringComparer.Ordinal).ToList();
            var json = JsonSerializer.Serialize(ordered);

            var grant = await context.Grants.FirstOrDefaultAsync(
                g => g.UserId == userId && g.ActivityId == null && g.SourceProviderId == provider.Id, ct);

            if (grant is null)
            {
                context.Grants.Add(new Grant
                {
                    UserId = userId,
                    SourceProviderId = provider.Id,
                    Permissions = json,
                    IsSystem = Authorization.Permissions.IsStaff(ordered),
                    CreatedFromTemplate = null,
                });
                return true;
            }

            if (grant.Permissions == json) return false;

            grant.Permissions = json;
            grant.IsSystem = Authorization.Permissions.IsStaff(ordered);
            return true;
        }

        private async Task<bool> WithdrawContributionAsync(
            IdentityProvider provider, string userId, CancellationToken ct)
        {
            var grant = await context.Grants.FirstOrDefaultAsync(
                g => g.UserId == userId && g.ActivityId == null && g.SourceProviderId == provider.Id, ct);

            if (grant is null) return false;

            context.Grants.Remove(grant);
            return true;
        }

        private async Task<IReadOnlySet<string>> DefaultPermissionsAsync(
            IdentityProvider provider, CancellationToken ct)
        {
            if (provider.DefaultTemplateName is null) return new HashSet<string>();

            var template = await context.PermissionTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Name == provider.DefaultTemplateName, ct);

            if (template is null) return new HashSet<string>();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in Deserialize(template.Permissions))
            {
                // The same two exclusions the mapping applies. A default template
                // is a mapping with no claim in front of it, and "unreachable
                // through a mapping" would be a strange thing to enforce on one
                // path and not the other.
                if (key == Authorization.Permissions.SystemAdministrator) continue;
                if (Authorization.Permissions.Unknown([key]).Count > 0) continue;
                keys.Add(key);
            }
            return keys;
        }

        private async Task RecordAsync(
            IdentityProvider provider,
            string subject,
            string? userId,
            MappedContribution mapped,
            FederatedSignInOutcome outcome,
            bool changed,
            string? detail,
            CancellationToken ct)
        {
            context.FederatedSignInAttempts.Add(new FederatedSignInAttempt
            {
                ProviderId = provider.Id,
                Subject = subject,
                UserId = userId,
                Outcome = outcome,
                ChangedPermissions = changed,
                Matched = JsonSerializer.Serialize(mapped.Matched),
                Detail = detail,
                At = clock.GetUtcNow().UtcDateTime,
            });
            await context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// The provider's own identifier for this person. <c>sub</c> first,
        /// because that is what OIDC promises is stable; the framework's
        /// <c>NameIdentifier</c> is where the handler puts it after mapping.
        /// </summary>
        private static string? Subject(ClaimsPrincipal principal) =>
            First(principal, "sub") ?? First(principal, ClaimTypes.NameIdentifier);

        private static string? First(ClaimsPrincipal principal, string type)
        {
            var value = principal.FindFirst(type)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>
        /// A login this product will accept: the password policy is length-only,
        /// but Identity still refuses characters outside its allowed set, and a
        /// provider may emit an address or a display name with spaces in it.
        /// </summary>
        private static string Sanitise(string raw)
        {
            var cleaned = new string([.. raw.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_' or '@' ? c : '-')]);

            return cleaned.Length is > 0 and <= 64 ? cleaned : cleaned[..Math.Min(64, cleaned.Length)];
        }

        private static IReadOnlyList<string> Deserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}

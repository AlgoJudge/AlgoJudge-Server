using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Lti.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>What a launch resolved to, and why it did not.</summary>
    public abstract record Resolution
    {
        /// <summary>An account. The launch may proceed.</summary>
        public sealed record Resolved(User User, ExternalIdentity Link) : Resolution;

        /// <summary>
        /// Nobody, and the way forward is for the person to sign in through SSO
        /// and come back — which writes the same link by a route the platform
        /// cannot forge (§4.4).
        /// </summary>
        public sealed record NeedsSignIn(string Reason) : Resolution;

        /// <summary>
        /// A link exists and the platform now asserts a different person for it.
        /// <b>Reported, never followed.</b>
        /// </summary>
        public sealed record Conflict(string Stored, string Asserted) : Resolution;
    }

    public interface IIdentityResolver
    {
        Task<Resolution> ResolveAsync(LaunchedMessage launch, CancellationToken ct);

        /// <summary>
        /// The account a platform may claim under this username, or null.
        ///
        /// <para>
        /// <b>The same safeguard a launch goes through, and deliberately the same
        /// code.</b> A roster reaches this too, and a second implementation of
        /// "may this platform have that account" is how the two drift until one
        /// of them is wrong.
        /// </para>
        ///
        /// <para>
        /// <b>It never creates anybody.</b> A launch may, because a person is
        /// standing there having just authenticated at the platform; a roster is
        /// a list read on somebody else's behalf, and creating accounts from it
        /// would invent an account for every name in a course that happens to
        /// look like one of ours.
        /// </para>
        /// </summary>
        Task<User?> MatchAsync(Platform platform, string username, CancellationToken ct);
    }

    /// <summary>
    /// Turns "who launched" into an AlgoJudge account, or into a reason it could
    /// not.
    /// <para>
    /// <b>An LTI <c>sub</c> is opaque and scoped to its platform</b> — not
    /// comparable with an OIDC <c>sub</c> from anywhere else, even for the same
    /// human — so the link is stored once and read afterwards, never recomputed
    /// (§4.2). What makes the first one possible is the username the platform
    /// asserts.
    /// </para>
    /// </summary>
    public class IdentityResolver(
        LtiDbContext db,
        ApplicationDbContext core,
        UserManager<User> users,
        TimeProvider clock
    ) : IIdentityResolver
    {
        public async Task<Resolution> ResolveAsync(LaunchedMessage launch, CancellationToken ct)
        {
            var platform = launch.Platform;

            var existing = await db.ExternalIdentities
                .FirstOrDefaultAsync(i => i.PlatformId == platform.Id && i.Subject == launch.Subject, ct);

            if (existing is not null)
            {
                // **A changed assertion is a conflict to report, not a link to
                // move** (§4.3). Following it would hand one person's history to
                // another because somebody edited a field in Moodle — and the
                // person losing their work would be the one who noticed.
                if (launch.AssertedUsername is { } asserted
                    && existing.AssertedUsername is { } stored
                    && !string.Equals(stored, asserted, StringComparison.OrdinalIgnoreCase))
                {
                    return new Resolution.Conflict(stored, asserted);
                }

                var user = await core.Users.FirstOrDefaultAsync(u => u.Id == existing.UserId, ct);
                if (user is null)
                {
                    // The account went; the link outlived it. Nothing is repaired
                    // here — the person signs in and a new link is written.
                    return new Resolution.NeedsSignIn("the linked account no longer exists");
                }

                existing.LastLaunchAt = clock.GetUtcNow().UtcDateTime;

                // **A launch is the witness a roster never was** (§4.4). A
                // provisional link was inferred from a list somebody else read;
                // this person has now authenticated at the platform and arrived
                // under the same subject, which is the evidence the link was
                // waiting for. It is raised once and never lowered.
                if (existing.Strength == LinkStrength.Provisional)
                {
                    existing.Strength = LinkStrength.Confirmed;
                }

                await db.SaveChangesAsync(ct);
                return new Resolution.Resolved(user, existing);
            }

            // ── No link yet. Everything below is §4.5's dangerous half. ───────

            if (!platform.IsIdentityAuthority)
            {
                return new Resolution.NeedsSignIn("this platform may not assert who somebody is");
            }
            if (string.IsNullOrWhiteSpace(launch.AssertedUsername))
            {
                return new Resolution.NeedsSignIn("the launch carried no username");
            }
            if (string.IsNullOrWhiteSpace(platform.IdentityNamespace))
            {
                // Refused rather than treated as "anywhere". Registration already
                // refuses this combination; this is the second lock, because the
                // first one is a validation and validations get relaxed.
                return new Resolution.NeedsSignIn("this platform names no namespace to assert within");
            }

            var username = launch.AssertedUsername.Trim();
            var candidate = await users.FindByNameAsync(username);

            if (candidate is null)
            {
                var settings = await db.Settings.FirstOrDefaultAsync(ct);
                if (settings?.AccountCreationEnabled != true)
                {
                    return new Resolution.NeedsSignIn("no account carries that username");
                }

                candidate = await CreateAsync(username, ct);
                if (candidate is null)
                {
                    return new Resolution.NeedsSignIn("the account could not be created");
                }
            }
            else if (!await BelongsToNamespaceAsync(candidate, platform.IdentityNamespace, ct))
            {
                // **The namespace is the whole safeguard**, decided 2026-08-13:
                // it names an `IdentityProvider` by slug, and a platform may only
                // claim accounts that already hold a link from that provider.
                //
                // §4.5's own sentence is what this implements — the platform is
                // trusted to make claims *inside the identity provider's
                // namespace* — and the consequence is the point: a compromised
                // Moodle cannot assert its way onto an administrator's account,
                // or onto anything registered locally, because neither carries a
                // link from the directory it is trusted for.
                return new Resolution.NeedsSignIn(
                    "that account does not belong to the directory this platform may assert for");
            }

            var link = new ExternalIdentity
            {
                PlatformId = platform.Id,
                Subject = launch.Subject,
                UserId = candidate.Id,
                Strength = LinkStrength.Confirmed,
                AssertedUsername = username,
                LastLaunchAt = clock.GetUtcNow().UtcDateTime,
            };
            db.ExternalIdentities.Add(link);
            await db.SaveChangesAsync(ct);

            return new Resolution.Resolved(candidate, link);
        }

        public async Task<User?> MatchAsync(
            Platform platform, string username, CancellationToken ct)
        {
            if (!platform.IsIdentityAuthority) return null;
            if (string.IsNullOrWhiteSpace(platform.IdentityNamespace)) return null;
            if (string.IsNullOrWhiteSpace(username)) return null;

            var candidate = await users.FindByNameAsync(username.Trim());
            if (candidate is null) return null;

            return await BelongsToNamespaceAsync(candidate, platform.IdentityNamespace, ct)
                ? candidate
                : null;
        }

        /// <summary>
        /// Whether this account came through the directory the platform is
        /// trusted for. A local account, an administrator, or somebody who
        /// registered at <c>auth.algojudge.app</c> answers no.
        /// </summary>
        private async Task<bool> BelongsToNamespaceAsync(
            User user, string providerSlug, CancellationToken ct)
        {
            var provider = await core.IdentityProviders
                .FirstOrDefaultAsync(p => p.Slug == providerSlug, ct);

            return provider is not null
                && await core.UserIdentities.AnyAsync(
                    i => i.UserId == user.Id && i.ProviderId == provider.Id, ct);
        }

        /// <summary>
        /// Creates the account a launch asserted, where the installation allows
        /// it. Off by default and off in every deployment that has not chosen
        /// otherwise (§4.6).
        /// <para>
        /// <b>The namespace cannot protect this path, and that is worth saying
        /// out loud.</b> An account that does not exist belongs to no directory,
        /// so there is nothing to check it against: with this on, the platform's
        /// assertion is the only evidence there is. Two consequences follow, and
        /// neither is hypothetical.
        /// </para>
        /// <para>
        /// A compromised platform can mint accounts for names nobody holds. They
        /// carry no permissions and no grants beyond the course they launched
        /// into, so this is closer to noise than to takeover.
        /// </para>
        /// <para>
        /// The sharper one: <b>an account minted here occupies its username</b>.
        /// When the real person later signs in through SSO, the name they were
        /// provisioned with is already taken — by a row this module wrote on a
        /// platform's word. That is why the setting ships off, and why turning it
        /// on is a decision about trusting one LMS with the directory's
        /// namespace.
        /// </para>
        /// </summary>
        private async Task<User?> CreateAsync(string username, CancellationToken ct)
        {
            var user = new User { UserName = username };
            var created = await users.CreateAsync(user);
            return created.Succeeded ? user : null;
        }
    }
}

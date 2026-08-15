using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    public interface IPlatformService
    {
        Task<IReadOnlyList<Platform>> ListAsync(CancellationToken ct);
        Task<Platform> GetAsync(Guid id, CancellationToken ct);
        Task<Platform> RegisterAsync(PlatformInput input, CancellationToken ct);

        /// <summary>
        /// Registers a platform that arrived through an invitation, rather than
        /// through somebody signed in here.
        ///
        /// <para>
        /// <b>The permission was checked when the invitation was created</b>, by
        /// the manager who created it; the browser completing the registration
        /// belongs to the platform's administrator and has no account with us at
        /// all. Asking for <c>provider:manage</c> again would ask it of the wrong
        /// person and refuse every dynamic registration — which is exactly what
        /// it did, until a test said so.
        /// </para>
        ///
        /// <para>
        /// It is a separate method rather than a flag so that the one call which
        /// skips the check is greppable, and so that the ordinary path cannot
        /// lose its check by somebody passing <c>false</c>.
        /// </para>
        /// </summary>
        Task<Platform> RegisterInvitedAsync(PlatformInput input, CancellationToken ct);
        Task<Platform> UpdateAsync(Guid id, PlatformInput input, CancellationToken ct);
        Task DeleteAsync(Guid id, CancellationToken ct);
    }

    /// <summary>What an operator types in to register a platform by hand.</summary>
    public record PlatformInput
    {
        public required string DisplayName { get; init; }
        public required string Issuer { get; init; }
        public required string ClientId { get; init; }
        public required string DeploymentId { get; init; }
        public required string KeySetUrl { get; init; }
        public required string AuthTokenUrl { get; init; }
        public required string AuthLoginUrl { get; init; }
        public bool IsIdentityAuthority { get; init; }
        public string? IdentityNamespace { get; init; }
        public string? UsernameClaim { get; init; }
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    /// Registering a platform, which is <b>two rows written together</b>.
    /// <para>
    /// The <see cref="Platform"/> holds what LTI needs. An
    /// <see cref="IdentityProvider"/> holds what the rest of the Server already
    /// knows how to do with a provider — chiefly being nameable by
    /// <c>Grant.SourceProviderId</c>, so a course grant produced by a launch has
    /// a source instead of looking like somebody typed it in.
    /// </para>
    /// <para>
    /// <b>That row is created disabled, and it has to be.</b> Disabled keeps it
    /// out of <c>ProviderRegistry</c> entirely — no authentication scheme is
    /// registered for it, so no callback path exists — and
    /// <c>FederatedSignInService</c> refuses it besides. Nobody signs in through
    /// a platform; a platform launches into an account that already exists (§4.1).
    /// A row that could do both would be a second way into every account, sitting
    /// on the sign-in screen with no client secret behind it.
    /// </para>
    /// <para>
    /// Written through <c>ApplicationDbContext</c> rather than through
    /// <c>IIdentityProviderService</c>, and the reason is honesty rather than
    /// convenience: that service requires a client secret, correctly, because a
    /// provider without one cannot complete a code exchange. LTI has no client
    /// secret — it authenticates with a signed assertion — so going through it
    /// would mean inventing a secret that authenticates nothing and showing
    /// <c>hasClientSecret: true</c> in a panel. The permission that governs
    /// provider rows is still required here; what is skipped is a validation that
    /// does not describe this case.
    /// </para>
    /// </summary>
    public class PlatformService(
        LtiDbContext db,
        ApplicationDbContext core,
        IPermissionService permissions
    ) : IPlatformService
    {
        public async Task<IReadOnlyList<Platform>> ListAsync(CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);
            return await db.Platforms.AsNoTracking().OrderBy(p => p.DisplayName).ToListAsync(ct);
        }

        public async Task<Platform> GetAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);
            return await db.Platforms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw new NotFoundException("Platform");
        }

        public async Task<Platform> RegisterAsync(PlatformInput input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);
            return await WriteAsync(input, ct);
        }

        public Task<Platform> RegisterInvitedAsync(PlatformInput input, CancellationToken ct) =>
            WriteAsync(input, ct);

        private async Task<Platform> WriteAsync(PlatformInput input, CancellationToken ct)
        {
            Validate(input);

            var issuer = input.Issuer.Trim();
            var clientId = input.ClientId.Trim();
            var deploymentId = input.DeploymentId.Trim();

            if (await db.Platforms.AnyAsync(
                p => p.Issuer == issuer && p.ClientId == clientId && p.DeploymentId == deploymentId, ct))
            {
                throw new ConflictException(
                    "That deployment of that client is already registered", "lti.platform.duplicate");
            }

            var provider = new IdentityProvider
            {
                Slug = await FreeSlugAsync(input.DisplayName, ct),
                DisplayName = input.DisplayName.Trim(),
                Issuer = issuer,
                ClientId = clientId,
                // No secret exists, and none is invented. Empty rather than
                // random: `HasClientSecret` then answers "no", which is true.
                ClientSecret = "",
                // See the class summary. This is the guard, not a default.
                Enabled = false,
            };
            core.IdentityProviders.Add(provider);
            await core.SaveChangesAsync(ct);

            var platform = new Platform
            {
                ProviderId = provider.Id,
                DisplayName = input.DisplayName.Trim(),
                Issuer = issuer,
                ClientId = clientId,
                DeploymentId = deploymentId,
                KeySetUrl = input.KeySetUrl.Trim(),
                AuthTokenUrl = input.AuthTokenUrl.Trim(),
                AuthLoginUrl = input.AuthLoginUrl.Trim(),
                IsIdentityAuthority = input.IsIdentityAuthority,
                IdentityNamespace = Normalise(input.IdentityNamespace),
                UsernameClaim = string.IsNullOrWhiteSpace(input.UsernameClaim)
                    ? "username"
                    : input.UsernameClaim.Trim(),
                Enabled = input.Enabled,
            };

            db.Platforms.Add(platform);
            await db.SaveChangesAsync(ct);
            return platform;
        }

        public async Task<Platform> UpdateAsync(Guid id, PlatformInput input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);
            Validate(input);

            var platform = await db.Platforms.FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw new NotFoundException("Platform");

            // **The issuer, client and deployment are not editable.** They are
            // the key an `id_token` is matched against and the key every
            // `ExternalIdentity` hangs off; changing one repoints every person
            // this platform ever launched, silently. Registering a second
            // platform is the honest way to say what that would mean.
            if (!string.Equals(platform.Issuer, input.Issuer.Trim(), StringComparison.Ordinal)
                || !string.Equals(platform.ClientId, input.ClientId.Trim(), StringComparison.Ordinal)
                || !string.Equals(platform.DeploymentId, input.DeploymentId.Trim(), StringComparison.Ordinal))
            {
                throw new ValidationException(
                    "The issuer, client id and deployment id cannot be changed. Register another platform instead",
                    "lti.platform.immutable");
            }

            platform.DisplayName = input.DisplayName.Trim();
            platform.KeySetUrl = input.KeySetUrl.Trim();
            platform.AuthTokenUrl = input.AuthTokenUrl.Trim();
            platform.AuthLoginUrl = input.AuthLoginUrl.Trim();
            platform.IsIdentityAuthority = input.IsIdentityAuthority;
            platform.IdentityNamespace = Normalise(input.IdentityNamespace);
            platform.UsernameClaim = string.IsNullOrWhiteSpace(input.UsernameClaim)
                ? "username"
                : input.UsernameClaim.Trim();
            platform.Enabled = input.Enabled;

            await db.SaveChangesAsync(ct);

            var provider = await core.IdentityProviders.FirstOrDefaultAsync(p => p.Id == platform.ProviderId, ct);
            if (provider is not null)
            {
                provider.DisplayName = platform.DisplayName;
                await core.SaveChangesAsync(ct);
            }

            return platform;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var platform = await db.Platforms.FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw new NotFoundException("Platform");

            // Refused while anything hangs off it, the same way a provider with
            // people behind it is refused. Removing a platform that has launched
            // takes a course's membership with it, and that is not a side effect
            // a delete button should have.
            var links = await db.ResourceLinks.CountAsync(l => l.PlatformId == id, ct);
            if (links > 0)
            {
                throw new ConflictException(
                    $"{links} activity placement(s) come from this platform. Disable it instead",
                    "lti.platform.inUse");
            }

            db.Platforms.Remove(platform);
            await db.SaveChangesAsync(ct);

            var provider = await core.IdentityProviders.FirstOrDefaultAsync(p => p.Id == platform.ProviderId, ct);
            if (provider is not null)
            {
                // The core refuses this itself if the row still sources grants,
                // with an answer naming how many. Nothing here second-guesses it.
                core.IdentityProviders.Remove(provider);
                await core.SaveChangesAsync(ct);
            }
        }

        private static string? Normalise(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

        private static void Validate(PlatformInput input)
        {
            foreach (var (value, name) in new[]
            {
                (input.Issuer, "issuer"), (input.ClientId, "clientId"),
                (input.DeploymentId, "deploymentId"), (input.KeySetUrl, "keySetUrl"),
                (input.AuthTokenUrl, "authTokenUrl"), (input.AuthLoginUrl, "authLoginUrl"),
                (input.DisplayName, "displayName"),
            })
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ValidationException($"{name} is required", $"lti.platform.{name}.required");
                }
            }

            foreach (var (url, name) in new[]
            {
                (input.KeySetUrl, "keySetUrl"), (input.AuthTokenUrl, "authTokenUrl"),
                (input.AuthLoginUrl, "authLoginUrl"),
            })
            {
                if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
                    || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
                {
                    throw new ValidationException(
                        $"{name} must be an absolute http or https URL", $"lti.platform.{name}.invalid");
                }
            }

            // **Authority over everything is not a namespace** (§4.5). The flag
            // means "this platform may say who somebody is, inside this suffix";
            // without the suffix it means the platform may claim any account in
            // the installation, which is the account-takeover shape the whole
            // section exists to bound.
            if (input.IsIdentityAuthority && string.IsNullOrWhiteSpace(input.IdentityNamespace))
            {
                throw new ValidationException(
                    "A platform trusted to assert identity must name the namespace it is trusted within",
                    "lti.platform.identityNamespace.required");
            }
        }

        /// <summary>
        /// A slug for the paired provider row. It never appears in a sign-in path
        /// — the row is disabled — but the column is unique and the panel shows
        /// it, so it should read as what it is.
        /// </summary>
        private async Task<string> FreeSlugAsync(string displayName, CancellationToken ct)
        {
            var basis = new string(displayName.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
                .Trim('-');
            if (basis.Length == 0)
            {
                basis = "platform";
            }

            var candidate = ("lti-" + basis);
            if (candidate.Length > 32)
            {
                candidate = candidate[..32].TrimEnd('-');
            }

            var slug = candidate;
            var suffix = 2;
            while (await core.IdentityProviders.AnyAsync(p => p.Slug == slug, ct))
            {
                var tail = "-" + suffix++;
                slug = candidate.Length + tail.Length > 32
                    ? candidate[..(32 - tail.Length)].TrimEnd('-') + tail
                    : candidate + tail;
            }
            return slug;
        }
    }
}

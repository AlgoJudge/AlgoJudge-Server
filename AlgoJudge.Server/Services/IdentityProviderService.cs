using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    public interface IIdentityProviderService
    {
        Task<IReadOnlyList<IdentityProviderDto>> ListAsync(CancellationToken ct);
        Task<IdentityProviderDto> GetAsync(Guid id, CancellationToken ct);
        Task<IdentityProviderDto> CreateAsync(IdentityProviderInputDto input, CancellationToken ct);
        Task<IdentityProviderDto> UpdateAsync(Guid id, IdentityProviderInputDto input, CancellationToken ct);
        Task DeleteAsync(Guid id, CancellationToken ct);
    }

    /// <summary>
    /// Registering identity providers, and the guards that keep a claim from
    /// minting privilege.
    /// <para>
    /// Everything here is behind <c>provider:manage</c>, which is the second most
    /// dangerous permission the product has. The mapping an operator writes
    /// decides what an external directory's groups buy inside this installation,
    /// so two rules are enforced on every write rather than described in a
    /// document:
    /// </para>
    /// <list type="number">
    /// <item><c>system:administrator</c> is unreachable through a mapping, in
    /// every configuration.</item>
    /// <item>Nobody may map onto a permission they do not themselves hold — the
    /// same rule that already governs writing a grant, applied to the path a
    /// claim takes.</item>
    /// </list>
    /// </summary>
    public partial class IdentityProviderService(
        ApplicationDbContext context,
        IPermissionService permissions,
        IProviderMappingService mapping,
        IProviderRegistry registry
    ) : IIdentityProviderService
    {
        [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,30}[a-z0-9]$")]
        private static partial Regex SlugPattern();

        public async Task<IReadOnlyList<IdentityProviderDto>> ListAsync(CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var providers = await Loaded().OrderBy(p => p.DisplayName).ToListAsync(ct);
            var counts = await CountsAsync(ct);
            return providers.Select(p => Project(p, counts)).ToList();
        }

        public async Task<IdentityProviderDto> GetAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var provider = await Loaded().FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw new NotFoundException("Identity provider");
            return Project(provider, await CountsAsync(ct));
        }

        /// <summary>
        /// The providers this screen owns: the doors people sign in through.
        /// <para>
        /// An LTI platform carries a provider row so a grant's roles can say
        /// where they came from, and it is not one of these. Listed as one it
        /// came with an enable switch and a delete button, and deleting it broke
        /// every launch from that course while looking like tidying up.
        /// </para>
        /// </summary>
        private IQueryable<IdentityProvider> Loaded() =>
            context.IdentityProviders
                .AsNoTracking()
                .Where(p => p.Kind == ProviderKind.SignIn)
                .Include(p => p.MappingRules).ThenInclude(r => r.Role)
                .Include(p => p.DefaultRoles);

        private async Task<Dictionary<Guid, int>> CountsAsync(CancellationToken ct) =>
            await context.UserIdentities
                .GroupBy(i => i.ProviderId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        /// <summary>
        /// The wire shape. <b>There is no branch here that could emit a
        /// secret</b>, because <see cref="IdentityProviderDto"/> has no field for
        /// one — the type is the enforcement, not this method's discipline.
        /// </summary>
        private IdentityProviderDto Project(IdentityProvider p, Dictionary<Guid, int> counts) => new()
        {
            Id = Wire.Id(p.Id),
            Slug = p.Slug,
            DisplayName = p.DisplayName,
            Issuer = p.Issuer,
            ClientId = p.ClientId,
            Scopes = p.Scopes,
            Enabled = p.Enabled,
            AccountUrl = p.AccountUrl,
            DeletionUrl = p.DeletionUrl,
            ClaimPath = p.ClaimPath,
            UnmappedBehavior = p.UnmappedBehavior == UnmappedBehavior.DefaultRole
                ? "defaultRole"
                : "deny",
            DefaultRoleIds = [.. p.DefaultRoles.Select(d => Wire.Id(d.RoleId))],
            DeletionChannelEnabled = p.DeletionChannelEnabled,
            // Built from the same string the OIDC options are built from, so the
            // panel and the handler cannot disagree about it.
            CallbackPath = Program.ApiPathBase + FederatedSchemes.CallbackPath(p.Slug),
            HasClientSecret = !string.IsNullOrEmpty(p.ClientSecret),
            HasDeletionSecret = !string.IsNullOrEmpty(p.DeletionSecret),
            MappingRules = mapping.Projected(p),
            LinkedAccounts = counts.TryGetValue(p.Id, out var n) ? n : 0,
            CreatedAt = Wire.At(p.CreatedAt),
        };

        public async Task<IdentityProviderDto> CreateAsync(IdentityProviderInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var slug = (input.Slug ?? "").Trim().ToLowerInvariant();
            RequireSlug(slug);
            if (await context.IdentityProviders.AnyAsync(p => p.Slug == slug, ct))
            {
                throw new ConflictException($"A provider with the slug \"{slug}\" already exists", "provider.slug.taken");
            }

            // Required on creation and optional afterwards. A provider registered
            // without one cannot complete a code exchange, so it would sit in the
            // list looking configured and fail at the only moment that matters.
            if (string.IsNullOrWhiteSpace(input.ClientSecret))
            {
                throw new ValidationException("A client secret is required", "provider.clientSecret.required");
            }

            var provider = new IdentityProvider
            {
                Slug = slug,
                DisplayName = "",
                Issuer = "",
                ClientId = "",
                ClientSecret = input.ClientSecret,
            };

            await ApplyAsync(provider, input, ct);
            context.IdentityProviders.Add(provider);
            await context.SaveChangesAsync(ct);
            registry.Invalidate();

            return await GetAsync(provider.Id, ct);
        }

        public async Task<IdentityProviderDto> UpdateAsync(
            Guid id, IdentityProviderInputDto input, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var provider = await context.IdentityProviders
                .Include(p => p.MappingRules)
                .Include(p => p.DefaultRoles)
                .FirstOrDefaultAsync(p => p.Id == id && p.Kind == ProviderKind.SignIn, ct)
                ?? throw new NotFoundException("Identity provider");

            var slug = (input.Slug ?? "").Trim().ToLowerInvariant();
            RequireSlug(slug);
            if (await context.IdentityProviders.AnyAsync(p => p.Slug == slug && p.Id != id, ct))
            {
                throw new ConflictException($"A provider with the slug \"{slug}\" already exists", "provider.slug.taken");
            }
            provider.Slug = slug;

            // Absent leaves the stored one alone. An empty string is the same
            // instruction rather than "clear it": there is no state where a
            // provider usefully has no secret, and treating blank as a deletion
            // would let a form that round-trips its own empty field silently
            // unconfigure a working provider.
            if (!string.IsNullOrWhiteSpace(input.ClientSecret))
            {
                provider.ClientSecret = input.ClientSecret;
            }

            await ApplyAsync(provider, input, ct);
            await context.SaveChangesAsync(ct);
            registry.Invalidate();

            return await GetAsync(provider.Id, ct);
        }

        /// <summary>
        /// Everything both paths validate and copy. Secrets are handled by the
        /// callers, because their rules differ: required once, preserved after.
        /// </summary>
        private async Task ApplyAsync(IdentityProvider provider, IdentityProviderInputDto input, CancellationToken ct)
        {
            // **Both paths, here rather than in the update path only.** The
            // create path used to leave this unset, so a registration that
            // supplied a deletion secret *and* asked for the channel was refused
            // by the guard below for not having the secret it had just been
            // given. Absent still means "leave the stored one alone"; on a
            // create there is nothing to leave.
            if (!string.IsNullOrWhiteSpace(input.DeletionSecret))
            {
                provider.DeletionSecret = input.DeletionSecret;
            }

            var displayName = (input.DisplayName ?? "").Trim();
            if (displayName.Length == 0)
            {
                throw new ValidationException("A display name is required", "provider.displayName.required");
            }

            provider.DisplayName = displayName;
            provider.Issuer = RequireIssuer(input.Issuer);

            var clientId = (input.ClientId ?? "").Trim();
            if (clientId.Length == 0)
            {
                throw new ValidationException("A client id is required", "provider.clientId.required");
            }
            provider.ClientId = clientId;

            provider.Scopes = string.IsNullOrWhiteSpace(input.Scopes)
                ? "openid profile email"
                : input.Scopes.Trim();
            provider.Enabled = input.Enabled;
            provider.AccountUrl = string.IsNullOrWhiteSpace(input.AccountUrl) ? null : input.AccountUrl.Trim();
            provider.DeletionUrl = string.IsNullOrWhiteSpace(input.DeletionUrl) ? null : input.DeletionUrl.Trim();

            var claimPath = string.IsNullOrWhiteSpace(input.ClaimPath) ? "groups" : input.ClaimPath.Trim();
            RequireClaimPath(claimPath);
            provider.ClaimPath = claimPath;

            provider.UnmappedBehavior = input.UnmappedBehavior switch
            {
                null or "" or "deny" => UnmappedBehavior.Deny,
                "defaultRole" => UnmappedBehavior.DefaultRole,
                _ => throw new ValidationException(
                    "unmappedBehavior is deny or defaultRole", "provider.unmappedBehavior.unknown"),
            };

            provider.DeletionChannelEnabled = input.DeletionChannelEnabled;
            if (provider.DeletionChannelEnabled && string.IsNullOrEmpty(provider.DeletionSecret))
            {
                // An open back channel with no secret is an endpoint anybody may
                // post an account deletion to.
                throw new ValidationException(
                    "The deletion channel needs a secret before it can be enabled",
                    "provider.deletionSecret.required");
            }

            if (input.MappingRules is { } wanted)
            {
                await mapping.ReplaceRulesAsync(provider, wanted, allowSlots: false, ct);
            }

            var defaults = input.DefaultRoleIds ?? [];
            if (provider.UnmappedBehavior == UnmappedBehavior.DefaultRole)
            {
                if (defaults.Count == 0)
                {
                    throw new ValidationException(
                        "unmappedBehavior is defaultRole, so defaultRoleIds must name at least one role",
                        "provider.defaultRole.required");
                }
            }
            else
            {
                // Under `deny` there is nothing to grant, and roles left behind
                // in the row would be a setting that looks live and is not.
                defaults = [];
            }
            await mapping.ReplaceDefaultRolesAsync(provider, defaults, ct);
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var provider = await context.IdentityProviders
                .FirstOrDefaultAsync(p => p.Id == id && p.Kind == ProviderKind.SignIn, ct)
                ?? throw new NotFoundException("Identity provider");

            // Refused rather than cascaded. Removing a provider that people sign
            // in through decides something about their accounts — under the
            // deletion cascade, an account whose last link goes and which has no
            // local credential is anonymized — and that is not a side effect a
            // delete button should have. Disabling it is the reversible act.
            var linked = await context.UserIdentities.CountAsync(i => i.ProviderId == id, ct);
            if (linked > 0)
            {
                throw new ConflictException(
                    $"{linked} account(s) sign in through \"{provider.Slug}\". Disable it instead, or remove the links first",
                    "provider.linked");
            }

            context.IdentityProviders.Remove(provider);
            await context.SaveChangesAsync(ct);
            registry.Invalidate();
        }

        private static void RequireSlug(string slug)
        {
            if (!SlugPattern().IsMatch(slug))
            {
                throw new ValidationException(
                    "A slug is 2-32 characters of a-z, 0-9 and hyphens, and does not start or end with one",
                    "provider.slug.invalid");
            }
        }

        /// <summary>
        /// An absolute HTTPS issuer, with loopback exempted so a development
        /// Authentik or Keycloak on <c>http://localhost</c> can be registered.
        /// <para>
        /// Not decoration: the issuer is half the federated key and the origin
        /// every token is validated against. Over plain HTTP on a real network,
        /// whoever answers first decides who your users are.
        /// </para>
        /// </summary>
        private static string RequireIssuer(string? value)
        {
            var issuer = (value ?? "").Trim().TrimEnd('/');
            if (issuer.Length == 0)
            {
                throw new ValidationException("An issuer is required", "provider.issuer.required");
            }
            if (!Uri.TryCreate(issuer, UriKind.Absolute, out _))
            {
                throw new ValidationException("The issuer must be an absolute URL", "provider.issuer.invalid");
            }

            // **The same rule the LTI platforms use, and it moved here rather
            // than being copied.** The version written inline said "https, or
            // anything on loopback" — and `Uri.IsLoopback` is true of a `file:`
            // URL, which has no host at all, so `file:///etc/passwd` passed it.
            if (!SecureUrl.IsHttpsOrLoopback(issuer))
            {
                throw new ValidationException(
                    "The issuer must be https, except on loopback", "provider.issuer.insecure");
            }
            return issuer;
        }

        /// <summary>
        /// A dotted path of claim names — and nothing that could be read as an
        /// expression. This is where "configuration, not code" is enforced.
        /// </summary>
        private static void RequireClaimPath(string path)
        {
            var segments = path.Split('.');
            if (segments.Any(s => s.Length == 0) ||
                !path.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or ':'))
            {
                throw new ValidationException(
                    "A claim path is dotted names, for example groups or realm_access.roles",
                    "provider.claimPath.invalid");
            }
        }

    }
}

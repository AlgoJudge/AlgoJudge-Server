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

        private IQueryable<IdentityProvider> Loaded() =>
            context.IdentityProviders.AsNoTracking().Include(p => p.MappingRules);

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
        private static IdentityProviderDto Project(IdentityProvider p, Dictionary<Guid, int> counts) => new()
        {
            Id = Wire.Id(p.Id),
            Slug = p.Slug,
            DisplayName = p.DisplayName,
            Issuer = p.Issuer,
            ClientId = p.ClientId,
            Scopes = p.Scopes,
            Enabled = p.Enabled,
            AccountUrl = p.AccountUrl,
            ClaimPath = p.ClaimPath,
            UnmappedBehavior = p.UnmappedBehavior == UnmappedBehavior.DefaultTemplate
                ? "defaultTemplate"
                : "deny",
            DefaultTemplateName = p.DefaultTemplateName,
            DeletionChannelEnabled = p.DeletionChannelEnabled,
            // Built from the same string the OIDC options are built from, so the
            // panel and the handler cannot disagree about it.
            CallbackPath = Program.ApiPathBase + FederatedSchemes.CallbackPath(p.Slug),
            HasClientSecret = !string.IsNullOrEmpty(p.ClientSecret),
            HasDeletionSecret = !string.IsNullOrEmpty(p.DeletionSecret),
            MappingRules = p.MappingRules
                .OrderBy(r => r.ClaimValue, StringComparer.Ordinal)
                .Select(r => new MappingRuleDto { ClaimValue = r.ClaimValue, TemplateName = r.TemplateName })
                .ToList(),
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
                .FirstOrDefaultAsync(p => p.Id == id, ct)
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

            var claimPath = string.IsNullOrWhiteSpace(input.ClaimPath) ? "groups" : input.ClaimPath.Trim();
            RequireClaimPath(claimPath);
            provider.ClaimPath = claimPath;

            provider.UnmappedBehavior = input.UnmappedBehavior switch
            {
                null or "" or "deny" => UnmappedBehavior.Deny,
                "defaultTemplate" => UnmappedBehavior.DefaultTemplate,
                _ => throw new ValidationException(
                    "unmappedBehavior is deny or defaultTemplate", "provider.unmappedBehavior.unknown"),
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

            var defaultTemplate = string.IsNullOrWhiteSpace(input.DefaultTemplateName)
                ? null
                : input.DefaultTemplateName.Trim();

            if (provider.UnmappedBehavior == UnmappedBehavior.DefaultTemplate)
            {
                if (defaultTemplate is null)
                {
                    throw new ValidationException(
                        "defaultTemplate needs a template to grant", "provider.defaultTemplate.required");
                }
                await RequireMappableAsync(defaultTemplate, ct);
            }
            else if (defaultTemplate is not null)
            {
                // Under `deny` there is nothing to grant, and a name left behind
                // in the row would be a setting that looks live and is not.
                defaultTemplate = null;
            }
            provider.DefaultTemplateName = defaultTemplate;

            if (input.MappingRules is { } wanted)
            {
                await ReplaceRulesAsync(provider, wanted, ct);
            }
        }

        private async Task ReplaceRulesAsync(
            IdentityProvider provider, IReadOnlyList<MappingRuleDto> wanted, CancellationToken ct)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rules = new List<IdentityProviderMappingRule>();

            foreach (var rule in wanted)
            {
                var value = (rule.ClaimValue ?? "").Trim();
                var template = (rule.TemplateName ?? "").Trim();

                if (value.Length == 0)
                {
                    throw new ValidationException("A rule needs a claim value", "provider.rule.claimValue.required");
                }
                if (!seen.Add(value))
                {
                    // Two rules for one value is not a merge — it is a question
                    // about ordering that this model deliberately does not have.
                    throw new ValidationException(
                        $"The claim value \"{value}\" is mapped twice", "provider.rule.duplicate");
                }

                await RequireMappableAsync(template, ct);
                rules.Add(new IdentityProviderMappingRule
                {
                    ProviderId = provider.Id,
                    ClaimValue = value,
                    TemplateName = template,
                });
            }

            provider.MappingRules.Clear();
            foreach (var rule in rules) provider.MappingRules.Add(rule);
        }

        /// <summary>
        /// The two guards, in one place so neither can be applied without the
        /// other.
        /// <para>
        /// Both refusals name the permission at fault. A validation message that
        /// says only "not allowed" turns a five-second correction into an
        /// afternoon of guessing which entry in a template of thirty is the
        /// problem.
        /// </para>
        /// </summary>
        private async Task RequireMappableAsync(string templateName, CancellationToken ct)
        {
            if (templateName.Length == 0)
            {
                throw new ValidationException("A rule needs a template", "provider.rule.template.required");
            }

            var template = await context.PermissionTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Name == templateName, ct)
                ?? throw new ValidationException(
                    $"No template named \"{templateName}\"", "provider.rule.template.unknown");

            var granted = Parse(template.Permissions);

            // Unreachable in every configuration, and not merely absent from the
            // templates that ship. An installation may invent a template, and one
            // carrying this key would otherwise turn a directory group into a way
            // of becoming an administrator here.
            if (granted.Contains(Permissions.SystemAdministrator))
            {
                throw new ForbiddenActionException(
                    $"\"{templateName}\" grants {Permissions.SystemAdministrator}, which no claim may ever grant",
                    "provider.rule.administrator");
            }

            // The same rule that governs writing a grant. Without it, holding
            // `provider:manage` would be a way of granting yourself anything: map
            // a group you are in onto a template you could not otherwise assign,
            // then sign in through the provider.
            var mine = await permissions.EffectiveAsync(null, ct);
            if (!mine.Contains(Permissions.SystemAdministrator))
            {
                var excess = granted.Where(p => !mine.Contains(p)).ToList();
                if (excess.Count > 0)
                {
                    throw new ForbiddenActionException(
                        "Cannot map onto permissions you do not hold: " + string.Join(", ", excess),
                        "provider.rule.excess");
                }
            }
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var provider = await context.IdentityProviders.FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw new NotFoundException("Identity provider");

            // Refused rather than cascaded. Removing a provider that people sign
            // in through decides something about their accounts — under the
            // deletion cascade, an account whose last link goes and which has no
            // local credential is anonymised — and that is not a side effect a
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
        /// Authentik on <c>http://localhost</c> can be registered.
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
            if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri))
            {
                throw new ValidationException("The issuer must be an absolute URL", "provider.issuer.invalid");
            }
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
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

        private static IReadOnlyList<string> Parse(string json)
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

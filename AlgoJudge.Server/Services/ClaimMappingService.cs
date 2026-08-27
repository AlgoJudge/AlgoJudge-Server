using System.Security.Claims;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Services
{
    /// <summary>
    /// What a provider's token buys, and nothing more.
    /// </summary>
    /// <param name="Matched">The claim values that matched a rule, for the log.</param>
    /// <param name="Permissions">
    /// The union of the matched templates. Empty is a real answer and is what
    /// <see cref="UnmappedBehavior"/> then decides about.
    /// </param>
    /// <param name="Templates">Which templates were used, for the log and the panel.</param>
    public record MappedContribution(
        IReadOnlyList<string> Matched,
        IReadOnlySet<string> Permissions,
        IReadOnlyList<string> Templates)
    {
        public bool Any => Matched.Count > 0;
    }

    public interface IClaimMappingService
    {
        Task<MappedContribution> ResolveAsync(
            IdentityProvider provider, ClaimsPrincipal principal, CancellationToken ct);

        /// <summary>Exposed for tests and for the panel's "what would this token get?".</summary>
        IReadOnlyList<string> ValuesAt(ClaimsPrincipal principal, string dottedPath);
    }

    /// <summary>
    /// Turns the claims in a token into a permission set, through the
    /// installation's allowlist.
    /// <para>
    /// <b>A path, never an expression.</b> The claim is found by walking dotted
    /// names, and matching is exact string equality against rules an operator
    /// wrote. There is no pattern, no prefix and nothing evaluated: an expression
    /// in provider configuration is code executed against the contents of a
    /// token, and a token is written by somebody else.
    /// </para>
    /// <para>
    /// The write path already refuses a rule pointing at a template that carries
    /// <c>system:administrator</c>, or at permissions its author does not hold.
    /// This filters again anyway — a template can be edited after a rule names
    /// it, and "unreachable through a mapping, in every configuration" has to
    /// mean at the moment the mapping is used, not only at the moment it was
    /// written.
    /// </para>
    /// </summary>
    public class ClaimMappingService(ApplicationDbContext context) : IClaimMappingService
    {
        public async Task<MappedContribution> ResolveAsync(
            IdentityProvider provider, ClaimsPrincipal principal, CancellationToken ct)
        {
            var present = ValuesAt(principal, provider.ClaimPath);
            if (present.Count == 0)
            {
                return new MappedContribution([], new HashSet<string>(), []);
            }

            var rules = provider.MappingRules.Count > 0
                ? provider.MappingRules.ToList()
                : await context.IdentityProviderMappingRules
                    .AsNoTracking()
                    .Where(r => r.ProviderId == provider.Id)
                    .ToListAsync(ct);

            var matched = new List<string>();
            var templates = new List<string>();

            foreach (var value in present)
            {
                var rule = rules.FirstOrDefault(r => string.Equals(r.ClaimValue, value, StringComparison.Ordinal));
                if (rule is null) continue;

                matched.Add(value);
                if (!templates.Contains(rule.TemplateName)) templates.Add(rule.TemplateName);
            }

            if (matched.Count == 0)
            {
                return new MappedContribution([], new HashSet<string>(), []);
            }

            return new MappedContribution(
                matched, await PermissionsOfAsync(templates, ct), templates);
        }

        /// <summary>
        /// The union of the named templates, with the two things a claim may
        /// never carry stripped: <c>system:administrator</c>, and any key the
        /// catalogue does not describe.
        /// <para>
        /// A template naming a permission this Server has never heard of would
        /// otherwise be stored into a grant, and <c>Permissions.IsStaff</c> counts
        /// an unknown key as staff — so a typo in a template would quietly take
        /// somebody out of a ranking.
        /// </para>
        /// </summary>
        private async Task<IReadOnlySet<string>> PermissionsOfAsync(
            IReadOnlyList<string> templateNames, CancellationToken ct)
        {
            var stored = await context.PermissionTemplates
                .AsNoTracking()
                .Where(t => templateNames.Contains(t.Name))
                .Select(t => t.Permissions)
                .ToListAsync(ct);

            var permissions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var json in stored)
            {
                foreach (var key in Parse(json))
                {
                    if (key == Permissions.SystemAdministrator) continue;
                    if (Permissions.Unknown([key]).Count > 0) continue;
                    permissions.Add(key);
                }
            }
            return permissions;
        }

        /// <summary>
        /// Every value at a dotted path, as strings.
        /// <para>
        /// Two shapes arrive and both are ordinary. A provider may emit one claim
        /// per group — several claims with the same name — or a single claim
        /// whose value is a JSON array. Keycloak does the second under
        /// <c>realm_access.roles</c>, Authentik the first under <c>groups</c>,
        /// and an installation should not have to know which.
        /// </para>
        /// <para>
        /// Both are driven through a whole sign-in in <c>FederatedSignInTests</c>
        /// and not only through this method. Until 2026-08-26 the nested shape was
        /// covered here and nowhere else, so "either provider works" rested on a
        /// unit test of one function.
        /// </para>
        /// </summary>
        public IReadOnlyList<string> ValuesAt(ClaimsPrincipal principal, string dottedPath)
        {
            var segments = dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return [];

            var head = segments[0];
            var rest = segments.Skip(1).ToArray();

            var found = new List<string>();
            foreach (var claim in principal.FindAll(head))
            {
                found.AddRange(Flatten(claim.Value, rest));
            }
            return found;
        }

        /// <summary>
        /// Walks whatever is left of the path into the claim's own value, then
        /// flattens what it lands on.
        /// <para>
        /// A claim value is a string as far as the framework is concerned; when
        /// the rest of the path is non-empty it has to be JSON, and when it is
        /// not JSON there is nothing to walk into. That is a configuration
        /// mistake and it resolves to no values — which
        /// <see cref="UnmappedBehavior"/> then decides about, rather than an
        /// exception during somebody's sign-in.
        /// </para>
        /// </summary>
        private static IEnumerable<string> Flatten(string raw, string[] path)
        {
            if (path.Length == 0 && !LooksLikeJson(raw)) return [raw];

            JsonElement element;
            try
            {
                element = JsonDocument.Parse(raw).RootElement.Clone();
            }
            catch (JsonException)
            {
                return path.Length == 0 ? [raw] : [];
            }

            foreach (var segment in path)
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty(segment, out var next))
                {
                    return [];
                }
                element = next;
            }

            return Leaves(element);
        }

        private static bool LooksLikeJson(string raw)
        {
            var trimmed = raw.AsSpan().TrimStart();
            return trimmed.Length > 0 && (trimmed[0] == '[' || trimmed[0] == '{');
        }

        private static IEnumerable<string> Leaves(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => [element.GetString()!],
            JsonValueKind.Number => [element.GetRawText()],
            JsonValueKind.Array => element.EnumerateArray().SelectMany(Leaves),
            // An object at the end of the path is not a value. Deliberately not
            // stringified: matching a rule against `{"name":"staff"}` would be a
            // rule nobody could write on purpose.
            _ => [],
        };

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

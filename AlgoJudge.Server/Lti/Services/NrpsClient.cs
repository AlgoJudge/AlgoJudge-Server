using System.Net.Http.Headers;
using System.Text.Json;
using AlgoJudge.Server.Lti.Data;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>Where the platform will answer with a course's roster.</summary>
    public record NrpsEndpoint(string ContextMemberships, string? ServiceVersion)
    {
        public static NrpsEndpoint? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                if (!root.TryGetProperty("context_memberships_url", out var url)
                    || url.GetString() is not { Length: > 0 } address)
                {
                    return null;
                }

                // A list in the specification, and platforms send one or several.
                // Kept only to be reported: nothing here behaves differently for
                // 2.0 than for anything else, and pretending otherwise would be a
                // branch nobody has tested against a platform that needs it.
                var version = root.TryGetProperty("service_versions", out var versions)
                        && versions.ValueKind == JsonValueKind.Array
                    ? versions.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString())
                        .FirstOrDefault()
                    : null;

                return new NrpsEndpoint(address, version);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// One person as the platform describes them in a course.
    ///
    /// <para>
    /// <b>Everything but <see cref="UserId"/> is optional, and that is the whole
    /// difficulty of milestone 2.</b> The subject is the only field a platform
    /// must send; a name, an address and a username are all things it may be
    /// configured not to disclose. What can be matched on therefore depends on
    /// the installation, not on the specification.
    /// </para>
    /// </summary>
    public record RosterMember
    {
        public required string UserId { get; init; }
        public IReadOnlyList<string> Roles { get; init; } = [];
        public string? Status { get; init; }
        public string? Name { get; init; }
        public string? GivenName { get; init; }
        public string? FamilyName { get; init; }
        public string? Email { get; init; }

        /// <summary>
        /// The username the platform asserts for this person, where it sends one
        /// at all.
        ///
        /// <para>
        /// <b>There is no standard field for it.</b> The specification's member
        /// object has no username, so a platform that discloses one does it its
        /// own way — Moodle puts it in <c>ext_user_username</c>, gated behind the
        /// same "may send names" switch as the full name, and measured on 5.2.2
        /// it is <b>not</b> carried as a custom parameter no matter what the tool
        /// registered. Read from a per-member custom value too, because that is
        /// where a different platform may well put it.
        /// </para>
        /// </summary>
        public string? Username { get; init; }
    }

    public record Roster(string ContextId, IReadOnlyList<RosterMember> Members);

    public interface INrpsClient
    {
        /// <summary>
        /// Reads a course's roster, following the platform's paging.
        /// </summary>
        /// <param name="resourceLinkId">
        /// The platform's own id for the placement, where the roster should be
        /// read for one link rather than the whole course. <b>Moodle discloses
        /// per-member, per-link data only when asked this way</b>, and answers
        /// the course's whole roster otherwise.
        /// </param>
        Task<Roster> ReadAsync(
            Platform platform, string url, string? resourceLinkId, CancellationToken ct);
    }

    /// <summary>
    /// The Names and Role Provisioning Service, read-only.
    ///
    /// <para>
    /// <b>Read-only is the whole of it, and permanently.</b> NRPS describes who a
    /// course holds; it is not a way to change that, and this product has no
    /// business writing into a university's course. What it produces here is
    /// membership <i>in an AlgoJudge activity</i>, which is ours.
    /// </para>
    /// </summary>
    public class NrpsClient(
        IHttpClientFactory clients, IPlatformTokens tokens, ILogger<NrpsClient> logger) : INrpsClient
    {
        /// <summary>
        /// The only scope this asks for. Asking for more than is needed is how a
        /// tool ends up holding a token that can post grades while reading a
        /// roster.
        /// </summary>
        public const string Scope =
            "https://purl.imsglobal.org/spec/lti-nrps/scope/contextmembership.readonly";

        private const string MediaType =
            "application/vnd.ims.lti-nrps.v2.membershipcontainer+json";

        /// <summary>
        /// A ceiling on paging, so a platform that answers with a `Link` header
        /// pointing at itself cannot hold this open for ever. Reached means
        /// something is wrong at the platform, and it is logged rather than
        /// silently truncating a roster.
        /// </summary>
        private const int MaximumPages = 50;

        public async Task<Roster> ReadAsync(
            Platform platform, string url, string? resourceLinkId, CancellationToken ct)
        {
            var token = await tokens.GetAsync(platform, [Scope], ct);
            var http = clients.CreateClient(nameof(NrpsClient));

            var members = new List<RosterMember>();
            var contextId = "";
            var next = WithLink(url, resourceLinkId);

            for (var page = 0; page < MaximumPages && next is { Length: > 0 }; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));

                using var response = await http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    throw new NrpsException(
                        $"The platform refused the roster ({(int)response.StatusCode}): {Short(body)}");
                }

                var (id, page_members) = ReadPage(body);
                if (id is { Length: > 0 }) contextId = id;
                members.AddRange(page_members);

                next = NextPage(response);
            }

            if (next is { Length: > 0 })
            {
                logger.LogWarning(
                    "Stopped reading the roster from {Url} after {Pages} pages; the platform kept offering more",
                    url, MaximumPages);
            }

            return new Roster(contextId, members);
        }

        /// <summary>
        /// Asks for one link's roster rather than the whole course's, where the
        /// caller knows which link. The platform decides what that changes; for
        /// Moodle it is the difference between members carrying per-link data and
        /// carrying none.
        /// </summary>
        private static string WithLink(string url, string? resourceLinkId)
        {
            if (string.IsNullOrWhiteSpace(resourceLinkId)) return url;
            var separator = url.Contains('?') ? '&' : '?';
            return url + separator + "rlid=" + Uri.EscapeDataString(resourceLinkId);
        }

        /// <summary>
        /// <b>Paging is in a `Link` header, not in the body.</b> The container has
        /// no "next" field, so a reader that only looks at the JSON silently gets
        /// the first page and calls it the roster.
        /// </summary>
        private static string? NextPage(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Link", out var links)) return null;

            foreach (var header in links)
            {
                foreach (var part in header.Split(','))
                {
                    var pieces = part.Split(';');
                    if (pieces.Length < 2) continue;
                    if (!pieces.Skip(1).Any(p => p.Contains("rel=\"next\"") || p.Contains("rel=next")))
                    {
                        continue;
                    }

                    var address = pieces[0].Trim().Trim('<', '>');
                    if (address.Length > 0) return address;
                }
            }

            return null;
        }

        private static (string? ContextId, IReadOnlyList<RosterMember> Members) ReadPage(string body)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var contextId = root.TryGetProperty("id", out _)
                    && root.TryGetProperty("context", out var context)
                    && context.ValueKind == JsonValueKind.Object
                    && context.TryGetProperty("id", out var id)
                ? id.GetString()
                : null;

            if (!root.TryGetProperty("members", out var members)
                || members.ValueKind != JsonValueKind.Array)
            {
                return (contextId, []);
            }

            return (contextId, members.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.Object)
                .Select(Read)
                .Where(m => m is not null)
                .Select(m => m!)
                .ToList());
        }

        private static RosterMember? Read(JsonElement member)
        {
            if (Text(member, "user_id") is not { Length: > 0 } userId) return null;

            return new RosterMember
            {
                UserId = userId,
                Roles = member.TryGetProperty("roles", out var roles)
                        && roles.ValueKind == JsonValueKind.Array
                    ? roles.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String)
                        .Select(r => r.GetString()!).ToList()
                    : [],
                Status = Text(member, "status"),
                Name = Text(member, "name"),
                GivenName = Text(member, "given_name"),
                FamilyName = Text(member, "family_name"),
                Email = Text(member, "email"),
                // Per-member custom values, where the platform sends them. This
                // is where `$User.username` arrives if the tool asked for it and
                // the platform substitutes per member rather than per launch.
                Username = Text(member, "ext_user_username")
                    ?? (member.TryGetProperty("message", out var messages)
                            && messages.ValueKind == JsonValueKind.Array
                        ? messages.EnumerateArray()
                            .Select(CustomUsername)
                            .FirstOrDefault(u => u is { Length: > 0 })
                        : null),
            };
        }

        private static string? CustomUsername(JsonElement message) =>
            message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty(LtiClaims.Custom, out var custom)
                && custom.ValueKind == JsonValueKind.Object
                    ? Text(custom, "username")
                    : null;

        private static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string Short(string body) =>
            body.Length <= 300 ? body : body[..300];
    }

    public class NrpsException(string message) : Exception(message);
}

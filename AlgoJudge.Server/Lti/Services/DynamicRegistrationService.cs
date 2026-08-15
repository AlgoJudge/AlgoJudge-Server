using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Utils;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>An invitation, as a manager reads it.</summary>
    public record InvitationView
    {
        public required string Id { get; init; }
        public required string Note { get; init; }

        /// <summary>
        /// The whole address to hand over, code and all. Given as one string
        /// because that is what gets pasted — an operator assembling it from
        /// parts is an operator who will assemble it wrong once.
        /// </summary>
        public required string RegistrationUrl { get; init; }

        public required string ExpiresAt { get; init; }
        public string? UsedAt { get; init; }
        public string? PlatformId { get; init; }
    }

    /// <summary>What the platform called this tool once it had registered it.</summary>
    public record RegisteredTool(string ClientId, string DeploymentId);

    /// <summary>What a completed registration produced, for the page to say.</summary>
    public record RegistrationOutcome(string PlatformName, string Issuer, Guid PlatformId);

    public interface IDynamicRegistrationService
    {
        Task<IReadOnlyList<InvitationView>> ListAsync(CancellationToken ct);

        /// <summary>Expects one registration, and says where to send it.</summary>
        Task<InvitationView> InviteAsync(string? note, CancellationToken ct);

        Task RevokeAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// The platform's own half: it opens this with a configuration URL and a
        /// token it minted. Anonymous by necessity — the person driving is the
        /// platform's administrator, not ours — and therefore gated on the code.
        /// </summary>
        Task<RegistrationOutcome> RegisterAsync(
            string code, string configurationUrl, string? registrationToken, CancellationToken ct);
    }

    public class DynamicRegistrationService(
        LtiDbContext db,
        IPlatformService platforms,
        IHttpClientFactory clients,
        IPermissionService permissions,
        ICurrentUserService current,
        IConfiguration configuration,
        IHttpContextAccessor http,
        TimeProvider clock,
        ILogger<DynamicRegistrationService> logger
    ) : IDynamicRegistrationService
    {
        /// <summary>
        /// Long enough to walk to somebody's office and short enough that a link
        /// left in a chat is worth nothing tomorrow.
        /// </summary>
        private static readonly TimeSpan Life = TimeSpan.FromMinutes(30);

        private const string PlatformConfiguration =
            "https://purl.imsglobal.org/spec/lti-platform-configuration";
        private const string ToolConfiguration =
            "https://purl.imsglobal.org/spec/lti-tool-configuration";

        public async Task<IReadOnlyList<InvitationView>> ListAsync(CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var invitations = await db.RegistrationInvitations.AsNoTracking()
                .OrderByDescending(i => i.CreatedAt)
                .Take(50)
                .ToListAsync(ct);

            return invitations.Select(Project).ToList();
        }

        public async Task<InvitationView> InviteAsync(string? note, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var invitation = new RegistrationInvitation
            {
                Code = Opaque(),
                CreatedByUserId = current.UserId
                    ?? throw new ForbiddenActionException("Not signed in", "auth.required"),
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                CreatedAt = clock.GetUtcNow().UtcDateTime,
                ExpiresAt = clock.GetUtcNow().UtcDateTime + Life,
            };

            db.RegistrationInvitations.Add(invitation);
            await db.SaveChangesAsync(ct);

            return Project(invitation);
        }

        public async Task RevokeAsync(Guid id, CancellationToken ct)
        {
            await permissions.RequireAsync(Permissions.ProviderManage, null, ct);

            var invitation = await db.RegistrationInvitations
                .FirstOrDefaultAsync(i => i.Id == id, ct)
                ?? throw new NotFoundException("Registration invitation");

            // Expired rather than deleted, so the list still shows that somebody
            // expected a registration and called it off.
            invitation.ExpiresAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        public async Task<RegistrationOutcome> RegisterAsync(
            string code, string configurationUrl, string? registrationToken, CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            // **Claimed by a conditional update, not by reading and writing.** Two
            // platforms arriving with the same address would otherwise both find
            // it unused, and one invitation would admit two.
            var claimed = await db.RegistrationInvitations
                .Where(i => i.Code == code && i.UsedAt == null && i.ExpiresAt > now)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.UsedAt, now), ct);

            if (claimed == 0)
            {
                throw new NotFoundException("Registration invitation");
            }

            var invitation = await db.RegistrationInvitations
                .FirstAsync(i => i.Code == code, ct);

            var configuration = await ReadConfigurationAsync(configurationUrl, ct);

            var registered = await RegisterWithPlatformAsync(
                configuration, registrationToken, ct);

            // **Disabled, and never an identity authority** (§10). A registration
            // over the network proves that somebody could reach this address with
            // a live invitation — not that they run the university's Moodle, and
            // certainly not that they may say who anybody is. Both remain acts a
            // person takes on the platforms screen, with the consequences in
            // front of them.
            var platform = await platforms.RegisterInvitedAsync(new PlatformInput
            {
                DisplayName = configuration.ProductName,
                Issuer = configuration.Issuer,
                ClientId = registered.ClientId,
                DeploymentId = registered.DeploymentId,
                KeySetUrl = configuration.JwksUri,
                AuthTokenUrl = configuration.TokenEndpoint,
                AuthLoginUrl = configuration.AuthorizationEndpoint,
                IsIdentityAuthority = false,
                IdentityNamespace = null,
                UsernameClaim = null,
                Enabled = false,
            }, ct);

            invitation.PlatformId = platform.Id;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "A platform registered itself dynamically: {Issuer}, invited by {User}",
                configuration.Issuer, invitation.CreatedByUserId);

            return new RegistrationOutcome(configuration.ProductName, configuration.Issuer, platform.Id);
        }

        /// <summary>
        /// The platform's own description of itself, fetched from the address it
        /// gave. Only ever read: nothing here trusts it beyond writing a disabled
        /// row.
        /// </summary>
        private async Task<PlatformConfigurationView> ReadConfigurationAsync(
            string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address)
                || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            {
                throw new ValidationException(
                    "The platform gave no usable configuration address", "lti.registration.configuration");
            }

            var http = clients.CreateClient(nameof(DynamicRegistrationService));
            using var response = await http.GetAsync(address, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new ValidationException(
                    $"The platform's configuration could not be read ({(int)response.StatusCode})",
                    "lti.registration.configuration");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            string Text(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";

            var lti = root.TryGetProperty(PlatformConfiguration, out var platform)
                    && platform.ValueKind == JsonValueKind.Object
                ? platform
                : default;

            var product = lti.ValueKind == JsonValueKind.Object
                    && lti.TryGetProperty("product_family_code", out var family)
                    && family.ValueKind == JsonValueKind.String
                ? family.GetString()!
                : "platform";

            var view = new PlatformConfigurationView
            {
                Issuer = Text("issuer"),
                TokenEndpoint = Text("token_endpoint"),
                AuthorizationEndpoint = Text("authorization_endpoint"),
                JwksUri = Text("jwks_uri"),
                RegistrationEndpoint = Text("registration_endpoint"),
                ProductName = char.ToUpperInvariant(product[0]) + product[1..],
            };

            if (view.Issuer.Length == 0 || view.RegistrationEndpoint.Length == 0
                || view.TokenEndpoint.Length == 0 || view.AuthorizationEndpoint.Length == 0
                || view.JwksUri.Length == 0)
            {
                throw new ValidationException(
                    "That configuration is missing something a launch needs — issuer, endpoints or key set",
                    "lti.registration.configuration");
            }

            return view;
        }

        /// <summary>
        /// Tells the platform what this tool is, and reads back the client id it
        /// was given.
        /// </summary>
        private async Task<RegisteredTool> RegisterWithPlatformAsync(
            PlatformConfigurationView platform, string? token, CancellationToken ct)
        {
            var api = ApiBase();

            var body = new Dictionary<string, object?>
            {
                ["application_type"] = "web",
                ["response_types"] = new[] { "id_token" },
                ["grant_types"] = new[] { "client_credentials", "implicit" },
                ["initiate_login_uri"] = api + "/lti/login",
                ["redirect_uris"] = new[] { api + "/lti/launch" },
                ["client_name"] = "AlgoJudge",
                ["jwks_uri"] = api + "/lti/jwks.json",
                ["token_endpoint_auth_method"] = "private_key_jwt",
                ["scope"] = string.Join(' ', new[]
                {
                    AgsScopes.LineItem, AgsScopes.Score, AgsScopes.ResultReadOnly,
                    NrpsClient.Scope,
                }),
                [ToolConfiguration] = new Dictionary<string, object?>
                {
                    ["domain"] = new Uri(api).Authority,
                    ["target_link_uri"] = api + "/lti/launch",
                    // What this tool wants to be told about a person. It asks for
                    // no more than the launch already reads.
                    ["claims"] = new[] { "sub", "iss", "name", "given_name", "family_name", "email" },
                    // **Both, or the second one can never happen.** A platform
                    // registered here offering only the first gets a tool that
                    // cannot be asked what to place — measured on Moodle 5.2
                    // (2026-08-15), where a dynamically registered tool without
                    // this message simply has no content selection.
                    ["messages"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "LtiResourceLinkRequest",
                        },
                        new Dictionary<string, object?>
                        {
                            ["type"] = "LtiDeepLinkingRequest",
                            ["target_link_uri"] = api + "/lti/launch",
                        },
                    },
                    // **The parameter identity linking rests on** (§4.3), asked
                    // for here rather than left to be typed. A registration that
                    // omits it produces a tool whose launches resolve nobody, and
                    // the reason is invisible in the platform's own screens.
                    ["custom_parameters"] = new Dictionary<string, string>
                    {
                        ["username"] = "$User.username",
                        ["context_history"] = "$Context.id.history",
                    },
                },
            };

            var http = clients.CreateClient(nameof(DynamicRegistrationService));
            using var request = new HttpRequestMessage(HttpMethod.Post, platform.RegistrationEndpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await http.SendAsync(request, ct);
            var answer = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new ValidationException(
                    $"The platform refused the registration ({(int)response.StatusCode}): "
                    + (answer.Length > 200 ? answer[..200] : answer),
                    "lti.registration.refused");
            }

            using var document = JsonDocument.Parse(answer);
            if (!document.RootElement.TryGetProperty("client_id", out var clientId)
                || clientId.GetString() is not { Length: > 0 } id)
            {
                throw new ValidationException(
                    "The platform accepted the registration and returned no client id",
                    "lti.registration.refused");
            }

            // **The deployment id comes back with the answer**, and only from
            // there: it is the platform's own name for this installation of the
            // tool, and a launch is matched on it. Measured in Moodle's
            // `registration_helper.php` on 4.5.13 and 5.2.2 (2026-08-15), where
            // it is the tool type's id as a string, inside the tool
            // configuration claim rather than at the top level.
            if (!document.RootElement.TryGetProperty(ToolConfiguration, out var tool)
                || !tool.TryGetProperty("deployment_id", out var deployment)
                || deployment.GetString() is not { Length: > 0 } deploymentId)
            {
                throw new ValidationException(
                    "The platform accepted the registration and returned no deployment id, "
                    + "so no launch from it could be matched to this registration",
                    "lti.registration.refused");
            }

            return new RegisteredTool(id, deploymentId);
        }

        /// <summary>
        /// The address a browser reaches this Server at, built the way
        /// <c>LtiPlatformsController</c> builds it: the configured value, and
        /// failing that the address this very request arrived on.
        ///
        /// <para>
        /// <b>It is checked rather than trusted.</b> Everywhere else this value
        /// is shown to somebody who can see it is wrong; here it is written into
        /// another institution's configuration, and a bad one is discovered at
        /// the end of a stranger's first launch. An empty configuration key used
        /// to reach <c>new Uri("")</c> and answer a platform with a 500.
        /// </para>
        /// </summary>
        private string ApiBase()
        {
            var request = http.HttpContext?.Request;
            var configured = configuration["PublicApiUrl"];
            var api = (string.IsNullOrWhiteSpace(configured)
                ? request is null
                    ? ""
                    : $"{request.Scheme}://{request.Host}{request.PathBase}"
                : configured).TrimEnd('/');

            if (!Uri.TryCreate(api, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            {
                throw new ValidationException(
                    "This installation has no absolute public address configured, so there is "
                    + "nothing to register — set PublicApiUrl to the address platforms reach it at",
                    "lti.registration.publicApiUrl");
            }

            return api;
        }

        private InvitationView Project(RegistrationInvitation invitation) => new()
        {
            Id = Wire.Id(invitation.Id),
            Note = invitation.Note ?? "",
            RegistrationUrl = ApiBase() + "/lti/register?code=" + Uri.EscapeDataString(invitation.Code),
            ExpiresAt = Wire.At(invitation.ExpiresAt),
            UsedAt = invitation.UsedAt is { } used ? Wire.At(used) : null,
            PlatformId = invitation.PlatformId is { } id ? Wire.Id(id) : null,
        };

        /// <summary>Random, and long enough that guessing is not a strategy.</summary>
        private static string Opaque() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private record PlatformConfigurationView
        {
            public required string Issuer { get; init; }
            public required string TokenEndpoint { get; init; }
            public required string AuthorizationEndpoint { get; init; }
            public required string JwksUri { get; init; }
            public required string RegistrationEndpoint { get; init; }
            public required string ProductName { get; init; }
        }
    }
}

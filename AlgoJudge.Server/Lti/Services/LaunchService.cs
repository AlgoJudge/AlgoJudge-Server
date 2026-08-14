using System.Security.Cryptography;
using System.Text.Json;
using AlgoJudge.Server.Lti.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AlgoJudge.Server.Lti.Services
{
    /// <summary>What a login initiation asks the tool to start.</summary>
    public record LoginInitiation
    {
        public required string Issuer { get; init; }
        public string? ClientId { get; init; }
        public string? DeploymentId { get; init; }
        public string? LoginHint { get; init; }
        public string? MessageHint { get; init; }
        public string? TargetLinkUri { get; init; }
    }

    /// <summary>A validated launch, read down to what this module uses.</summary>
    public record LaunchedMessage
    {
        public required Platform Platform { get; init; }
        public required string Subject { get; init; }
        public required string ResourceLinkId { get; init; }
        public string? ResourceLinkTitle { get; init; }
        public required string ContextId { get; init; }
        public string? ContextTitle { get; init; }
        public string? ContextHistory { get; init; }
        public required IReadOnlyList<string> Roles { get; init; }

        /// <summary>The slug naming the activity this placement runs (§5.1).</summary>
        public string? ActivitySlug { get; init; }

        /// <summary>What the platform asserted this person's username is (§4.3).</summary>
        public string? AssertedUsername { get; init; }

        /// <summary>From `launch_presentation`. Drives the Client's language (§5.4).</summary>
        public string? Locale { get; init; }

        /// <summary>`iframe` or `window`. Embedded is the learner's default (§5.2).</summary>
        public string? DocumentTarget { get; init; }

        /// <summary>Where the platform wants a finished or failed launch to return.</summary>
        public string? ReturnUrl { get; init; }

        /// <summary>The AGS endpoint claim, kept whole for milestone 5's line items.</summary>
        public string? AgsEndpointJson { get; init; }

        /// <summary>The NRPS claim, kept whole for milestone 2's roster.</summary>
        public string? NrpsJson { get; init; }
    }

    public interface ILaunchService
    {
        /// <summary>
        /// Answers a login initiation with the address to send the browser to.
        /// </summary>
        Task<string> BeginAsync(LoginInitiation initiation, string redirectUri, CancellationToken ct);

        /// <summary>
        /// Validates a launch and consumes its state. Throws
        /// <see cref="LtiLaunchException"/> for every refusal.
        /// </summary>
        Task<LaunchedMessage> CompleteAsync(string? state, string? idToken, CancellationToken ct);
    }

    /// <summary>
    /// The OIDC third-party-initiated login that starts every LTI 1.3 launch,
    /// and the validation of what comes back.
    /// <para>
    /// <b>The `state` and `nonce` live in this Server, in a table.</b> The
    /// specification's own answer is LTI Platform Storage — the initiation
    /// carries an <c>lti_storage_target</c> and the tool parks its state in the
    /// platform's storage over <c>postMessage</c>, so no cookie of its own is
    /// needed in a third-party frame. Measured 2026-08-13: <b>Moodle implements
    /// none of it</b>, in 4.5.13, 5.2.2 or 5.3dev, and <c>mod/lti</c> contains no
    /// <c>postMessage</c> at all. A cookie would then be the only other place to
    /// put it, and a cookie in an iframe is what Safari has blocked for years.
    /// </para>
    /// <para>
    /// So the launch itself needs no cookie. What still does is the session that
    /// follows it, which is §5.3's separate half and is measured in a browser
    /// rather than reasoned about.
    /// </para>
    /// </summary>
    public class LaunchService(
        LtiDbContext db,
        IPlatformKeys keys,
        TimeProvider clock
    ) : ILaunchService
    {
        /// <summary>
        /// How long a launch may take between the initiation and the token.
        /// <para>
        /// Short, because it is a redirect chain a person is waiting through
        /// rather than something resumed later, and every extra minute is a
        /// minute a stolen `state` is worth something. Long enough to survive a
        /// slow login at the platform, which is what sits in the middle.
        /// </para>
        /// </summary>
        private static readonly TimeSpan LaunchWindow = TimeSpan.FromMinutes(10);

        public async Task<string> BeginAsync(
            LoginInitiation initiation, string redirectUri, CancellationToken ct)
        {
            var platform = await ResolveAsync(initiation.Issuer, initiation.ClientId,
                initiation.DeploymentId, ct);

            var now = clock.GetUtcNow().UtcDateTime;

            // Swept here rather than by a worker: this table turns over once per
            // launch, the rows are worthless the moment they expire, and a
            // background service for it would be more machinery than the problem.
            await db.LaunchStates
                .Where(s => s.ExpiresAt < now)
                .ExecuteDeleteAsync(ct);

            var launch = new LaunchState
            {
                State = Opaque(),
                Nonce = Opaque(),
                PlatformId = platform.Id,
                TargetLinkUri = initiation.TargetLinkUri,
                ExpiresAt = now + LaunchWindow,
            };
            db.LaunchStates.Add(launch);
            await db.SaveChangesAsync(ct);

            var query = new Dictionary<string, string?>
            {
                ["scope"] = "openid",
                ["response_type"] = "id_token",
                // The token arrives as a form POST rather than in a fragment,
                // which is what lets it be validated on the server at all.
                ["response_mode"] = "form_post",
                ["client_id"] = platform.ClientId,
                ["redirect_uri"] = redirectUri,
                ["login_hint"] = initiation.LoginHint,
                ["lti_message_hint"] = initiation.MessageHint,
                ["state"] = launch.State,
                ["nonce"] = launch.Nonce,
                // The person is already signed in at the platform — they are
                // clicking a link inside it — so a login screen here would be the
                // platform asking them to sign in to itself.
                ["prompt"] = "none",
            };

            var parts = query
                .Where(pair => !string.IsNullOrEmpty(pair.Value))
                .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");

            var separator = platform.AuthLoginUrl.Contains('?') ? '&' : '?';
            return platform.AuthLoginUrl + separator + string.Join('&', parts);
        }

        public async Task<LaunchedMessage> CompleteAsync(
            string? state, string? idToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(idToken))
            {
                throw new LtiLaunchException(LtiLaunchException.BadState,
                    "The launch arrived without a state or without a token");
            }

            var now = clock.GetUtcNow().UtcDateTime;

            var launch = await db.LaunchStates.AsNoTracking()
                .FirstOrDefaultAsync(s => s.State == state && s.ExpiresAt >= now, ct);

            // **Consumed by a delete, not by a flag**, and the delete is what
            // decides the race. Two arrivals of the same token both read the row
            // above; exactly one of them removes it, and the other is told the
            // state is unusable. A `used` flag would let both read `false` and
            // both proceed, which is a replay that works.
            //
            // Read before deleting because `ExecuteDelete` returns a count and
            // not the row — the nonce below has to come from somewhere.
            var consumed = launch is null
                ? 0
                : await db.LaunchStates.Where(s => s.Id == launch.Id).ExecuteDeleteAsync(ct);

            if (consumed == 0)
            {
                // Unknown, expired and replayed are one answer on purpose: which
                // of the three an attacker hit is free information.
                throw new LtiLaunchException(LtiLaunchException.BadState,
                    "The launch state was not recognised, had expired, or had already been used");
            }

            var handler = new JsonWebTokenHandler();
            var unvalidated = handler.ReadJsonWebToken(idToken);
            var platform = await MatchAsync(unvalidated, ct);

            var signing = await keys.GetAsync(platform, unvalidated.Kid, ct);

            var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = platform.Issuer,
                ValidAudience = platform.ClientId,
                IssuerSigningKeys = signing,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                // The specification's own bound. Larger only hides a platform
                // whose clock is wrong, which is worth finding out about.
                ClockSkew = TimeSpan.FromMinutes(2),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            });

            if (!result.IsValid)
            {
                throw new LtiLaunchException(LtiLaunchException.BadToken,
                    result.Exception?.Message ?? "The launch token did not validate");
            }

            var token = (JsonWebToken)result.SecurityToken;

            // Ordinary comparison, deliberately. The nonce arrives inside a token
            // whose signature has just been checked against the platform's key,
            // so an attacker able to influence this string already holds the
            // platform's private key — a timing side channel is not what stands
            // between them and the account.
            if (!string.Equals(Claim(token, "nonce"), launch!.Nonce, StringComparison.Ordinal))
            {
                throw new LtiLaunchException(LtiLaunchException.BadState,
                    "The token's nonce did not match the one this launch was started with");
            }

            // The platform the token names has to be the one the login initiation
            // was started for. Without this a launch begun against one deployment
            // could be finished with another's token — both validly signed, and
            // the state would have been spent on the wrong course.
            if (launch.PlatformId != platform.Id)
            {
                throw new LtiLaunchException(LtiLaunchException.BadState,
                    "The token came from a different platform than the one this launch was started with");
            }

            return Read(token, platform);
        }

        /// <summary>
        /// Which platform this token claims to be from — <b>read before
        /// validation, and trusted for nothing but choosing the keys.</b> The
        /// signature is then checked against that platform's key set, so a token
        /// naming somebody else's deployment simply fails to validate.
        /// </summary>
        private async Task<Platform> MatchAsync(JsonWebToken token, CancellationToken ct)
        {
            var audience = token.Audiences.FirstOrDefault();
            var deployment = Claim(token, LtiClaims.DeploymentId);
            return await ResolveAsync(token.Issuer, audience, deployment, ct);
        }

        private async Task<Platform> ResolveAsync(
            string issuer, string? clientId, string? deploymentId, CancellationToken ct)
        {
            var candidates = await db.Platforms
                .Where(p => p.Issuer == issuer)
                .ToListAsync(ct);

            if (clientId is not null)
            {
                candidates = candidates.Where(p => p.ClientId == clientId).ToList();
            }
            if (deploymentId is not null)
            {
                candidates = candidates.Where(p => p.DeploymentId == deploymentId).ToList();
            }

            // With one registration per issuer the hints are optional, which is
            // what makes a login initiation that omits `client_id` work. With
            // several, an ambiguous initiation is refused rather than guessed:
            // guessing would send somebody into another faculty's course.
            var platform = candidates.Count == 1 ? candidates[0] : null;
            if (platform is null)
            {
                throw new LtiLaunchException(LtiLaunchException.UnknownPlatform,
                    candidates.Count == 0
                        ? $"No platform is registered for issuer {issuer}"
                        : $"{candidates.Count} platforms match issuer {issuer}; the launch named no deployment");
            }

            if (!platform.Enabled)
            {
                throw new LtiLaunchException(LtiLaunchException.PlatformDisabled,
                    $"The platform \"{platform.DisplayName}\" is registered and switched off");
            }

            return platform;
        }

        private static LaunchedMessage Read(JsonWebToken token, Platform platform)
        {
            if (Claim(token, LtiClaims.MessageType) != LtiClaims.ResourceLinkRequest)
            {
                throw new LtiLaunchException(LtiLaunchException.UnsupportedMessage,
                    $"This tool accepts {LtiClaims.ResourceLinkRequest} and nothing else in this version");
            }
            if (Claim(token, LtiClaims.Version) != LtiClaims.SupportedVersion)
            {
                throw new LtiLaunchException(LtiLaunchException.UnsupportedMessage,
                    "This tool implements LTI 1.3 and the launch declared another version");
            }

            var resourceLink = Object(token, LtiClaims.ResourceLink);
            var context = Object(token, LtiClaims.Context);
            var custom = Object(token, LtiClaims.Custom);
            var presentation = Object(token, LtiClaims.LaunchPresentation);

            var resourceLinkId = String(resourceLink, "id")
                ?? throw new LtiLaunchException(LtiLaunchException.UnsupportedMessage,
                    "The launch carried no resource link id");

            return new LaunchedMessage
            {
                Platform = platform,
                Subject = token.Subject
                    ?? throw new LtiLaunchException(LtiLaunchException.BadToken,
                        "The launch token carried no subject"),
                ResourceLinkId = resourceLinkId,
                ResourceLinkTitle = String(resourceLink, "title"),
                // A context is optional in the specification; a launch without one
                // is a link outside any course, which this module has nowhere to
                // put. Named rather than defaulted to empty.
                ContextId = String(context, "id")
                    ?? throw new LtiLaunchException(LtiLaunchException.UnsupportedMessage,
                        "The launch carried no context, so it belongs to no course"),
                ContextTitle = String(context, "title"),
                ContextHistory = String(custom, "context_history"),
                Roles = Strings(token, LtiClaims.Roles),
                ActivitySlug = String(custom, "activity"),
                AssertedUsername = String(custom, platform.UsernameClaim),
                Locale = String(presentation, "locale"),
                DocumentTarget = String(presentation, "document_target"),
                ReturnUrl = String(presentation, "return_url"),
                AgsEndpointJson = Raw(token, LtiClaims.AgsEndpoint),
                NrpsJson = Raw(token, LtiClaims.NrpsService),
            };
        }

        /// <summary>128 bits of randomness, URL-safe. Nothing is derived from it.</summary>
        private static string Opaque() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private static string? Claim(JsonWebToken token, string name) =>
            token.TryGetClaim(name, out var claim) ? claim.Value : null;

        /// <summary>
        /// A claim as the JSON it actually is.
        /// <para>
        /// <b>Read from the payload rather than through <c>TryGetClaim</c></b>,
        /// which flattens: for an array claim it hands back the first element as
        /// a bare string, so parsing it as JSON fails and the whole claim reads
        /// as absent. That is not theoretical — it is how the roles claim
        /// silently became empty, and every launch arrived as a learner
        /// regardless of what the platform said. Caught by the instructor test
        /// and not by reasoning about it.
        /// </para>
        /// </summary>
        private static JsonElement? Payload(JsonWebToken token, string name) =>
            token.TryGetPayloadValue<JsonElement>(name, out var value) ? value : null;

        private static string? Raw(JsonWebToken token, string name) =>
            Payload(token, name)?.GetRawText();

        private static JsonElement? Object(JsonWebToken token, string name)
        {
            var value = Payload(token, name);
            return value?.ValueKind == JsonValueKind.Object ? value : null;
        }

        private static string? String(JsonElement? element, string property) =>
            element?.ValueKind == JsonValueKind.Object
            && element.Value.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static IReadOnlyList<string> Strings(JsonWebToken token, string name)
        {
            var value = Payload(token, name);
            return value?.ValueKind switch
            {
                JsonValueKind.Array => value.Value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
                    .ToList(),
                // A single role sent as a bare string rather than a list of one.
                // The specification says array; platforms are platforms.
                JsonValueKind.String => [value.Value.GetString()!],
                _ => [],
            };
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using AlgoJudge.Server.Lti;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A platform, for tests: its own key pair, and launches signed with it.
/// <para>
/// <b>It signs real tokens with a real key.</b> The alternative — stubbing the
/// validation out — would leave the one part of a launch that is security rather
/// than plumbing untested, and that part is the whole reason LTI is cheap to
/// adopt: the specification supplies the security model.
/// </para>
/// <para>
/// What it does <i>not</i> do is serve a JWKS over HTTP.
/// <see cref="Keys"/> replaces <see cref="IPlatformKeys"/> in the test host, so
/// the suite does not need a listening socket for something the module already
/// isolates behind an interface.
/// </para>
/// </summary>
public sealed class FakePlatform : IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);

    public string Kid { get; } = Guid.NewGuid().ToString("N");
    public string Issuer { get; }
    public string ClientId { get; } = Guid.NewGuid().ToString("N");
    public string DeploymentId { get; } = Guid.NewGuid().ToString("N");

    public FakePlatform(string? issuer = null) =>
        Issuer = issuer ?? "https://moodle-" + Guid.NewGuid().ToString("N")[..8] + ".invalid";

    /// <summary>What a registration request for this platform looks like.</summary>
    public object Registration(bool isIdentityAuthority = false, string? identityNamespace = null) => new
    {
        displayName = "Fake " + Issuer,
        issuer = Issuer,
        clientId = ClientId,
        deploymentId = DeploymentId,
        keySetUrl = Issuer + "/mod/lti/certs.php",
        authTokenUrl = Issuer + "/mod/lti/token.php",
        authLoginUrl = Issuer + "/mod/lti/auth.php",
        isIdentityAuthority,
        identityNamespace = identityNamespace ?? (isIdentityAuthority ? "algojudge.invalid" : null),
    };

    /// <summary>The signing key, as the module would have fetched it.</summary>
    public SecurityKey SigningKey =>
        new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = Kid };

    /// <summary>
    /// An <c>id_token</c> for a resource-link launch. Every part a real one
    /// carries is here, so a test can take one away and see what breaks.
    /// </summary>
    public string IdToken(
        string nonce,
        string? subject = null,
        string? resourceLinkId = null,
        string? contextId = null,
        string? activitySlug = "activity-slug",
        string? username = "jkowalski",
        string? locale = "pl",
        string[]? roles = null,
        string? messageType = LtiClaims.ResourceLinkRequest,
        string? version = LtiClaims.SupportedVersion,
        string? audience = null,
        string? issuer = null,
        string? deploymentId = null,
        DateTime? expires = null)
    {
        var custom = new Dictionary<string, object>();
        if (activitySlug is not null) custom["activity"] = activitySlug;
        if (username is not null) custom["username"] = username;

        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject ?? "moodle-user-1",
            ["nonce"] = nonce,
            [LtiClaims.MessageType] = messageType ?? "",
            [LtiClaims.Version] = version ?? "",
            [LtiClaims.DeploymentId] = deploymentId ?? DeploymentId,
            [LtiClaims.ResourceLink] = new Dictionary<string, object>
            {
                ["id"] = resourceLinkId ?? "rl-1",
                ["title"] = "Laboratorium 1",
            },
            [LtiClaims.Context] = new Dictionary<string, object>
            {
                ["id"] = contextId ?? "course-1",
                ["title"] = "Algorytmy i struktury danych",
            },
            [LtiClaims.Roles] = roles ?? [LtiRoles.Learner],
            [LtiClaims.Custom] = custom,
            [LtiClaims.LaunchPresentation] = new Dictionary<string, object>
            {
                ["document_target"] = "iframe",
                ["locale"] = locale ?? "en",
                ["return_url"] = Issuer + "/mod/lti/return.php",
            },
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? ClientId,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(rsa) { KeyId = Kid }, SecurityAlgorithms.RsaSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// The claim value for a nested object, for a test that wants to see what
    /// the module read.
    /// </summary>
    public static JsonElement Claim(string idToken, string name)
    {
        var token = new JsonWebTokenHandler().ReadJsonWebToken(idToken);
        token.TryGetClaim(name, out var claim);
        return JsonDocument.Parse(claim.Value).RootElement.Clone();
    }

    public void Dispose() => rsa.Dispose();
}

/// <summary>
/// The key set the module would have fetched, without a socket to fetch it from.
/// </summary>
public sealed class StubbedPlatformKeys(params SecurityKey[] keys) : IPlatformKeys
{
    public Task<IReadOnlyCollection<SecurityKey>> GetAsync(
        Platform platform, string? kid, CancellationToken ct) =>
        Task.FromResult<IReadOnlyCollection<SecurityKey>>(keys);
}

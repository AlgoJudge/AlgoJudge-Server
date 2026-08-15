using System.Net;
using System.Text;
using System.Text.Json;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A platform offering itself for dynamic registration.
///
/// <para>
/// Shaped from Moodle's own answer, measured on 4.5.13, 5.2.2 and 5.3dev
/// (2026-08-15): the same eight scopes, the same two message types, and
/// `User.username` among forty substitution variables. What it accepts back is
/// shaped from `mod/lti/openid-registration.php`.
/// </para>
/// </summary>
public sealed class FakePlatformRegistry : HttpMessageHandler
{
    public const string Issuer = "https://platform.invalid";
    public const string ConfigurationUrl = Issuer + "/mod/lti/openid-configuration.php";
    public const string RegistrationUrl = Issuer + "/mod/lti/openid-registration.php";

    /// <summary>Every registration body that arrived, so a test can read what was asked for.</summary>
    public List<string> Registered { get; } = [];

    /// <summary>The bearer token each registration carried, if any.</summary>
    public List<string?> Tokens { get; } = [];

    /// <summary>What the platform will say when asked to register the tool.</summary>
    public HttpStatusCode RegistrationStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>Leave null to answer with a client id; set to answer without one.</summary>
    public string? ClientId { get; set; } = "dynamic-client-1";

    /// <summary>
    /// The deployment id the platform names this installation of the tool by.
    /// Moodle answers with the tool type's id as a string — measured in
    /// `registration_helper.php` on 4.5.13 and 5.2.2 — inside the tool
    /// configuration claim, not at the top level. Set to null to answer without.
    /// </summary>
    public string? DeploymentId { get; set; } = "7";

    /// <summary>Fields to leave out of the configuration, for the refusals.</summary>
    public HashSet<string> Omit { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        if (url == ConfigurationUrl)
        {
            var configuration = new Dictionary<string, object?>
            {
                ["issuer"] = Issuer,
                ["token_endpoint"] = Issuer + "/mod/lti/token.php",
                ["authorization_endpoint"] = Issuer + "/mod/lti/auth.php",
                ["jwks_uri"] = Issuer + "/mod/lti/certs.php",
                ["registration_endpoint"] = RegistrationUrl,
                ["scopes_supported"] = new[]
                {
                    "https://purl.imsglobal.org/spec/lti-ags/scope/lineitem",
                    "https://purl.imsglobal.org/spec/lti-ags/scope/score",
                    "https://purl.imsglobal.org/spec/lti-nrps/scope/contextmembership.readonly",
                },
                ["https://purl.imsglobal.org/spec/lti-platform-configuration"] =
                    new Dictionary<string, object?>
                    {
                        ["product_family_code"] = "moodle",
                        ["version"] = "5.2.2 (Build: 20260810)",
                        ["variables"] = new[] { "User.username", "Context.id.history" },
                    },
            };

            foreach (var field in Omit) configuration.Remove(field);

            return Json(JsonSerializer.Serialize(configuration));
        }

        if (url == RegistrationUrl)
        {
            Registered.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Tokens.Add(request.Headers.Authorization?.Parameter);

            if (RegistrationStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(RegistrationStatus)
                {
                    Content = new StringContent("""{"error":"invalid_client_metadata"}"""),
                };
            }

            var tool = new Dictionary<string, object?> { ["version"] = "1.3.0" };
            if (DeploymentId is not null) tool["deployment_id"] = DeploymentId;

            var answer = new Dictionary<string, object?>
            {
                ["client_name"] = "AlgoJudge",
                ["https://purl.imsglobal.org/spec/lti-tool-configuration"] = tool,
            };
            if (ClientId is not null) answer["client_id"] = ClientId;
            return Json(JsonSerializer.Serialize(answer));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

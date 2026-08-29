using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The tool's key, and registering a platform by hand.
/// <para>
/// Two things here are security properties rather than features, and each has a
/// test that fails if it stops being true: <b>the private key never leaves the
/// Server</b> (§9), and <b>a platform trusted to assert identity must name the
/// namespace it is trusted within</b> (§4.5).
/// </para>
/// </summary>
[Collection("server-3")]
public class LtiRegistrationTests(ServerFixture server)
{
    [Fact]
    public async Task The_key_set_is_public_and_carries_a_usable_key()
    {
        // Anonymous on purpose: a platform fetches this before any trust exists.
        var anonymous = server.CreateClient();
        var response = await anonymous.GetAsync("/api/v1/lti/jwks.json");

        // The body goes into the message rather than being read only on success:
        // a bare "expected OK, got InternalServerError" sends whoever sees it
        // looking through logs for something the response already said.
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = body.GetProperty("keys").EnumerateArray().ToArray();

        Assert.NotEmpty(keys);
        var key = keys[0];
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.Equal("sig", key.GetProperty("use").GetString());
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.False(string.IsNullOrWhiteSpace(key.GetProperty("kid").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(key.GetProperty("n").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(key.GetProperty("e").GetString()));
    }

    /// <summary>
    /// <b>The one that matters.</b> §9 of <c>LMS_INTEGRATION.md</c> makes "the
    /// private key is generated in the Server and never leaves it" an approved
    /// decision, and a decision nothing checks is a comment.
    /// <para>
    /// It scans every answer a platform-registration flow produces, rather than
    /// the key set alone, because the way a private key escapes is never the
    /// endpoint whose job is to publish keys — it is a projection somebody added
    /// a field to.
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_answer_anywhere_carries_the_private_key()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var platform = await RegisterAsync(admin, "Disclosure check");
        var id = platform.GetProperty("id").GetString();

        var anonymous = server.CreateClient();

        foreach (var (client, path) in new[]
        {
            (anonymous, "/api/v1/lti/jwks.json"),
            (admin, "/api/v1/lti/platforms"),
            (admin, $"/api/v1/lti/platforms/{id}"),
            (admin, $"/api/v1/lti/platforms/{id}/registration"),
            (admin, "/api/v1/identity/providers"),
        })
        {
            var body = await (await client.GetAsync(path)).Content.ReadAsStringAsync();

            // The PEM header is the thing itself. Checked as a substring rather
            // than by parsing, because a leak would not arrive as a well-formed
            // field with a helpful name.
            Assert.DoesNotContain("PRIVATE KEY", body, StringComparison.Ordinal);
            Assert.DoesNotContain("privatePem", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// §4.5: the flag means "this platform may say who somebody is, <i>inside
    /// this suffix</i>". Without the suffix it means the platform may claim any
    /// account in the installation, which is the takeover shape the section
    /// exists to bound.
    /// </summary>
    [Fact]
    public async Task Identity_authority_without_a_namespace_is_refused()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var response = await admin.PostAsJsonAsync("/api/v1/lti/platforms", Body(
            "Authority with no bound", isIdentityAuthority: true, identityNamespace: null));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("identityNamespace", problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A platform is not a way in. Its provider row exists so a course grant has
    /// a source; it must never appear as something to sign in with, or the
    /// installation grows a door with no lock behind it.
    /// </summary>
    [Fact]
    public async Task A_platforms_provider_row_is_not_a_way_to_sign_in()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var platform = await RegisterAsync(admin, "Not a sign-in");
        var providerId = platform.GetProperty("providerId").GetString();

        var providers = await (await admin.GetAsync("/api/v1/identity/providers"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var row = providers.EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == providerId);

        Assert.False(row.GetProperty("enabled").GetBoolean());
        Assert.False(row.GetProperty("hasClientSecret").GetBoolean());

        // And the list the sign-in screen is built from does not offer it.
        //
        // Compared by **slug**, because that answer carries a slug and a display
        // name and no id at all — asserting the id is absent would pass whatever
        // the code did, which is a test that reads like one and is not.
        var slug = row.GetProperty("slug").GetString();
        var anonymous = server.CreateClient();
        var instance = await (await anonymous.GetAsync("/api/v1/instance"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var offered = instance.GetProperty("providers").EnumerateArray()
            .Select(p => p.GetProperty("slug").GetString())
            .ToArray();

        Assert.DoesNotContain(slug, offered);
    }

    /// <summary>
    /// The issuer, client and deployment are the key an <c>id_token</c> is
    /// matched against and the key every link hangs off. Editing one repoints
    /// everybody the platform ever launched, silently.
    /// </summary>
    [Fact]
    public async Task The_platform_key_cannot_be_edited()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var platform = await RegisterAsync(admin, "Immutable key");
        var id = platform.GetProperty("id").GetString();

        // Everything as registered except the issuer, so the refusal is about
        // the issuer and not about three fields differing at once.
        var moved = new
        {
            displayName = platform.GetProperty("displayName").GetString(),
            issuer = "https://somewhere.else.invalid",
            clientId = platform.GetProperty("clientId").GetString(),
            deploymentId = platform.GetProperty("deploymentId").GetString(),
            keySetUrl = platform.GetProperty("keySetUrl").GetString(),
            authTokenUrl = platform.GetProperty("authTokenUrl").GetString(),
            authLoginUrl = platform.GetProperty("authLoginUrl").GetString(),
            isIdentityAuthority = false,
            identityNamespace = platform.GetProperty("identityNamespace").GetString(),
        };
        var response = await admin.PutAsJsonAsync($"/api/v1/lti/platforms/{id}", moved);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // **The control.** A rename with the key left alone succeeds, so the
        // refusal above is about the issuer and not about the endpoint refusing
        // every update it is given.
        var renamed = await admin.PutAsJsonAsync($"/api/v1/lti/platforms/{id}",
            moved with { issuer = platform.GetProperty("issuer").GetString()!, displayName = "Renamed" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var after = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Renamed", after.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Registering_the_same_deployment_twice_is_refused()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var body = Body("Twice");

        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsJsonAsync("/api/v1/lti/platforms", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/v1/lti/platforms", body)).StatusCode);
    }

    [Fact]
    public async Task Registration_tells_an_operator_to_send_the_username()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var platform = await RegisterAsync(admin, "What to type in");
        var id = platform.GetProperty("id").GetString();

        var registration = await (await admin.GetAsync($"/api/v1/lti/platforms/{id}/registration"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var custom = registration.GetProperty("customParameters")
            .EnumerateArray().Select(v => v.GetString()).ToArray();

        // Without this the launch cannot resolve who launched, and lands on the
        // sign-in page — which reads as a broken tool rather than as a missing
        // parameter.
        Assert.Contains("username=$User.username", custom);
        Assert.EndsWith("/lti/jwks.json", registration.GetProperty("keySetUrl").GetString());
    }

    private static async Task<JsonElement> RegisterAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/lti/platforms", Body(name));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// A distinct deployment per call, so tests sharing a database do not
    /// collide on the uniqueness this module enforces.
    /// </summary>
    private static object Body(
        string displayName,
        string? issuer = null,
        bool isIdentityAuthority = false,
        string? identityNamespace = "algojudge.invalid") => new
        {
            displayName,
            issuer = issuer ?? "https://moodle.algojudge.invalid",
            clientId = Guid.NewGuid().ToString("N"),
            deploymentId = Guid.NewGuid().ToString("N"),
            keySetUrl = "https://moodle.algojudge.invalid/mod/lti/certs.php",
            authTokenUrl = "https://moodle.algojudge.invalid/mod/lti/token.php",
            authLoginUrl = "https://moodle.algojudge.invalid/mod/lti/auth.php",
            isIdentityAuthority,
            identityNamespace,
        };
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The launch: the login initiation, and what a signed <c>id_token</c> is
/// allowed to do.
/// <para>
/// <b>The state and nonce live in this Server</b> because Moodle implements no
/// LTI Platform Storage — measured 2026-08-13 against three versions. That makes
/// these tests the only thing standing between a launch and a replay, so they
/// are about refusals more than about the happy path.
/// </para>
/// </summary>
[Collection("server")]
public class LtiLaunchTests(ServerFixture server)
{
    /// <summary>
    /// A host whose platform key set is the fake platform's, with everything
    /// else exactly as the product runs it.
    /// </summary>
    private WebApplicationFactory<Program> HostFor(FakePlatform platform) =>
        server.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPlatformKeys>(
                new StubbedPlatformKeys(platform.SigningKey)))));

    [Fact]
    public async Task A_login_initiation_sends_the_browser_back_to_the_platform()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);

        using var host = HostFor(platform);
        var response = await NoRedirects(host).PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["iss"] = platform.Issuer,
                ["login_hint"] = "42",
                ["target_link_uri"] = "https://algojudge.invalid/lti/launch",
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var target = response.Headers.Location!;
        Assert.StartsWith(platform.Issuer + "/mod/lti/auth.php", target.ToString());

        var query = HttpUtility.ParseQueryString(target.Query);
        Assert.Equal("openid", query["scope"]);
        Assert.Equal("id_token", query["response_type"]);
        // Without form_post the token arrives in a fragment, which the Server
        // never sees — the launch would look like it worked and do nothing.
        Assert.Equal("form_post", query["response_mode"]);
        Assert.Equal("none", query["prompt"]);
        Assert.Equal(platform.ClientId, query["client_id"]);
        Assert.Equal("42", query["login_hint"]);
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));
        Assert.False(string.IsNullOrWhiteSpace(query["nonce"]));
    }

    /// <summary>
    /// A launch that validates but resolves to nobody — this platform is not an
    /// identity authority — offers the one action §4.4 allows, and <b>keeps
    /// where the launch was going</b>. Signing in and then landing on a front
    /// page would make the person find their way back by hand, which is the
    /// difference between one click and giving up.
    /// </summary>
    [Fact]
    public async Task A_launch_with_nobody_to_resolve_offers_sign_in_and_keeps_the_destination()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        // A launch has to name an activity that exists, so this test needs one:
        // resolving the placement is the first thing a validated launch does.
        var (slug, _) = await Build.ActivityAsync(server);
        using var host = HostFor(platform);

        var (state, nonce) = await BeginAsync(host, platform);
        var response = await LaunchAsync(host, state, platform.IdToken(nonce, activitySlug: slug));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var landing = response.Headers.Location!.ToString();

        Assert.Contains("/lti/sign-in", landing);

        // Parsed off the string rather than through `Uri`: with `App:BaseUrl`
        // unset — a single-origin deployment, and every test — the redirect is a
        // relative path, and `Uri.Query` throws on one of those.
        var query = HttpUtility.ParseQueryString(landing[(landing.IndexOf('?') + 1)..]);
        var destination = query["returnTo"];

        Assert.NotNull(destination);
        Assert.StartsWith("/lti/launched", destination);
        // The placement is already resolved and bound before anybody signs in,
        // so coming back is a redirect rather than a second launch.
        Assert.Contains("link=", destination);
        // And no ticket, because nothing was established: a ticket is proof that
        // a launch resolved to somebody, and nobody was resolved here.
        Assert.DoesNotContain("ticket=", destination);
    }

    /// <summary>
    /// <b>The one that matters most here.</b> A launch replayed is a launch
    /// somebody captured; the state is spent on first use and the second attempt
    /// gets the same answer as an unknown one.
    /// </summary>
    [Fact]
    public async Task A_launch_cannot_be_replayed()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        using var host = HostFor(platform);

        var (slug, _) = await Build.ActivityAsync(server);
        var (state, nonce) = await BeginAsync(host, platform);
        var token = platform.IdToken(nonce, activitySlug: slug);

        Assert.DoesNotContain("failed", (await LaunchAsync(host, state, token))
            .Headers.Location!.ToString());

        var again = await LaunchAsync(host, state, token);
        Assert.Contains("reason=" + LtiLaunchException.BadState, again.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_token_signed_by_somebody_else_is_refused()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        using var host = HostFor(platform);

        var (state, nonce) = await BeginAsync(host, platform);

        // Same issuer, same client, same deployment — a different key. This is
        // exactly what an attacker with a stolen launch URL can produce.
        using var impostor = new FakePlatform(platform.Issuer);
        var forged = impostor.IdToken(nonce, audience: platform.ClientId,
            issuer: platform.Issuer, deploymentId: platform.DeploymentId);

        var response = await LaunchAsync(host, state, forged);
        Assert.Contains("reason=" + LtiLaunchException.BadToken,
            response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_token_whose_nonce_is_not_the_one_we_issued_is_refused()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        using var host = HostFor(platform);

        var (state, _) = await BeginAsync(host, platform);

        var response = await LaunchAsync(host, state, platform.IdToken("a-nonce-we-never-issued"));
        Assert.Contains("reason=" + LtiLaunchException.BadState,
            response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        using var host = HostFor(platform);

        var (state, nonce) = await BeginAsync(host, platform);
        var stale = platform.IdToken(nonce, expires: DateTime.UtcNow.AddMinutes(-30));

        var response = await LaunchAsync(host, state, stale);
        Assert.Contains("reason=" + LtiLaunchException.BadToken,
            response.Headers.Location!.ToString());
    }

    /// <summary>
    /// Named for what it actually checks. A token addressed to another tool
    /// never reaches the audience validation in the token handler: the audience
    /// <b>is</b> the client id, so it is what the platform is looked up by, and
    /// an unknown one resolves to nothing. The handler's own
    /// <c>ValidateAudience</c> stays on as a second line behind this one.
    /// </summary>
    [Fact]
    public async Task A_token_addressed_to_another_tool_resolves_to_no_platform()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        using var host = HostFor(platform);

        var (state, nonce) = await BeginAsync(host, platform);
        var elsewhere = platform.IdToken(nonce, audience: "some-other-tool");

        var response = await LaunchAsync(host, state, elsewhere);
        Assert.Contains("reason=" + LtiLaunchException.UnknownPlatform,
            response.Headers.Location!.ToString());
    }

    /// <summary>
    /// A launch begun for one platform cannot be finished with another's token,
    /// even though both are registered and both tokens are validly signed.
    /// <para>
    /// Without this the `state` — which is what says "this browser started a
    /// launch" — could be spent on a course at a different institution.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_state_begun_for_one_platform_cannot_be_finished_by_another()
    {
        using var first = new FakePlatform();
        using var second = new FakePlatform();
        await RegisterAsync(first);
        await RegisterAsync(second);

        // Both platforms' keys are known to this host, so the second token
        // validates on its own terms — which is exactly the case worth refusing.
        using var host = server.WithWebHostBuilder(builder => builder.ConfigureTestServices(
            services => services.Replace(ServiceDescriptor.Singleton<IPlatformKeys>(
                new StubbedPlatformKeys(first.SigningKey, second.SigningKey)))));

        var (state, nonce) = await BeginAsync(host, first);
        var response = await LaunchAsync(host, state, second.IdToken(nonce));

        Assert.Contains("reason=" + LtiLaunchException.BadState,
            response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_message_this_tool_does_not_implement_is_refused_by_name()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        using var host = HostFor(platform);

        var (state, nonce) = await BeginAsync(host, platform);
        var deepLink = platform.IdToken(nonce, messageType: "LtiDeepLinkingRequest");

        var response = await LaunchAsync(host, state, deepLink);
        Assert.Contains("reason=" + LtiLaunchException.UnsupportedMessage,
            response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_launch_at_a_platform_nobody_registered_is_refused()
    {
        using var platform = new FakePlatform();
        // Deliberately not registered.
        using var host = HostFor(platform);

        var response = await NoRedirects(host).PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["iss"] = platform.Issuer }));

        Assert.Contains("reason=" + LtiLaunchException.UnknownPlatform,
            response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_disabled_platform_cannot_launch()
    {
        using var platform = new FakePlatform();
        var registered = await RegisterAsync(platform);
        var id = registered.GetProperty("id").GetString();

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(platform.Registration()))!;
        body["enabled"] = JsonDocument.Parse("false").RootElement;
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/api/v1/lti/platforms/{id}", body)).StatusCode);

        using var host = HostFor(platform);
        var response = await NoRedirects(host).PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["iss"] = platform.Issuer }));

        Assert.Contains("reason=" + LtiLaunchException.PlatformDisabled,
            response.Headers.Location!.ToString());
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private async Task<JsonElement> RegisterAsync(FakePlatform platform)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var response = await admin.PostAsJsonAsync("/api/v1/lti/platforms", platform.Registration());
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Runs a real login initiation and reads back the `state` and `nonce` the
    /// Server issued — rather than writing rows by hand, so the two halves are
    /// tested against each other.
    /// </summary>
    private static async Task<(string State, string Nonce)> BeginAsync(
        WebApplicationFactory<Program> host, FakePlatform platform)
    {
        var response = await NoRedirects(host).PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["iss"] = platform.Issuer }));

        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        return (query["state"]!, query["nonce"]!);
    }

    private static Task<HttpResponseMessage> LaunchAsync(
        WebApplicationFactory<Program> host, string state, string idToken) =>
        NoRedirects(host).PostAsync("/api/v1/lti/launch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = state,
                ["id_token"] = idToken,
            }));

    /// <summary>
    /// A client that does not follow redirects, because the redirect <b>is</b>
    /// the answer here — following it would land on the Client's origin, which
    /// this Server does not serve.
    /// </summary>
    private static HttpClient NoRedirects(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            // Over TLS: a launch's session cookie is `Secure`.
            BaseAddress = new Uri("https://localhost"),
        });
}

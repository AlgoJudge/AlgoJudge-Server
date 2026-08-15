using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A platform registering itself, and the several ways that is refused.
///
/// <para>
/// <b>Dynamic Registration is the one flow this tool cannot authenticate.</b> The
/// browser belongs to whoever administers the platform, the token was minted by
/// the platform, and nothing in it proves who they are. Everything here is about
/// what that costs: an invitation somebody here created, a platform that arrives
/// switched off, and a flag it can never set for itself.
/// </para>
/// </summary>
[Collection("server")]
public class LtiDynamicRegistrationTests(ServerFixture server)
{
    [Fact]
    public async Task A_platform_registers_itself_and_arrives_switched_off()
    {
        var (host, registry, manager) = await BuildAsync();

        var invitation = await InviteAsync(manager, "WMiI Moodle");
        var page = await RegisterAsync(host, invitation);

        // Printed rather than asserted plainly: this page carries the reason a
        // registration was refused, and a truncated assert message cost an hour.
        Assert.True(page.Contains("registered"), page);
        // The page has to tell the platform's own screen to close, or the iframe
        // sits there with a finished registration behind it.
        Assert.Contains("org.imsglobal.lti.close", page);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();
        var platform = await db.Platforms.FirstAsync(p => p.Issuer == FakePlatformRegistry.Issuer);

        // **Both of these, and neither is a default anybody may change remotely.**
        Assert.False(platform.Enabled);
        Assert.False(platform.IsIdentityAuthority);
        Assert.Null(platform.IdentityNamespace);
        Assert.Equal("dynamic-client-1", platform.ClientId);

        // **Both halves of what identifies a launch**, and the second one only
        // ever comes back with the platform's answer.
        Assert.Equal("7", platform.DeploymentId);
    }

    /// <summary>
    /// A deployment id is how a launch is matched back to this registration, so
    /// an answer without one produces a row that cannot serve its purpose. Moodle
    /// always sends it; a platform that does not is refused rather than stored
    /// half-formed.
    /// </summary>
    [Fact]
    public async Task A_platform_that_names_no_deployment_is_not_stored()
    {
        var (host, registry, manager) = await BuildAsync();
        registry.DeploymentId = null;

        var page = await RegisterAsync(host, await InviteAsync(manager));

        Assert.DoesNotContain("registered", page);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();
        Assert.False(await db.Platforms.AnyAsync(p => p.Issuer == FakePlatformRegistry.Issuer));
    }

    /// <summary>
    /// <b>What gets registered is an address on this installation</b>, and an
    /// address without a scheme is not one. The refusal lands on the manager
    /// inviting, which is the only person who can fix it — a platform reaching a
    /// misconfigured tool would see either a 500 or, worse, a registration that
    /// completes and then fails at somebody's first launch.
    /// </summary>
    [Fact]
    public async Task An_installation_with_no_public_address_hands_out_no_invitation()
    {
        var host = server.WithWebHostBuilder(builder =>
            builder.UseSetting("PublicApiUrl", "moodle.invalid:8452"));
        var manager = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var refused = await manager.PostAsJsonAsync("/api/v1/lti/registrations", new { note = "n" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        // Naming the setting, because the person reading this is the person who
        // can set it.
        Assert.Contains("PublicApiUrl", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The registration says what the tool needs, rather than leaving it to be
    /// typed afterwards — a tool registered without the username parameter
    /// launches nobody, and the reason is invisible in the platform's screens.
    /// </summary>
    [Fact]
    public async Task The_registration_asks_for_the_parameter_identity_rests_on()
    {
        var (host, registry, manager) = await BuildAsync();

        await RegisterAsync(host, await InviteAsync(manager));

        var body = Assert.Single(registry.Registered);
        using var document = JsonDocument.Parse(body);
        var tool = document.RootElement
            .GetProperty("https://purl.imsglobal.org/spec/lti-tool-configuration");

        var custom = tool.GetProperty("custom_parameters");
        Assert.Equal("$User.username", custom.GetProperty("username").GetString());
        Assert.Equal("$Context.id.history", custom.GetProperty("context_history").GetString());

        // And it asks for the scopes it actually uses, not for everything on
        // offer: a tool holding a token that can post grades while reading a
        // roster asked for more than it needed.
        var scopes = document.RootElement.GetProperty("scope").GetString()!;
        Assert.Contains("lti-ags/scope/score", scopes);
        Assert.Contains("lti-nrps/scope/contextmembership.readonly", scopes);
        Assert.DoesNotContain("toolsetting", scopes);
        Assert.DoesNotContain("basicoutcome", scopes);
    }

    /// <summary>
    /// <b>A registration that offers only one message type is a tool half of
    /// which cannot be reached.</b> Moodle gives a dynamically registered tool
    /// content selection only if the registration asked for it, so a platform
    /// admitted this way could never use deep linking — found by registering
    /// against a real 5.2 rather than by reading the specification.
    /// </summary>
    [Fact]
    public async Task The_registration_offers_both_errands_this_tool_answers()
    {
        var (host, registry, manager) = await BuildAsync();

        await RegisterAsync(host, await InviteAsync(manager));

        using var document = JsonDocument.Parse(Assert.Single(registry.Registered));
        var messages = document.RootElement
            .GetProperty("https://purl.imsglobal.org/spec/lti-tool-configuration")
            .GetProperty("messages")
            .EnumerateArray()
            .Select(m => m.GetProperty("type").GetString())
            .ToList();

        Assert.Contains("LtiResourceLinkRequest", messages);
        Assert.Contains("LtiDeepLinkingRequest", messages);
    }

    [Fact]
    public async Task The_platforms_own_token_is_carried_back_to_it()
    {
        var (host, registry, manager) = await BuildAsync();

        await RegisterAsync(host, await InviteAsync(manager), token: "a-token-moodle-minted");

        Assert.Equal("a-token-moodle-minted", Assert.Single(registry.Tokens));
    }

    /// <summary>
    /// <b>The reason the endpoint is gated at all.</b> Without an invitation it
    /// is a way for anybody to fill the platform list, and a blind SSRF: it would
    /// fetch whatever URL a stranger put in the address.
    /// </summary>
    [Fact]
    public async Task Without_an_invitation_nothing_is_registered_and_nothing_is_fetched()
    {
        var (host, registry, manager) = await BuildAsync();

        // **A live invitation exists**, and this is still refused. Without one in
        // the table the refusal proves nothing: an empty table refuses everybody,
        // including a code that was never checked.
        await InviteAsync(manager);

        var page = await RegisterAsync(host, "not-a-real-code");

        // **Refused, and refused on purpose.** Asserting only that nothing was
        // stored cannot tell a refusal from a crash — and a crash satisfies every
        // other assertion here.
        Assert.Contains("That did not work", page);
        Assert.DoesNotContain("registered", page);
        Assert.Empty(registry.Registered);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();
        Assert.False(await db.Platforms.AnyAsync(p => p.Issuer == FakePlatformRegistry.Issuer));
    }

    [Fact]
    public async Task An_invitation_is_spent_once()
    {
        var (host, registry, manager) = await BuildAsync();
        var invitation = await InviteAsync(manager);

        var first = await RegisterAsync(host, invitation);
        Assert.Contains("registered", first);

        var second = await RegisterAsync(host, invitation);
        Assert.DoesNotContain("registered", second);
        Assert.Single(registry.Registered);
    }

    [Fact]
    public async Task A_revoked_invitation_admits_nobody()
    {
        var (host, registry, manager) = await BuildAsync();

        var created = await manager.PostAsJsonAsync("/api/v1/lti/registrations", new { note = "off" });
        var invitation = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = invitation.GetProperty("id").GetString();

        (await manager.PostAsync($"/api/v1/lti/registrations/{id}/revoke", null))
            .EnsureSuccessStatusCode();

        var page = await RegisterAsync(host, CodeOf(invitation));

        Assert.DoesNotContain("registered", page);
        Assert.Empty(registry.Registered);
    }

    /// <summary>
    /// A configuration missing an endpoint a launch needs is refused rather than
    /// stored — a platform row that cannot launch is worse than none, because it
    /// looks like one that can.
    /// </summary>
    [Fact]
    public async Task A_configuration_missing_what_a_launch_needs_is_refused()
    {
        var (host, registry, manager) = await BuildAsync();
        registry.Omit.Add("authorization_endpoint");

        var page = await RegisterAsync(host, await InviteAsync(manager));

        Assert.DoesNotContain("registered", page);
        Assert.Empty(registry.Registered);
    }

    [Fact]
    public async Task A_platform_that_returns_no_client_id_is_not_stored()
    {
        var (host, registry, manager) = await BuildAsync();
        registry.ClientId = null;

        var page = await RegisterAsync(host, await InviteAsync(manager));

        Assert.DoesNotContain("registered", page);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();
        Assert.False(await db.Platforms.AnyAsync(p => p.Issuer == FakePlatformRegistry.Issuer));
    }

    [Fact]
    public async Task Inviting_is_behind_the_permission_that_governs_providers()
    {
        var (_, _, _) = await BuildAsync();
        var participant = await Sign.InAsync(
            server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);

        var refused = await participant.PostAsJsonAsync("/api/v1/lti/registrations", new { });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private async Task<(WebApplicationFactory<Program> Host, FakePlatformRegistry Registry, HttpClient Manager)>
        BuildAsync()
    {
        var registry = new FakePlatformRegistry();

        var host = server.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(nameof(DynamicRegistrationService))
                    .ConfigurePrimaryHttpMessageHandler(() => registry)));

        // Against this host, not the fixture's: a client from the fixture is
        // served by a host where the fake platform is not wired in.
        var manager = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        // Each test gets its own issuer, because the fixture's database is shared
        // and a platform is unique on it.
        await ClearAsync(host);

        return (host, registry, manager);
    }

    private static async Task ClearAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();
        await db.Platforms.Where(p => p.Issuer == FakePlatformRegistry.Issuer).ExecuteDeleteAsync();
    }

    private static async Task<string> InviteAsync(HttpClient manager, string note = "a platform")
    {
        var response = await manager.PostAsJsonAsync("/api/v1/lti/registrations", new { note });
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return CodeOf(await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    /// <summary>The code out of the address, which is how it is handed over.</summary>
    private static string CodeOf(JsonElement invitation)
    {
        var url = invitation.GetProperty("registrationUrl").GetString()!;
        var at = url.IndexOf("code=", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no code in {url}");
        return url[(at + 5)..];
    }

    private static async Task<string> RegisterAsync(
        WebApplicationFactory<Program> host, string code, string? token = "registration-token")
    {
        var anonymous = host.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/v1/lti/register?code={Uri.EscapeDataString(code)}"
            + $"&openid_configuration={Uri.EscapeDataString(FakePlatformRegistry.ConfigurationUrl)}"
            + (token is null ? "" : $"&registration_token={Uri.EscapeDataString(token)}"));

        return await response.Content.ReadAsStringAsync();
    }
}

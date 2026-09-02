using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// How the Client learns it is inside a launch.
/// <para>
/// §5.2 is explicit that the embedded presentation is entered <b>because of how
/// the session was established, not because a URL said so</b> — "a query
/// parameter anybody may set is a way to make the full interface look confined".
/// These tests are what keeps the ticket from becoming that parameter by
/// another name.
/// </para>
/// </summary>
[Collection("server-3")]
public class LtiSessionTests(ServerFixture server)
{
    private const string Directory = "session-directory";

    [Fact]
    public async Task A_launch_hands_over_a_ticket_and_it_buys_the_context()
    {
        var world = await LaunchAsync();

        var context = await world.ClaimAsync(world.Ticket);

        Assert.Equal(world.Slug, context.GetProperty("activitySlug").GetString());
        Assert.True(Guid.TryParse(context.GetProperty("linkId").GetString(), out _));
        // §5.4 — the platform knows the language the course is taken in.
        Assert.Equal("pl", context.GetProperty("locale").GetString());
        // §5.2 — the platform framed it, so the interface is the confined one.
        Assert.True(context.GetProperty("embedded").GetBoolean());
    }

    /// <summary>
    /// <b>Once.</b> A ticket left in history, a referrer header or a proxy log is
    /// worth nothing after the Client has used it.
    /// </summary>
    [Fact]
    public async Task A_ticket_cannot_be_claimed_twice()
    {
        var world = await LaunchAsync();

        await world.ClaimAsync(world.Ticket);

        var again = await world.Client.PostAsJsonAsync(
            "/api/v1/lti/session/claim", new { ticket = world.Ticket });
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    /// <summary>
    /// <b>And by its owner.</b> Somebody else holding the string gets nothing —
    /// which is what makes this different from a query parameter.
    /// </summary>
    [Fact]
    public async Task A_ticket_is_useless_to_a_different_session()
    {
        var world = await LaunchAsync();

        var stranger = await Sign.InAsync(world.Host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var stolen = await stranger.PostAsJsonAsync(
            "/api/v1/lti/session/claim", new { ticket = world.Ticket });

        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);

        // And the real owner can still use it: the refusal above took nothing
        // away from them.
        var context = await world.ClaimAsync(world.Ticket);
        Assert.Equal(world.Slug, context.GetProperty("activitySlug").GetString());
    }

    [Fact]
    public async Task An_invented_ticket_buys_nothing()
    {
        var world = await LaunchAsync();

        var invented = await world.Client.PostAsJsonAsync(
            "/api/v1/lti/session/claim", new { ticket = "not-a-ticket-anybody-issued" });

        Assert.Equal(HttpStatusCode.NotFound, invented.StatusCode);
    }

    [Fact]
    public async Task Claiming_needs_a_session_at_all()
    {
        var world = await LaunchAsync();

        var anonymous = world.Host.CreateClient();
        var refused = await anonymous.PostAsJsonAsync(
            "/api/v1/lti/session/claim", new { ticket = world.Ticket });

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }


    /// <summary>
    /// The cookie a launch sets, read as a browser reads it.
    ///
    /// <para>
    /// <b>`SameSite=Lax` is not stored at all inside a frame on another site.</b>
    /// The sign-in appears to work, the redirect happens, and every request after
    /// it is anonymous — measured in Chrome on 2026-08-14, where the browser
    /// refused both cookies with the reason `SameSiteLax`. An assertion on the
    /// header is the only part of that a test can hold.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_launch_into_a_frame_sets_a_cookie_a_browser_will_keep()
    {
        var world = await LaunchAsync();

        var cookies = world.Launched.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToList()
            : [];
        var identity = cookies.FirstOrDefault(c => c.StartsWith(".AspNetCore.Identity.Application"));
        Assert.NotNull(identity);

        Assert.Contains("samesite=none", identity!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", identity, StringComparison.OrdinalIgnoreCase);
        // Keyed to the site that embedded us, so a session opened inside one
        // course is not a session anywhere else.
        Assert.Contains("partitioned", identity, StringComparison.OrdinalIgnoreCase);

        // **And the session cookie beside it**, which is written by middleware on
        // this same response and cannot read a ticket that does not exist yet.
        // Left narrow it is refused, and every request after the launch then
        // starts a fresh session row.
        var tracking = cookies.FirstOrDefault(c => c.StartsWith("aj_session"));
        Assert.NotNull(tracking);
        Assert.Contains("samesite=none", tracking!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partitioned", tracking, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And the ordinary sign-in keeps the narrow cookie, which is the half that
    /// would be easy to lose: widening everything would be simpler to write and
    /// would give up what `Lax` buys on every screen that has nothing to do with
    /// any of this.
    /// </summary>
    [Fact]
    public async Task An_ordinary_sign_in_stays_narrow()
    {
        var client = server.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/identity/login?useCookies=true",
            new { email = Seeder.DevAdminLogin, password = Seeder.DevAdminPassword });
        response.EnsureSuccessStatusCode();

        var identity = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(".AspNetCore.Identity.Application"));

        Assert.Contains("samesite=lax", identity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partitioned", identity, StringComparison.OrdinalIgnoreCase);
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private sealed record World(
        WebApplicationFactory<Program> Host, HttpClient Client, string Ticket, string Slug,
        HttpResponseMessage Launched)
    {
        public async Task<JsonElement> ClaimAsync(string ticket)
        {
            var response = await Client.PostAsJsonAsync(
                "/api/v1/lti/session/claim", new { ticket });
            Assert.True(response.IsSuccessStatusCode,
                $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    /// <summary>
    /// A real launch, followed by the browser: the cookie the launch set is what
    /// the claim is made with, which is the arrangement being tested.
    /// </summary>
    private async Task<World> LaunchAsync()
    {
        using var platform = new FakePlatform();

        var host = server.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPlatformKeys>(
                new StubbedPlatformKeys(platform.SigningKey)))));

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        (await admin.PostAsJsonAsync("/api/v1/lti/platforms",
            platform.Registration(isIdentityAuthority: true, identityNamespace: Directory)))
            .EnsureSuccessStatusCode();

        var (slug, _) = await Build.ActivityAsync(server);
        var user = await DirectoryUserAsync();

        // Cookies kept, redirects not followed: the launch's `SignInAsync` sets
        // the session on this client, and the redirect carries the ticket.
        // **Over TLS, because the cookie a launch sets is `Secure`.** An
        // embedded session is `SameSite=None`, which no browser accepts without
        // `Secure`, so a client on plain HTTP is handed a cookie it will never
        // send back — and every request after the launch is anonymous. That is
        // the real behaviour, not a test artefact: an installation serving plain
        // HTTP cannot have embedded sessions at all.
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost"),
        });

        var begun = await client.PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["iss"] = platform.Issuer }));
        var query = HttpUtility.ParseQueryString(begun.Headers.Location!.Query);

        var launched = await client.PostAsync("/api/v1/lti/launch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = query["state"]!,
                ["id_token"] = platform.IdToken(
                    query["nonce"]!, subject: "sub-" + user.UserName,
                    activitySlug: slug, username: user.UserName),
            }));

        var landing = launched.Headers.Location!.ToString();
        Assert.Contains("/lti/launched", landing);

        var ticket = HttpUtility.ParseQueryString(landing[(landing.IndexOf('?') + 1)..])["ticket"];
        Assert.False(string.IsNullOrWhiteSpace(ticket), $"no ticket in {landing}");

        return new World(host, client, ticket!, slug, launched);
    }

    private async Task<User> DirectoryUserAsync()
    {
        using var scope = server.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var provider = await core.IdentityProviders.FirstOrDefaultAsync(p => p.Slug == Directory);
        if (provider is null)
        {
            provider = new IdentityProvider
            {
                Slug = Directory,
                DisplayName = "Directory",
                Issuer = "https://sso.algojudge.invalid",
                ClientId = "algojudge",
                ClientSecret = "development-only",
            };
            core.IdentityProviders.Add(provider);
            await core.SaveChangesAsync();
        }

        var user = new User { UserName = "sess-" + Guid.NewGuid().ToString("N")[..10], ApprovedAt = DateTime.UtcNow };
        Assert.True((await users.CreateAsync(user)).Succeeded);

        core.UserIdentities.Add(new UserIdentity
        {
            UserId = user.Id,
            ProviderId = provider.Id,
            Subject = Guid.NewGuid().ToString("N"),
        });
        await core.SaveChangesAsync();

        return user;
    }
}

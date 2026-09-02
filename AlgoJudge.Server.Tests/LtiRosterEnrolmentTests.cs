using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Lti;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Putting a course's roster into an activity, without anybody launching.
///
/// <para>
/// <b>This is the half of milestone 2 that can go wrong quietly.</b> A launch has
/// a person at the other end who authenticated a moment ago; a roster is a list
/// somebody else's system handed us, and every account it names is an account
/// this installation is being asked to hand over on that say-so. Most of what
/// follows is about refusing to.
/// </para>
/// </summary>
[Collection("server-1")]
public class LtiRosterEnrolmentTests(ServerFixture server)
{
    private const string Directory = "roster-directory";

    [Fact]
    public async Task A_roster_links_somebody_provisionally_and_puts_them_in_the_activity()
    {
        var world = await BuildAsync();
        var (user, _) = await DirectoryUserAsync();

        world.Roster.Members = [FakeRoster.Member("m-1", username: user.UserName, name: "Jan")];

        var enrolled = await EnrolAsync(world);

        Assert.Equal(1, enrolled.GetProperty("linked").GetInt32());
        Assert.Equal(1, enrolled.GetProperty("granted").GetInt32());
        Assert.Empty(enrolled.GetProperty("skipped").EnumerateArray());

        // **Provisional, and the word matters** (§4.4): nobody authenticated
        // here. What exists is a link this installation inferred.
        await UsingLtiAsync(world.Host, async db =>
        {
            var link = await db.ExternalIdentities.FirstAsync(i => i.Subject == "m-1");
            Assert.Equal(LinkStrength.Provisional, link.Strength);
            Assert.Equal(user.Id, link.UserId);
        });

        using var scope = world.Host.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.True(await core.Grants.AnyAsync(g => g.UserId == user.Id && g.ActivityId != null));
    }

    /// <summary>
    /// <b>The refusal the whole feature hangs off.</b> A roster carrying a name
    /// this installation happens to use is not evidence that they are the same
    /// person — only the namespace makes it so, and only a platform trusted for
    /// that directory may say it.
    /// </summary>
    [Fact]
    public async Task An_account_outside_the_namespace_is_left_alone_and_reported()
    {
        var world = await BuildAsync();
        var local = await LocalUserAsync();

        world.Roster.Members = [FakeRoster.Member("m-2", username: local.UserName)];

        var enrolled = await EnrolAsync(world);

        Assert.Equal(0, enrolled.GetProperty("linked").GetInt32());
        var skipped = Assert.Single(enrolled.GetProperty("skipped").EnumerateArray().ToList());
        Assert.Equal("outsideNamespace", skipped.GetProperty("reason").GetString());

        await UsingLtiAsync(world.Host, async db =>
            Assert.False(await db.ExternalIdentities.AnyAsync(i => i.UserId == local.Id)));
    }

    /// <summary>
    /// Nobody is matched on an address. An automatic correlation on an
    /// unverified email is account takeover, which the identity rules forbid
    /// outright — so a roster that discloses everything <i>but</i> a username
    /// links nobody, however obvious the match looks.
    /// </summary>
    [Fact]
    public async Task A_member_with_no_username_is_never_matched_on_their_address()
    {
        var world = await BuildAsync();
        var (user, _) = await DirectoryUserAsync();

        world.Roster.Members =
        [
            FakeRoster.Member("m-3", name: user.UserName, email: user.Email),
        ];

        var enrolled = await EnrolAsync(world);

        Assert.Equal(0, enrolled.GetProperty("linked").GetInt32());
        var skipped = Assert.Single(enrolled.GetProperty("skipped").EnumerateArray().ToList());
        Assert.Equal("noUsername", skipped.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Somebody_the_course_says_has_left_is_not_put_back_in()
    {
        var world = await BuildAsync();
        var (user, _) = await DirectoryUserAsync();

        world.Roster.Members =
        [
            FakeRoster.Member("m-4", username: user.UserName, status: "Inactive"),
        ];

        var enrolled = await EnrolAsync(world);

        Assert.Equal(0, enrolled.GetProperty("granted").GetInt32());
        var skipped = Assert.Single(enrolled.GetProperty("skipped").EnumerateArray().ToList());
        Assert.Equal("inactive", skipped.GetProperty("reason").GetString());
    }

    /// <summary>
    /// A roster never creates an account. A launch may, because somebody is
    /// standing there; a list read on a manager's behalf would invent an account
    /// for every name in a course that resembles one of ours.
    /// </summary>
    [Fact]
    public async Task A_roster_creates_nobody_even_where_a_launch_would()
    {
        var world = await BuildAsync();
        await AllowAccountCreationAsync(world.Host);

        var invented = "nobody-" + Guid.NewGuid().ToString("N")[..8];
        world.Roster.Members = [FakeRoster.Member("m-5", username: invented)];

        var enrolled = await EnrolAsync(world);

        Assert.Equal(0, enrolled.GetProperty("linked").GetInt32());
        Assert.Equal("unknownAccount",
            enrolled.GetProperty("skipped")[0].GetProperty("reason").GetString());

        using var scope = world.Host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.Null(await users.FindByNameAsync(invented));
    }

    /// <summary>
    /// A platform not trusted to say who somebody is cannot enrol from a roster
    /// either — the flag governs exactly this, and reading the list is still
    /// allowed.
    /// </summary>
    [Fact]
    public async Task A_platform_without_identity_authority_cannot_enrol_from_a_roster()
    {
        var world = await BuildAsync(authority: false);
        var (user, _) = await DirectoryUserAsync();
        world.Roster.Members = [FakeRoster.Member("m-6", username: user.UserName)];

        var refused = await world.Manager.PostAsync(
            $"/api/v1/lti/placements/{world.LinkId}/roster/enrol", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("lti.roster.notAuthority", problem.GetProperty("code").GetString());
    }

    /// <summary>
    /// The other half of §4.4: a launch is the witness a roster never was, so the
    /// link it inferred is raised to confirmed when the person actually arrives.
    /// </summary>
    [Fact]
    public async Task A_launch_raises_the_link_a_roster_only_inferred()
    {
        var world = await BuildAsync();
        var (user, _) = await DirectoryUserAsync();
        world.Roster.Members = [FakeRoster.Member("m-7", username: user.UserName)];

        await EnrolAsync(world);
        await UsingLtiAsync(world.Host, async db =>
            Assert.Equal(LinkStrength.Provisional,
                (await db.ExternalIdentities.FirstAsync(i => i.Subject == "m-7")).Strength));

        await LaunchAsync(world, username: user.UserName!, subject: "m-7");

        await UsingLtiAsync(world.Host, async db =>
            Assert.Equal(LinkStrength.Confirmed,
                (await db.ExternalIdentities.FirstAsync(i => i.Subject == "m-7")).Strength));
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private sealed record World(
        WebApplicationFactory<Program> Host,
        FakePlatform Platform,
        FakeRoster Roster,
        HttpClient Manager,
        string LinkId,
        string Slug);

    /// <summary>
    /// A registered platform, a placement that knows where its roster is, and a
    /// manager to ask with. The placement is made by an actual launch, so the
    /// roster address is the one a launch captured rather than one written here.
    /// </summary>
    private async Task<World> BuildAsync(bool authority = true)
    {
        var platform = new FakePlatform();
        var roster = new FakeRoster();

        var host = server.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IPlatformKeys>(
                    new StubbedPlatformKeys(platform.SigningKey)));
                services.AddHttpClient(nameof(PlatformTokens))
                    .ConfigurePrimaryHttpMessageHandler(() => roster);
                services.AddHttpClient(nameof(NrpsClient))
                    .ConfigurePrimaryHttpMessageHandler(() => roster);
            }));

        // **Signed in against the host this test built**, not the shared fixture.
        // A client from the fixture is served by the fixture's own host, where
        // the fake platform is not wired in — every call then goes to the real
        // network and fails as "the token endpoint could not be reached", which
        // reads like the fake being broken rather than unused.
        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        // Registered against the fake's own token endpoint, so the
        // client-credentials grant goes somewhere that answers — the same shape
        // the grade tests use.
        (await admin.PostAsJsonAsync("/api/v1/lti/platforms", new
        {
            displayName = "Roster platform",
            issuer = platform.Issuer,
            clientId = platform.ClientId,
            deploymentId = platform.DeploymentId,
            keySetUrl = platform.Issuer + "/certs",
            authTokenUrl = FakeRoster.TokenUrl,
            authLoginUrl = platform.Issuer + "/auth",
            isIdentityAuthority = authority,
            identityNamespace = authority ? Directory : null,
        })).EnsureSuccessStatusCode();

        var (slug, _) = await Build.ActivityAsync(server);

        // A first launch, by somebody the platform may not claim, purely to make
        // the placement exist and learn where the roster is.
        var world = new World(host, platform, roster, admin, "", slug);
        await LaunchAsync(world, username: "nobody-" + Guid.NewGuid().ToString("N")[..8],
            subject: "setup", activity: slug);

        string linkId = "";
        await UsingLtiAsync(host, async db =>
        {
            var link = await db.ResourceLinks.OrderByDescending(l => l.CreatedAt).FirstAsync();
            Assert.False(string.IsNullOrWhiteSpace(link.NrpsMembershipsUrl),
                "the launch did not capture where the roster is");
            linkId = link.Id.ToString("D");
        });

        return world with { LinkId = linkId };
    }

    private async Task<JsonElement> EnrolAsync(World world)
    {
        var response = await world.Manager.PostAsync(
            $"/api/v1/lti/placements/{world.LinkId}/roster/enrol", null);
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> LaunchAsync(
        World world, string username, string subject, string? activity = null)
    {
        var client = world.Host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        var begun = await client.PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["iss"] = world.Platform.Issuer,
            }));
        var query = HttpUtility.ParseQueryString(begun.Headers.Location!.Query);

        var launched = await client.PostAsync("/api/v1/lti/launch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = query["state"]!,
                ["id_token"] = world.Platform.IdToken(
                    query["nonce"]!, subject: subject,
                    activitySlug: activity ?? world.Slug, username: username),
            }));

        return launched.Headers.Location?.ToString() ?? "";
    }

    private static async Task AllowAccountCreationAsync(WebApplicationFactory<Program> host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LtiDbContext>();
        var settings = await db.Settings.FirstOrDefaultAsync();
        if (settings is null)
        {
            db.Settings.Add(new LtiSettings { AccountCreationEnabled = true });
        }
        else
        {
            settings.AccountCreationEnabled = true;
        }
        await db.SaveChangesAsync();
    }

    private async Task<(User User, IdentityProvider Provider)> DirectoryUserAsync()
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
                DisplayName = "The directory under test",
                Issuer = "https://sso.roster.invalid",
                ClientId = "algojudge",
                ClientSecret = "development-only",
            };
            core.IdentityProviders.Add(provider);
            await core.SaveChangesAsync();
        }

        var name = "roster-" + Guid.NewGuid().ToString("N")[..8];
        var user = new User { UserName = name, Email = name + "@roster.invalid", ApprovedAt = DateTime.UtcNow };
        (await users.CreateAsync(user, "Roster-development-only-1!")).Succeeded.Should();

        core.UserIdentities.Add(new UserIdentity
        {
            ProviderId = provider.Id,
            UserId = user.Id,
            Subject = "sso-" + name,
        });
        await core.SaveChangesAsync();

        return (user, provider);
    }

    private async Task<User> LocalUserAsync()
    {
        using var scope = server.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var name = "local-" + Guid.NewGuid().ToString("N")[..8];
        var user = new User { UserName = name, Email = name + "@local.invalid", ApprovedAt = DateTime.UtcNow };
        (await users.CreateAsync(user, "Local-development-only-1!")).Succeeded.Should();
        return user;
    }

    private static async Task UsingLtiAsync(
        WebApplicationFactory<Program> host, Func<LtiDbContext, Task> what)
    {
        using var scope = host.Services.CreateScope();
        await what(scope.ServiceProvider.GetRequiredService<LtiDbContext>());
    }
}

file static class Assertions
{
    /// <summary>Reads as a sentence and fails with the reason rather than "false".</summary>
    public static void Should(this bool succeeded) =>
        Assert.True(succeeded, "the account could not be created");
}

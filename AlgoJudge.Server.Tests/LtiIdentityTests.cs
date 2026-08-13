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
/// Who a launch resolves to, and — mostly — who it refuses to resolve to.
/// <para>
/// §4.5 is honest about what this mechanism is: matching on an attribute the
/// platform asserts means trusting the platform to make claims inside the
/// directory's namespace, and a compromised platform could otherwise assert any
/// username and be handed the account. Everything here is the bound on that.
/// </para>
/// </summary>
[Collection("server")]
public class LtiIdentityTests(ServerFixture server)
{
    private const string Directory = "test-directory";

    [Fact]
    public async Task A_launch_binds_the_asserted_username_to_the_account_that_holds_it()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: true);
        var (user, _) = await DirectoryUserAsync();
        var slug = await ActivityAsync();

        using var host = HostFor(platform);
        var landing = await LaunchAsync(host, platform, username: user.UserName!, activity: slug);

        Assert.Contains("/lti/launched", landing);

        // **A ticket, and nothing else.** The context — placement, locale,
        // whether the platform framed it — is bought with it rather than written
        // in the address, because §5.2 will not have the confined mode entered
        // because a URL said so. `LtiSessionTests` covers the exchange.
        var query = System.Web.HttpUtility.ParseQueryString(landing[(landing.IndexOf('?') + 1)..]);
        Assert.False(string.IsNullOrWhiteSpace(query["ticket"]), "the landing carries no ticket");
        Assert.Null(query["embedded"]);

        // The link is written once and read afterwards, so it has to exist.
        await UsingLtiAsync(host, async db =>
        {
            var link = await db.ExternalIdentities.FirstOrDefaultAsync(i => i.UserId == user.Id);
            Assert.NotNull(link);
            Assert.Equal(LinkStrength.Confirmed, link!.Strength);
            Assert.Equal(user.UserName, link.AssertedUsername);
        });
    }

    /// <summary>
    /// <b>The safeguard the whole section exists for.</b> The namespace names an
    /// identity provider; an account that does not come through that directory
    /// cannot be claimed, however loudly the platform asserts it.
    /// </summary>
    [Fact]
    public async Task A_platform_cannot_claim_an_account_outside_the_directory_it_is_trusted_for()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: true);
        var slug = await ActivityAsync();

        // A local account: no link from the directory, exactly like an
        // administrator or anybody registered at the instance itself.
        var local = await LocalUserAsync();

        using var host = HostFor(platform);
        var landing = await LaunchAsync(host, platform, username: local.UserName!, activity: slug);

        Assert.Contains("/lti/sign-in", landing);

        await UsingLtiAsync(host, async db =>
            Assert.False(await db.ExternalIdentities.AnyAsync(i => i.UserId == local.Id)));
    }

    /// <summary>
    /// The flag alone, isolated. The platform is registered <b>with</b> a
    /// namespace and without the authority to use it — otherwise the refusal
    /// could come from the missing namespace instead, and the test would pass
    /// with the flag never being read. It did, until a sabotage said so.
    /// </summary>
    [Fact]
    public async Task A_platform_without_identity_authority_never_binds_anybody()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: false, identityNamespace: Directory);
        var (user, _) = await DirectoryUserAsync();
        var slug = await ActivityAsync();

        using var host = HostFor(platform);
        var landing = await LaunchAsync(host, platform, username: user.UserName!, activity: slug);

        Assert.Contains("/lti/sign-in", landing);
    }

    /// <summary>
    /// §4.3: written once. A later launch asserting somebody else for the same
    /// subject is a conflict to report — following it would hand one person's
    /// history to another because a field changed in Moodle.
    /// </summary>
    [Fact]
    public async Task A_changed_assertion_is_reported_and_the_link_does_not_move()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: true);
        var (first, _) = await DirectoryUserAsync();
        var (second, _) = await DirectoryUserAsync();
        var slug = await ActivityAsync();

        using var host = HostFor(platform);
        await LaunchAsync(host, platform, username: first.UserName!, activity: slug, subject: "sub-1");

        var landing = await LaunchAsync(
            host, platform, username: second.UserName!, activity: slug, subject: "sub-1");

        Assert.Contains("/lti/conflict", landing);

        await UsingLtiAsync(host, async db =>
        {
            var link = await db.ExternalIdentities.FirstAsync(i => i.Subject == "sub-1");
            Assert.Equal(first.Id, link.UserId);
            Assert.Equal(first.UserName, link.AssertedUsername);
        });
    }

    [Fact]
    public async Task A_launch_that_resolves_to_nobody_creates_no_account_by_default()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: true);
        var slug = await ActivityAsync();
        var invented = "nobody-" + Guid.NewGuid().ToString("N")[..8];

        using var host = HostFor(platform);
        var landing = await LaunchAsync(host, platform, username: invented, activity: slug);

        Assert.Contains("/lti/sign-in", landing);

        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.Null(await users.FindByNameAsync(invented));
    }

    // ── What a launch grants ─────────────────────────────────────────────────

    [Fact]
    public async Task A_learner_launch_puts_them_in_the_activity_as_a_participant()
    {
        using var platform = new FakePlatform();
        var registered = await RegisterAsync(platform, authority: true);
        var (user, _) = await DirectoryUserAsync();
        var slug = await ActivityAsync();

        using var host = HostFor(platform);
        await LaunchAsync(host, platform, username: user.UserName!, activity: slug);

        var grant = await GrantAsync(host, user.Id);
        Assert.NotNull(grant);
        Assert.Equal("participant", grant!.CreatedFromTemplate);
        Assert.False(grant.IsSystem);
        // Attributable: a course grant from a launch names the platform's
        // provider row rather than looking like somebody typed it in.
        Assert.Equal(registered.GetProperty("providerId").GetString(),
            grant.SourceProviderId!.Value.ToString("D"));
        // And never authoritative — a launch does not demote anybody.
        Assert.False(grant.OverrideSystem);
    }

    [Fact]
    public async Task An_instructor_launch_puts_them_in_as_staff()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: true);
        var (user, _) = await DirectoryUserAsync();
        var slug = await ActivityAsync();

        using var host = HostFor(platform);
        await LaunchAsync(host, platform, username: user.UserName!, activity: slug,
            roles: [LtiRoles.Instructor]);

        var grant = await GrantAsync(host, user.Id);
        Assert.NotNull(grant);
        Assert.Equal("manager", grant!.CreatedFromTemplate);
        Assert.True(grant.IsSystem);
    }

    /// <summary>
    /// A system role at the platform says what somebody may do in Moodle.
    /// Reading it as authority here would let a claim mint privilege, which the
    /// permission model forbids everywhere else.
    /// </summary>
    [Fact]
    public async Task A_platform_administrator_is_not_an_AlgoJudge_manager()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: true);
        var (user, _) = await DirectoryUserAsync();
        var slug = await ActivityAsync();

        using var host = HostFor(platform);
        await LaunchAsync(host, platform, username: user.UserName!, activity: slug,
            roles: [LtiRoles.Administrator, LtiRoles.Learner]);

        var grant = await GrantAsync(host, user.Id);
        Assert.Equal("participant", grant!.CreatedFromTemplate);
    }

    // ── The placement ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_launch_naming_no_activity_says_which_parameter_is_missing()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: true);
        var (user, _) = await DirectoryUserAsync();

        using var host = HostFor(platform);
        var landing = await LaunchAsync(host, platform, username: user.UserName!, activity: null);

        Assert.Contains("reason=" + LtiLaunchException.NoActivity, landing);
    }

    /// <summary>
    /// Decided 2026-08-13: a second placement is allowed, and is not allowed to
    /// be silent. One activity feeding two gradebooks is a thing to learn from a
    /// screen rather than from a gradebook that disagrees with itself.
    /// </summary>
    [Fact]
    public async Task A_second_course_placing_the_same_activity_waits_for_a_decision()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform, authority: true);
        var (user, _) = await DirectoryUserAsync();
        var slug = await ActivityAsync();

        using var host = HostFor(platform);

        var first = await LaunchAsync(host, platform, username: user.UserName!, activity: slug,
            resourceLinkId: "rl-first", contextId: "course-a");
        Assert.Contains("/lti/launched", first);

        var second = await LaunchAsync(host, platform, username: user.UserName!, activity: slug,
            resourceLinkId: "rl-second", contextId: "course-b");
        Assert.Contains("reason=" + LtiLaunchException.SharingNotAcknowledged, second);
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> HostFor(FakePlatform platform) =>
        server.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPlatformKeys>(
                new StubbedPlatformKeys(platform.SigningKey)))));

    private async Task<JsonElement> RegisterAsync(
        FakePlatform platform, bool authority, string? identityNamespace = null)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var response = await admin.PostAsJsonAsync("/api/v1/lti/platforms",
            platform.Registration(authority, identityNamespace ?? (authority ? Directory : null)));
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// An account that came through the directory the platform is trusted for —
    /// which is what makes it claimable at all.
    /// </summary>
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
                Issuer = "https://sso.algojudge.invalid",
                ClientId = "algojudge",
                ClientSecret = "development-only",
            };
            core.IdentityProviders.Add(provider);
            await core.SaveChangesAsync();
        }

        var user = new User { UserName = "dir-" + Guid.NewGuid().ToString("N")[..10] };
        Assert.True((await users.CreateAsync(user)).Succeeded);

        core.UserIdentities.Add(new UserIdentity
        {
            UserId = user.Id,
            ProviderId = provider.Id,
            Subject = Guid.NewGuid().ToString("N"),
        });
        await core.SaveChangesAsync();

        return (user, provider);
    }

    /// <summary>An account with no directory behind it, like an administrator's.</summary>
    private async Task<User> LocalUserAsync()
    {
        using var scope = server.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User { UserName = "local-" + Guid.NewGuid().ToString("N")[..10] };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        return user;
    }

    private async Task<string> ActivityAsync()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        return slug;
    }

    private static async Task<string> LaunchAsync(
        WebApplicationFactory<Program> host,
        FakePlatform platform,
        string username,
        string? activity,
        string? subject = null,
        string? resourceLinkId = null,
        string? contextId = null,
        string[]? roles = null)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var begun = await client.PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["iss"] = platform.Issuer }));
        var query = HttpUtility.ParseQueryString(begun.Headers.Location!.Query);

        var response = await client.PostAsync("/api/v1/lti/launch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = query["state"]!,
                ["id_token"] = platform.IdToken(
                    query["nonce"]!,
                    subject: subject ?? "sub-" + username,
                    resourceLinkId: resourceLinkId,
                    contextId: contextId,
                    activitySlug: activity,
                    username: username,
                    roles: roles),
            }));

        return response.Headers.Location!.ToString();
    }

    private static async Task<Grant?> GrantAsync(WebApplicationFactory<Program> host, string userId)
    {
        using var scope = host.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await core.Grants.AsNoTracking()
            .FirstOrDefaultAsync(g => g.UserId == userId && g.ActivityId != null);
    }

    private static async Task UsingLtiAsync(
        WebApplicationFactory<Program> host, Func<LtiDbContext, Task> what)
    {
        using var scope = host.Services.CreateScope();
        await what(scope.ServiceProvider.GetRequiredService<LtiDbContext>());
    }
}

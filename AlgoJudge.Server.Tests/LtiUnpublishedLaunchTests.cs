using System.Net.Http.Json;
using System.Web;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Lti;
using AlgoJudge.Server.Lti.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a launch does while the activity behind it is still being prepared.
///
/// <para>
/// <b>The answer differs by who launched.</b> A copy of last year is reachable
/// from the course the moment somebody places it, and the people who arrive
/// first are the ones preparing it. Telling them all the same thing would either
/// let a class into an unfinished activity or lock the teacher out of the thing
/// they are finishing.
/// </para>
/// </summary>
[Collection("server-3")]
public class LtiUnpublishedLaunchTests(ServerFixture server)
{
    private const string Directory = "unpublished-directory";

    [Fact]
    public async Task Somebody_taking_part_is_told_it_is_not_open_yet()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var user = await DirectoryUserAsync();
        var slug = await UnpublishedActivityAsync();

        using var host = HostFor(platform);
        var landing = await LaunchAsync(host, platform, user.UserName!, slug);

        // **Its own reason, not a general failure.** The page a participant reads
        // inside their course has to say something true: nothing is broken and
        // there is nothing for them to do but come back.
        Assert.Contains("/lti/failed?reason=notPublished", landing);
    }

    /// <summary>
    /// <b>And the person preparing it gets in</b>, which is how it gets prepared:
    /// the copy is reached from the course like everything else. Who that is comes
    /// from the role the launch carries, not from a permission here - nobody is
    /// signed in yet at the point this is decided.
    /// </summary>
    [Fact]
    public async Task Somebody_who_runs_the_course_launches_into_it()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var user = await DirectoryUserAsync();
        var slug = await UnpublishedActivityAsync();

        using var host = HostFor(platform);
        var landing = await LaunchAsync(host, platform, user.UserName!, slug, runsTheCourse: true);

        Assert.Contains("/lti/launched", landing);
    }

    /// <summary>
    /// Publishing is what changes the first answer, so the refusal is a state
    /// rather than a wall.
    /// </summary>
    [Fact]
    public async Task Publishing_it_lets_everybody_in()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var user = await DirectoryUserAsync();
        var slug = await UnpublishedActivityAsync();

        using var host = HostFor(platform);
        var link = "rl-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Contains("notPublished",
            await LaunchAsync(host, platform, user.UserName!, slug, resourceLinkId: link));

        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var id = await IdOfAsync(slug);
        (await manager.PostAsJsonAsync(
            $"/api/v1/activities/{id}/published", new { published = true }))
            .EnsureSuccessStatusCode();

        Assert.Contains("/lti/launched",
            await LaunchAsync(host, platform, user.UserName!, slug, resourceLinkId: link));
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private WebApplicationFactory<Program> HostFor(FakePlatform platform) =>
        server.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IPlatformKeys>(
                new StubbedPlatformKeys(platform.SigningKey)))));

    private async Task RegisterAsync(FakePlatform platform)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var response = await admin.PostAsJsonAsync("/api/v1/lti/platforms",
            platform.Registration(isIdentityAuthority: true, identityNamespace: Directory));
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<string> LaunchAsync(
        WebApplicationFactory<Program> host, FakePlatform platform, string username, string slug,
        bool runsTheCourse = false, string? resourceLinkId = null)
    {
        var browser = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        var begun = await browser.PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["iss"] = platform.Issuer }));
        var query = HttpUtility.ParseQueryString(begun.Headers.Location!.Query);

        var response = await browser.PostAsync("/api/v1/lti/launch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = query["state"]!,
                ["id_token"] = platform.IdToken(
                    query["nonce"]!,
                    // **The same link across a test that launches twice.** A new
                    // one each time is a second placement of one activity, which
                    // the sharing gate refuses before anything here is reached.
                    resourceLinkId: resourceLinkId ?? "rl-" + Guid.NewGuid().ToString("N")[..8],
                    activitySlug: slug,
                    username: username,
                    roles: runsTheCourse ? [LtiRoles.Instructor] : [LtiRoles.Learner]),
            }));

        return response.Headers.Location?.OriginalString
            ?? throw new Xunit.Sdk.XunitException(
                $"the launch did not redirect: {(int)response.StatusCode}");
    }

    /// <summary>An activity that exists and has not been published.</summary>
    private async Task<string> UnpublishedActivityAsync()
    {
        var (slug, _) = await Build.ActivityAsync(server);

        await using var context = server.NewContext();
        var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
        activity.PublishedAt = null;
        await context.SaveChangesAsync();

        return slug;
    }

    private async Task<Guid> IdOfAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id;
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
                DisplayName = "The directory under test",
                Issuer = "https://sso.unpublished.invalid",
                ClientId = "algojudge",
                ClientSecret = "development-only",
            };
            core.IdentityProviders.Add(provider);
            await core.SaveChangesAsync();
        }

        var user = new User { UserName = "unp-" + Guid.NewGuid().ToString("N")[..10], ApprovedAt = DateTime.UtcNow };
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

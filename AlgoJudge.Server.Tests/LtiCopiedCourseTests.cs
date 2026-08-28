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
/// A course somebody copied, and the two different things that can mean.
///
/// <para>
/// <b>The data is identical in both cases.</b> A second course reaching one
/// activity is a share when one offering runs in two courses, and a new year
/// when last year's course was rolled over - and nothing in the launch says
/// which. So the launch refuses, the screen says what it looks like, and a
/// person decides.
/// </para>
/// </summary>
[Collection("server-1")]
public class LtiCopiedCourseTests(ServerFixture server)
{
    private const string Directory = "copied-course-directory";

    /// <summary>
    /// <b>What the platform sends is a list.</b> Moodle answers `3,2` for a copy
    /// of a copy - measured on 5.2, 2026-08-15 - so the placement two years on
    /// still points back at the original.
    /// </summary>
    [Fact]
    public async Task A_placement_says_which_one_it_looks_like_a_copy_of()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var user = await DirectoryUserAsync();
        var (slug, _) = await Build.ActivityAsync(server);

        using var host = HostFor(platform);
        await LaunchAsync(host, platform, user.UserName!, slug, context: "2", history: null);

        // The copy of the copy: its history names the course before it and the
        // one before that.
        await LaunchAsync(host, platform, user.UserName!, slug, context: "4", history: "3,2");

        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var placements = await manager.GetFromJsonAsync<JsonElement>(
            "/api/v1/lti/placements");

        var copy = placements.EnumerateArray()
            .First(p => p.GetProperty("contextId").GetString() == "4");
        var original = placements.EnumerateArray()
            .First(p => p.GetProperty("contextId").GetString() == "2");

        Assert.Equal(original.GetProperty("id").GetString(), Hint(copy));

        // And the original is not a copy of anything.
        Assert.Null(Hint(original));
    }

    [Fact]
    public async Task A_course_copied_from_one_we_never_saw_is_not_a_copy_of_anything()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var user = await DirectoryUserAsync();
        var (slug, _) = await Build.ActivityAsync(server);

        using var host = HostFor(platform);

        // **A sibling exists**, in a course the history does not name. Without
        // one, "no match" is what an implementation that matched on nothing at
        // all would also answer, and the test would pass either way.
        await LaunchAsync(host, platform, user.UserName!, slug, context: "5", history: null);
        await LaunchAsync(host, platform, user.UserName!, slug, context: "9", history: "8,7");

        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var placements = await manager.GetFromJsonAsync<JsonElement>("/api/v1/lti/placements");

        var placement = placements.EnumerateArray()
            .First(p => p.GetProperty("contextId").GetString() == "9");

        Assert.Null(Hint(placement));
    }

    /// <summary>
    /// <b>The answer for a new year.</b> Accepting the sharing would put both
    /// cohorts into one activity; this gives the copied course its own, and the
    /// placement points there in the same act.
    /// </summary>
    [Fact]
    public async Task Copying_the_activity_repoints_the_placement_at_the_copy()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var user = await DirectoryUserAsync();
        var (slug, _) = await Build.ActivityAsync(server);

        using var host = HostFor(platform);
        await LaunchAsync(host, platform, user.UserName!, slug, context: "2", history: null);
        await LaunchAsync(host, platform, user.UserName!, slug, context: "3", history: "2");

        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var placements = await manager.GetFromJsonAsync<JsonElement>("/api/v1/lti/placements");
        var copy = placements.EnumerateArray()
            .First(p => p.GetProperty("contextId").GetString() == "3");
        var placementId = copy.GetProperty("id").GetString();
        var originalActivity = copy.GetProperty("activityId").GetString();

        var newSlug = "next-year-" + Guid.NewGuid().ToString("N")[..8];
        var response = await manager.PostAsJsonAsync(
            $"/api/v1/lti/placements/{placementId}/copy-activity",
            new { slug = newSlug, startsAt = DateTime.UtcNow.AddDays(365) });

        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var after = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(newSlug, after.GetProperty("activitySlug").GetString());
        Assert.NotEqual(originalActivity, after.GetProperty("activityId").GetString());

        // **Nothing is shared any more**, so the launch that was refused now
        // has nothing to be refused for.
        Assert.True(after.GetProperty("sharingAcknowledged").GetBoolean());

        await using var context = server.NewContext();
        var made = await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == newSlug);

        // And it arrives like every other copy: not for anybody yet.
        Assert.Null(made.PublishedAt);
    }

    // ── Getting there ────────────────────────────────────────────────────────

    /// <summary>
    /// The hint, absent rather than null: this API omits fields it has no value
    /// for, so asking for the property directly throws instead of answering.
    /// </summary>
    private static string? Hint(JsonElement placement) =>
        placement.TryGetProperty("looksLikeCopyOf", out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    /// <summary>
    /// A launch from one course. The second one is refused for want of a
    /// decision about the sharing, which is the state this whole screen is for -
    /// so the answer is not checked here.
    /// </summary>
    private static async Task LaunchAsync(
        WebApplicationFactory<Program> host, FakePlatform platform,
        string username, string slug, string context, string? history)
    {
        var browser = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        var begun = await browser.PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["iss"] = platform.Issuer }));
        var query = HttpUtility.ParseQueryString(begun.Headers.Location!.Query);

        await browser.PostAsync("/api/v1/lti/launch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = query["state"]!,
                ["id_token"] = platform.IdToken(
                    query["nonce"]!,
                    resourceLinkId: "rl-" + context + "-" + Guid.NewGuid().ToString("N")[..6],
                    contextId: context,
                    activitySlug: slug,
                    username: username,
                    contextHistory: history),
            }));
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
                Issuer = "https://sso.copied.invalid",
                ClientId = "algojudge",
                ClientSecret = "development-only",
            };
            core.IdentityProviders.Add(provider);
            await core.SaveChangesAsync();
        }

        var user = new User { UserName = "cp-" + Guid.NewGuid().ToString("N")[..10] };
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

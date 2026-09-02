using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using AlgoJudge.Server.Authorization;
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
using Microsoft.IdentityModel.JsonWebTokens;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Deep Linking: the platform asks what to place, somebody here picks, and the
/// answer goes back signed.
///
/// <para>
/// <b>The awkward part is that the answer travels through a person.</b> Between
/// the platform's signed question and this tool's signed answer a browser leaves
/// for the application and comes back, so everything the answer is built from
/// has to survive that trip somewhere a person cannot edit — above all the
/// address the answer is posted at.
/// </para>
/// </summary>
[Collection("server-3")]
public class LtiDeepLinkTests(ServerFixture server)
{
    private const string Directory = "deep-link-directory";

    [Fact]
    public async Task A_platform_asking_what_to_place_is_sent_to_choose()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var (user, _) = await DirectoryUserAsync();

        using var host = HostFor(platform);
        var browser = Browser(host);

        var landing = await ChooseAsync(host, browser, platform, user.UserName!);

        // Not `/lti/launched`: there is nothing to run, and sending somebody to
        // an activity they never picked is how a placement silently becomes one.
        Assert.Contains("/lti/choose?code=", landing);

        var view = await OpenAsync(browser, CodeOf(landing));

        Assert.Equal("Algorytmy i struktury danych", view.GetProperty("contextTitle").GetString());
        Assert.True(view.GetProperty("acceptMultiple").GetBoolean());
        // The frame the platform opened this in, carried through the round trip
        // — the screen has to fit inside it.
        Assert.True(view.GetProperty("embedded").GetBoolean());
    }

    /// <summary>
    /// <b>What somebody may place is what they may edit</b>, decided by the same
    /// permission that governs an activity everywhere else. A screen that offered
    /// every activity would let a teacher in one faculty place another faculty's
    /// work into their course.
    /// </summary>
    [Fact]
    public async Task Only_the_activities_this_person_manages_are_offered()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var (user, _) = await DirectoryUserAsync();
        var (mine, _) = await Build.ActivityAsync(server);
        var (foreign, _) = await Build.ActivityAsync(server);

        using var host = HostFor(platform);
        var browser = Browser(host);

        var landing = await ChooseAsync(host, browser, platform, user.UserName!);
        var beforeAnyRights = await OpenAsync(browser, CodeOf(landing));

        // Managing nothing offers nothing, which is the honest answer rather
        // than an empty screen that looks broken.
        Assert.Empty(beforeAnyRights.GetProperty("activities").EnumerateArray());

        await MayEditAsync(user.Id, mine);

        var second = await ChooseAsync(host, browser, platform, user.UserName!);
        var offered = await OpenAsync(browser, CodeOf(second));

        var slugs = offered.GetProperty("activities").EnumerateArray()
            .Select(a => a.GetProperty("slug").GetString())
            .ToList();

        Assert.Contains(mine, slugs);
        Assert.DoesNotContain(foreign, slugs);
    }

    [Fact]
    public async Task The_answer_names_the_activity_and_is_signed_as_this_tool()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var (user, _) = await DirectoryUserAsync();
        var (slug, _) = await Build.ActivityAsync(server);
        await MayEditAsync(user.Id, slug);

        using var host = HostFor(platform);
        var browser = Browser(host);

        var landing = await ChooseAsync(host, browser, platform, user.UserName!);
        // The id comes off the list the screen was given, which is where the
        // Client gets it too.
        var answer = await RespondAsync(
            browser, CodeOf(landing), await IdOfAsync(browser, CodeOf(landing), slug));

        // Posted at the platform's own address, session key and all — never one
        // this tool built or a caller supplied.
        Assert.Equal(
            platform.Issuer + "/mod/lti/contentitem_return.php?course=2&id=7&sesskey=aBc123",
            answer.GetProperty("returnUrl").GetString());

        var token = new JsonWebTokenHandler().ReadJsonWebToken(answer.GetProperty("jwt").GetString());

        Assert.Equal(platform.ClientId, token.Issuer);
        Assert.Equal(platform.Issuer, token.Audiences.Single());
        Assert.Equal(LtiClaims.DeepLinkingResponse, Claim(token, LtiClaims.MessageType).GetString());
        Assert.Equal(LtiClaims.SupportedVersion, Claim(token, LtiClaims.Version).GetString());
        Assert.Equal(platform.DeploymentId, Claim(token, LtiClaims.DeploymentId).GetString());

        // **Echoed untouched.** The platform gave this string out and is entitled
        // to get exactly it back.
        Assert.Equal("moodle-opaque-data", Claim(token, LtiClaims.DeepLinkingData).GetString());

        var item = Claim(token, LtiClaims.ContentItems).EnumerateArray().Single();
        Assert.Equal("ltiResourceLink", item.GetProperty("type").GetString());
        Assert.Equal(slug, item.GetProperty("custom").GetProperty("activity").GetString());

        // The substitution parameters travel with the placement, so a link placed
        // this way resolves people the same way one typed by hand does.
        Assert.Equal("$User.username",
            item.GetProperty("custom").GetProperty("username").GetString());

        // **No line item.** Scores are per problem and their line items are
        // created over AGS; asking for one here makes a column for the activity
        // as a whole that nothing ever writes to.
        Assert.False(item.TryGetProperty("lineItem", out _));

        // Signed with the tool key a platform can fetch, not with anything local
        // to this request.
        var jwks = await host.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/lti/jwks.json");
        var published = jwks.GetProperty("keys").EnumerateArray()
            .Select(k => k.GetProperty("kid").GetString()).ToList();
        Assert.Contains(token.Kid, published);
    }

    /// <summary>
    /// A platform that said it takes one item means it. Moodle keeps the first
    /// and drops the rest without a word, so the refusal has to happen here.
    /// </summary>
    [Fact]
    public async Task A_platform_that_takes_one_item_is_not_sent_two()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var (user, _) = await DirectoryUserAsync();
        var (first, _) = await Build.ActivityAsync(server);
        var (second, _) = await Build.ActivityAsync(server);
        await MayEditAsync(user.Id, first);
        await MayEditAsync(user.Id, second);

        using var host = HostFor(platform);
        var browser = Browser(host);

        var landing = await ChooseAsync(
            host, browser, platform, user.UserName!, acceptMultiple: false);
        var code = CodeOf(landing);

        var refused = await browser.PostAsJsonAsync(
            $"/api/v1/lti/deep-link/{code}/response",
            new
            {
                activityIds = new[]
                {
                    await IdOfAsync(browser, code, first),
                    await IdOfAsync(browser, code, second),
                },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    [Fact]
    public async Task A_choosing_is_answered_once()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var (user, _) = await DirectoryUserAsync();
        var (slug, _) = await Build.ActivityAsync(server);
        await MayEditAsync(user.Id, slug);

        using var host = HostFor(platform);
        var browser = Browser(host);

        var code = CodeOf(await ChooseAsync(host, browser, platform, user.UserName!));
        var activityId = await IdOfAsync(browser, code, slug);
        await RespondAsync(browser, code, activityId);

        var again = await browser.PostAsJsonAsync(
            $"/api/v1/lti/deep-link/{code}/response", new { activityIds = new[] { activityId } });

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    /// <summary>
    /// <b>Somebody else's code is not found rather than forbidden.</b> Telling a
    /// stranger that a code exists but is not theirs tells them the code is real,
    /// and this one places links into a course.
    /// </summary>
    [Fact]
    public async Task Somebody_elses_choosing_is_not_theirs_to_answer()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var (user, _) = await DirectoryUserAsync();

        using var host = HostFor(platform);
        var browser = Browser(host);
        var code = CodeOf(await ChooseAsync(host, browser, platform, user.UserName!));

        var stranger = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var refused = await stranger.GetAsync($"/api/v1/lti/deep-link/{code}");

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task Nobody_signed_in_may_read_a_choosing()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var (user, _) = await DirectoryUserAsync();

        using var host = HostFor(platform);
        var code = CodeOf(await ChooseAsync(host, Browser(host), platform, user.UserName!));

        var anonymous = await host.CreateClient().GetAsync($"/api/v1/lti/deep-link/{code}");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    /// <summary>
    /// A request with nowhere to send the answer is refused before anybody is
    /// signed in — there would be no way to finish, and a person left on a
    /// picking screen that cannot submit has no way to find that out.
    /// </summary>
    [Fact]
    public async Task A_request_naming_no_return_address_is_refused()
    {
        using var platform = new FakePlatform();
        await RegisterAsync(platform);
        var (user, _) = await DirectoryUserAsync();

        using var host = HostFor(platform);
        var browser = Browser(host);

        var (state, nonce) = await BeginAsync(host, browser, platform);
        var token = platform.DeepLinkToken(nonce, username: user.UserName, returnUrl: "");

        var response = await browser.PostAsync("/api/v1/lti/launch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = state,
                ["id_token"] = token,
            }));

        Assert.Contains("/lti/failed", response.Headers.Location!.OriginalString);
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

    /// <summary>
    /// One client through the whole flow, because the launch's session cookie is
    /// what makes the choosing this person's.
    /// </summary>
    private static HttpClient Browser(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private static async Task<(string State, string Nonce)> BeginAsync(
        WebApplicationFactory<Program> host, HttpClient browser, FakePlatform platform)
    {
        var response = await browser.PostAsync("/api/v1/lti/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["iss"] = platform.Issuer }));

        var query = HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        return (query["state"]!, query["nonce"]!);
    }

    /// <summary>
    /// Where the launch sent the browser, as the header spells it. A string
    /// rather than a <c>Uri</c>: this redirect is relative, and every convenience
    /// on <c>Uri</c> — <c>Query</c>, <c>PathAndQuery</c> — throws on one.
    /// </summary>
    private static async Task<string> ChooseAsync(
        WebApplicationFactory<Program> host, HttpClient browser, FakePlatform platform,
        string username, bool acceptMultiple = true)
    {
        var (state, nonce) = await BeginAsync(host, browser, platform);
        var token = platform.DeepLinkToken(
            nonce, username: username, acceptMultiple: acceptMultiple);

        var response = await browser.PostAsync("/api/v1/lti/launch",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = state,
                ["id_token"] = token,
            }));

        var location = response.Headers.Location?.OriginalString
            ?? throw new Xunit.Sdk.XunitException(
                $"the launch did not redirect: {(int)response.StatusCode} "
                + await response.Content.ReadAsStringAsync());

        Assert.DoesNotContain("/lti/failed", location);

        return location;
    }

    private static string CodeOf(string landing)
    {
        var at = landing.IndexOf('?');
        var code = at < 0 ? null : HttpUtility.ParseQueryString(landing[at..])["code"];
        return code ?? throw new Xunit.Sdk.XunitException($"no code in {landing}");
    }

    private static async Task<JsonElement> OpenAsync(HttpClient browser, string code)
    {
        var response = await browser.GetAsync($"/api/v1/lti/deep-link/{code}");
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> RespondAsync(
        HttpClient browser, string code, string activityId)
    {
        var response = await browser.PostAsJsonAsync(
            $"/api/v1/lti/deep-link/{code}/response", new { activityIds = new[] { activityId } });
        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The id the screen was given for an activity, found by its slug.</summary>
    private static async Task<string> IdOfAsync(HttpClient browser, string code, string slug)
    {
        var view = await OpenAsync(browser, code);
        var candidate = view.GetProperty("activities").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("slug").GetString() == slug);

        Assert.True(candidate.ValueKind == JsonValueKind.Object, $"{slug} was not offered");
        return candidate.GetProperty("id").GetString()!;
    }

    private static JsonElement Claim(JsonWebToken token, string name) =>
        JsonDocument.Parse(Base64Url(token.EncodedPayload)).RootElement.GetProperty(name);

    private static byte[] Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }

    /// <summary>An account the platform is trusted to name.</summary>
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
                Issuer = "https://sso.deep-link.invalid",
                ClientId = "algojudge",
                ClientSecret = "development-only",
            };
            core.IdentityProviders.Add(provider);
            await core.SaveChangesAsync();
        }

        var user = new User { UserName = "dl-" + Guid.NewGuid().ToString("N")[..10], ApprovedAt = DateTime.UtcNow };
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

    /// <summary>
    /// The rights that let somebody place an activity, which are the rights that
    /// let them edit it. Granted on the activity rather than the system, so the
    /// test says something about scope as well as about permission.
    /// </summary>
    private async Task MayEditAsync(string userId, string slug)
    {
        await using var context = server.NewContext();
        var activity = await context.Activities.FirstAsync(a => a.Slug == slug);

        context.Grants.Add(new Grant
        {
            UserId = userId,
            ActivityId = activity.Id,
            Permissions = $"""["{Permissions.ActivityUpdate}"]""",
        });
        await context.SaveChangesAsync();
    }
}

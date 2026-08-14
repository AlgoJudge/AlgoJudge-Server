using System.Text.Json;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What reaches a gradebook, and what must not.
/// <para>
/// The AGS client is exercised against a gradebook that behaves like a real one
/// in the way that matters: <b>it accepts a stale timestamp and ignores it</b>.
/// That is the trap §6.4 names — a retry reusing the original result's time
/// succeeds and changes nothing — and a stub answering 200 to everything would
/// hide it completely.
/// </para>
/// </summary>
public class LtiGradeTests
{
    [Fact]
    public async Task A_score_carries_what_makes_Moodle_show_it_as_graded()
    {
        var (ags, gradebook, platform) = Build();

        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "moodle-user-1",
            score: 42, scoreMaximum: 50, timestamp: DateTime.UtcNow, graded: true,
            CancellationToken.None);

        var body = JsonDocument.Parse(gradebook.BodyFor("/scores")).RootElement;

        Assert.Equal("moodle-user-1", body.GetProperty("userId").GetString());
        Assert.Equal(42, body.GetProperty("scoreGiven").GetDouble());
        Assert.Equal(50, body.GetProperty("scoreMaximum").GetDouble());
        // §6.5, and the reason both are adopted: together they are what makes a
        // platform show "submitted, awaiting a grade" rather than an empty cell
        // that reads as nothing having been handed in.
        Assert.Equal("Completed", body.GetProperty("activityProgress").GetString());
        Assert.Equal("FullyGraded", body.GetProperty("gradingProgress").GetString());
    }

    [Fact]
    public async Task A_score_that_is_not_yet_marked_says_so_rather_than_looking_absent()
    {
        var (ags, gradebook, platform) = Build();

        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "moodle-user-1",
            score: 0, scoreMaximum: 50, timestamp: DateTime.UtcNow, graded: false,
            CancellationToken.None);

        var body = JsonDocument.Parse(gradebook.BodyFor("/scores")).RootElement;
        Assert.Equal("Pending", body.GetProperty("gradingProgress").GetString());
    }

    /// <summary>
    /// Moodle's line item URLs carry their identifiers in the query string.
    /// Appending <c>/scores</c> naively produces a URL that 404s while looking
    /// entirely correct in a log.
    /// </summary>
    [Fact]
    public async Task The_scores_path_goes_before_the_query_string()
    {
        var (ags, gradebook, platform) = Build();

        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 1, 1,
            DateTime.UtcNow, true, CancellationToken.None);

        var url = gradebook.Urls.Last(u => u.Contains("/scores"));
        Assert.Contains("/lineitem/scores?", url);
        Assert.DoesNotContain("?type_id=1/scores", url);
    }

    /// <summary>
    /// What Moodle actually does with a stale timestamp, measured 2026-08-14
    /// against 4.5.13, 5.2.2 and 5.3dev: <b>409, with a sentence saying so.</b>
    /// <para>
    /// And the resolution matters as much as the refusal: the comparison goes
    /// through <c>strtotime</c>, so a post a millisecond later is <i>the same
    /// second</i> and is refused too. That is why the worker moves by a whole
    /// second rather than by the smallest amount that looks newer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_timestamp_in_the_same_second_is_refused_by_name()
    {
        var (ags, gradebook, platform) = Build();
        var stamp = DateTime.UtcNow;

        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 10, 100, stamp, true,
            CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<AgsException>(() =>
            ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 90, 100,
                stamp.AddMilliseconds(1), true, CancellationToken.None));

        Assert.Contains("409", refusal.Message);
        Assert.Contains("earlier timestamp", refusal.Message);
        Assert.Equal(10, gradebook.Held["u"].Score);

        // A second later does land, which is what the worker's bump is sized for.
        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 90, 100,
            stamp.AddSeconds(1), true, CancellationToken.None);

        Assert.Equal(90, gradebook.Held["u"].Score);
    }

    /// <summary>
    /// The other behaviour the specification permits, and the one §6.4 assumed:
    /// accepted, ignored, reported as success. No platform in the reference
    /// stack does this — but a tool that only survived Moodle's answer would
    /// mark a grade synchronised that never arrived.
    /// </summary>
    [Fact]
    public async Task A_platform_that_drops_a_stale_score_silently_still_gets_a_newer_one()
    {
        var (ags, gradebook, platform) = Build();
        gradebook.DropsStaleSilently = true;
        var stamp = DateTime.UtcNow;

        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 10, 100, stamp, true,
            CancellationToken.None);
        // No exception, and no change: exactly the failure that is invisible
        // without the verifier.
        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 90, 100, stamp, true,
            CancellationToken.None);

        Assert.Equal(10, gradebook.Held["u"].Score);

        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 90, 100,
            stamp.AddSeconds(1), true, CancellationToken.None);

        Assert.Equal(90, gradebook.Held["u"].Score);
    }

    [Fact]
    public async Task A_line_item_is_created_with_our_own_identifier_on_it()
    {
        var (ags, gradebook, platform) = Build();
        var assignment = Guid.NewGuid().ToString("D");

        var url = await ags.EnsureLineItemAsync(platform, FakeGradebook.LineItemsUrl,
            "rl-1", assignment, "Sum of two numbers", 50, CancellationToken.None);

        Assert.Equal(gradebook.LineItemUrl, url);

        var created = JsonDocument.Parse(gradebook.Bodies.Last(b => b.Contains("scoreMaximum")))
            .RootElement;
        // §6.5 — the platform carries it and returns it with every result, which
        // is what lets a column be found again instead of duplicated.
        Assert.Equal(assignment, created.GetProperty("resourceId").GetString());
        Assert.Equal(50, created.GetProperty("scoreMaximum").GetDouble());
        // Named after the assignment, because a column named after an identifier
        // is a column nobody can find.
        Assert.Equal("Sum of two numbers", created.GetProperty("label").GetString());
    }

    /// <summary>
    /// AGS identifies its payloads by content type and a platform answers 400 to
    /// <c>application/json</c>. The failure then reads as "the tool sent
    /// rubbish", which sends whoever debugs it anywhere but the header.
    /// </summary>
    [Fact]
    public async Task The_media_types_are_the_ones_AGS_defines()
    {
        var (ags, gradebook, platform) = Build();

        await ags.EnsureLineItemAsync(platform, FakeGradebook.LineItemsUrl, "rl", "r", "l", 1,
            CancellationToken.None);
        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 1, 1, DateTime.UtcNow, true,
            CancellationToken.None);

        var lineItem = gradebook.Requests.Last(r =>
            r.Method == HttpMethod.Post && !r.RequestUri!.ToString().Contains("/scores")
            && !r.RequestUri.ToString().Contains("token"));
        Assert.Equal("application/vnd.ims.lis.v2.lineitem+json",
            lineItem.Content!.Headers.ContentType!.MediaType);

        var score = gradebook.Requests.Last(r => r.RequestUri!.ToString().Contains("/scores"));
        Assert.Equal("application/vnd.ims.lis.v1.score+json",
            score.Content!.Headers.ContentType!.MediaType);
    }

    /// <summary>
    /// The verifier's half: what the platform actually holds, which is how drift
    /// this module did not cause — a teacher editing a grade by hand, a course
    /// restored from backup — becomes visible at all.
    /// </summary>
    [Fact]
    public async Task What_the_platform_holds_can_be_read_back()
    {
        var (ags, gradebook, platform) = Build();

        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u1", 30, 100,
            DateTime.UtcNow, true, CancellationToken.None);

        var results = await ags.ReadResultsAsync(platform, gradebook.LineItemUrl,
            CancellationToken.None);

        var read = Assert.Single(results);
        Assert.Equal("u1", read.UserId);
        Assert.Equal(30, read.ResultScore);
    }

    [Fact]
    public async Task A_refused_score_says_what_the_platform_said()
    {
        var (ags, gradebook, platform) = Build();
        gradebook.RefuseScoresWith = System.Net.HttpStatusCode.Forbidden;

        var refusal = await Assert.ThrowsAsync<AgsException>(() =>
            ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 1, 1, DateTime.UtcNow, true,
                CancellationToken.None));

        Assert.Contains("403", refusal.Message);
        // The platform's own words travel, because "synchronisation failed" is
        // not something an operator can act on.
        Assert.Contains("the gradebook says no", refusal.Message);
    }

    /// <summary>
    /// A token per platform rather than per request. Three calls, one grant —
    /// the cache is the reason this is a singleton at all.
    /// </summary>
    [Fact]
    public async Task An_access_token_is_minted_once_and_reused()
    {
        var (ags, gradebook, platform) = Build();

        for (var i = 0; i < 3; i++)
        {
            await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", i, 10,
                DateTime.UtcNow.AddSeconds(i), true, CancellationToken.None);
        }

        Assert.Equal(1, gradebook.TokensIssued);
    }

    [Fact]
    public async Task The_assertion_authenticates_the_tool_with_no_secret_anywhere()
    {
        var (ags, gradebook, platform) = Build();

        await ags.PostScoreAsync(platform, gradebook.LineItemUrl, "u", 1, 1, DateTime.UtcNow, true,
            CancellationToken.None);

        var grant = gradebook.BodyFor("token.php");
        Assert.Contains("grant_type=client_credentials", grant);
        Assert.Contains("urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer", grant);
        Assert.Contains("client_assertion=", grant);
        // There is no secret in this protocol and none is invented.
        Assert.DoesNotContain("client_secret", grant);
    }

    // ── Getting there ────────────────────────────────────────────────────────

    /// <summary>
    /// An AGS client wired to an in-memory gradebook, with a real tool key
    /// generated behind it — the assertion is genuinely signed.
    /// </summary>
    private static (IAgsClient Ags, FakeGradebook Gradebook, Platform Platform) Build()
    {
        var gradebook = new FakeGradebook();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        // **The tool key is stubbed, and only the tool key.** Everything under
        // test here is what goes on the wire; a real `ToolKeyService` would drag
        // in the module's database for a value this test does not look at. The
        // key is still a real RSA key, so the assertion is genuinely signed.
        services.AddSingleton<IToolKeyService>(new StubbedToolKey());
        services.AddSingleton<IPlatformTokens, PlatformTokens>();
        services.AddScoped<IAgsClient, AgsClient>();

        services.AddHttpClient(nameof(PlatformTokens))
            .ConfigurePrimaryHttpMessageHandler(() => gradebook);
        services.AddHttpClient(nameof(AgsClient))
            .ConfigurePrimaryHttpMessageHandler(() => gradebook);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        return (
            scope.ServiceProvider.GetRequiredService<IAgsClient>(),
            gradebook,
            new Platform
            {
                DisplayName = "Fake",
                Issuer = "https://platform.invalid",
                ClientId = "client-1",
                DeploymentId = "1",
                KeySetUrl = "https://platform.invalid/certs",
                AuthTokenUrl = FakeGradebook.TokenUrl,
                AuthLoginUrl = "https://platform.invalid/auth",
            });
    }
}

/// <summary>A real RSA key, without the database the real service reads it from.</summary>
file sealed class StubbedToolKey : IToolKeyService
{
    private readonly System.Security.Cryptography.RSA rsa =
        System.Security.Cryptography.RSA.Create(2048);

    public Task<ToolKey> CurrentAsync(CancellationToken ct) =>
        Task.FromResult(new ToolKey
        {
            Kid = "test",
            PublicPem = rsa.ExportSubjectPublicKeyInfoPem(),
            PrivatePem = rsa.ExportPkcs8PrivateKeyPem(),
        });

    public Task<object> KeySetAsync(CancellationToken ct) => Task.FromResult<object>(new { keys = Array.Empty<object>() });

    public Task<Microsoft.IdentityModel.Tokens.SigningCredentials> CredentialsAsync(CancellationToken ct) =>
        Task.FromResult(new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) { KeyId = "test" },
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256));
}

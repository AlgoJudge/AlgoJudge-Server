using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Lti.Data;
using AlgoJudge.Server.Lti.Services;
using AlgoJudge.Server.Lti.Workers;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The whole loop: somebody launches, submits, is judged, and the number turns up
/// in a gradebook — then the states that say why it sometimes should not.
/// <para>
/// The platform here is a <see cref="FakeGradebook"/> rather than a stub, so a
/// stale timestamp is accepted and ignored exactly as a real one would.
/// </para>
/// </summary>
[Collection("server")]
public class LtiGradeSyncTests(ServerFixture server)
{
    private const string Directory = "sync-directory";

    [Fact]
    public async Task A_judged_submission_reaches_the_gradebook()
    {
        var world = await BuildAsync();

        var posted = await world.SweepAsync();

        Assert.True(posted >= 1, "the sweep posted nothing");
        Assert.True(world.Gradebook.Held.ContainsKey(world.Subject),
            "the gradebook holds nothing for the person who submitted");

        var summary = await world.SummaryAsync();
        Assert.Equal(1, summary.GetProperty("synchronised").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
    }

    /// <summary>
    /// §6.3 — a gradebook column is a participant-visible surface, so a score
    /// reaches it exactly when the participant may see it. Under
    /// <c>managersOnly</c> that is never: posting would publish through the
    /// platform precisely what the activity withholds.
    /// </summary>
    [Fact]
    public async Task A_managers_only_activity_posts_nothing_ever()
    {
        var world = await BuildAsync(scoreVisibility: ScoreVisibility.ManagersOnly);

        await world.SweepAsync();

        // **About this person, not about the gradebook being empty.** The worker
        // sweeps every placement in the installation, which is correct, and the
        // suite shares one database — so another test's grade legitimately
        // arrives through this handler. Asserting emptiness would make this test
        // fail for somebody else's reasons.
        Assert.DoesNotContain(world.Subject, world.Gradebook.Held.Keys);

        var summary = await world.SummaryAsync();
        Assert.Equal(1, summary.GetProperty("withheld").GetInt32());
        Assert.Equal(0, summary.GetProperty("synchronised").GetInt32());
        // Withheld is not failed, and a screen must not read as broken when it
        // is behaving exactly as configured.
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
    }

    /// <summary>
    /// During a freeze the teacher does not see it either, and §6.3 accepts that
    /// cost: a gradebook has no equivalent of reading an unfrozen ranking.
    /// </summary>
    [Fact]
    public async Task A_frozen_round_defers_rather_than_failing()
    {
        var world = await BuildAsync(freeze: true);

        await world.SweepAsync();

        Assert.DoesNotContain(world.Subject, world.Gradebook.Held.Keys);

        var summary = await world.SummaryAsync();
        Assert.Equal(1, summary.GetProperty("deferred").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
    }

    /// <summary>
    /// <b>The silent one.</b> A rejudge produces a new number for a submission
    /// already posted; if the retry reused the first timestamp the platform would
    /// answer success and keep the old grade. Nothing but this test and the
    /// verifier would ever say so.
    /// </summary>
    [Fact]
    public async Task A_second_posting_moves_the_grade_rather_than_being_dropped()
    {
        var world = await BuildAsync();
        await world.SweepAsync();

        var first = world.Gradebook.Held[world.Subject].Score;

        await world.RejudgeAsync(newScore: 100);
        var posted = await world.SweepAsync();

        Assert.True(posted >= 1, "the changed grade was never sent");
        Assert.NotEqual(first, world.Gradebook.Held[world.Subject].Score);
    }

    /// <summary>
    /// <b>A synchronised grade is not sent again, and this is the test that was
    /// missing.</b> Until it existed, every sweep moved every synchronised row
    /// back to pending — so every grade in the installation was reposted every
    /// minute, for ever, against somebody else's Moodle. It looked like working
    /// software.
    /// </summary>
    [Fact]
    public async Task A_settled_grade_is_not_posted_again_on_every_sweep()
    {
        var world = await BuildAsync();
        await world.SweepAsync();

        var after = world.Gradebook.Urls.Count(u => u.Contains("/scores"));

        await world.SweepAsync();
        await world.SweepAsync();

        Assert.Equal(after, world.Gradebook.Urls.Count(u => u.Contains("/scores")));
    }

    [Fact]
    public async Task A_platform_that_refuses_leaves_a_reason_a_person_can_read()
    {
        var world = await BuildAsync();
        world.Gradebook.RefuseScoresWith = System.Net.HttpStatusCode.Forbidden;

        await world.SweepAsync();

        var summary = await world.SummaryAsync();
        Assert.Equal(0, summary.GetProperty("synchronised").GetInt32());
        Assert.Equal(1, summary.GetProperty("pending").GetInt32());
        // Not a status code and not "synchronisation failed": the platform's own
        // words, because that is what an operator can act on.
        Assert.Contains("the gradebook says no", summary.GetProperty("lastError").GetString()!);
    }

    /// <summary>
    /// Reporting rather than repairing. A teacher who changed a grade on purpose
    /// must not have it overwritten by a sweep they did not run.
    /// </summary>
    [Fact]
    public async Task A_grade_edited_at_the_platform_is_reported_and_not_overwritten()
    {
        var world = await BuildAsync();
        await world.SweepAsync();

        var ours = world.Gradebook.Held[world.Subject].Score;

        // A teacher edits the grade in the platform. **Stamped a moment ago**,
        // which is the realistic case: a first attempt used a year hence and
        // showed something worth knowing — a resync cannot overwrite an entry
        // stamped in the future, because AGS drops the older timestamp. The
        // worker names that limit; it is not one this test should paper over by
        // pretending clocks disagree.
        world.Gradebook.Held[world.Subject] = (ours + 17, DateTime.UtcNow.AddSeconds(-1));

        // A sweep does not touch it: the row is already synchronised.
        await world.SweepAsync();
        Assert.Equal(ours + 17, world.Gradebook.Held[world.Subject].Score);

        var verified = await world.SummaryAsync(verify: true);
        Assert.Equal(1, verified.GetProperty("drifted").GetInt32());

        // And a resync is what puts ours back — deliberate, with a button.
        var resync = await world.Manager.PostAsync(
            $"/api/v1/lti/placements/{world.LinkId}/grades/resync", null);
        resync.EnsureSuccessStatusCode();

        await world.SweepAsync();
        Assert.Equal(ours, world.Gradebook.Held[world.Subject].Score);
    }

    /// <summary>
    /// <b>The rule the wall clock was hiding.</b> Between two sweeps real time
    /// moves by seconds, so a timestamp rises whether or not anything makes it —
    /// which meant the monotonic bump could be deleted and every test stayed
    /// green. With the clock stopped, the second post carries the same instant as
    /// the first, and only the bump saves it from being accepted and dropped.
    /// </summary>
    [Fact]
    public async Task Two_postings_in_the_same_instant_still_move_the_grade()
    {
        var frozen = new FrozenClock(DateTimeOffset.UtcNow);
        var world = await BuildAsync(clock: frozen);

        await world.SweepAsync();
        var first = world.Gradebook.Held[world.Subject].Score;

        await world.RejudgeAsync(newScore: 100);
        await world.SweepAsync();

        Assert.NotEqual(first, world.Gradebook.Held[world.Subject].Score);
    }

    // ── The world these tests run in ─────────────────────────────────────────

    private sealed record World(
        WebApplicationFactory<Program> Host,
        FakeGradebook Gradebook,
        HttpClient Manager,
        Guid LinkId,
        string Subject,
        Guid SubmissionId)
    {
        public async Task<int> SweepAsync()
        {
            var worker = Host.Services.GetServices<IHostedService>()
                .OfType<GradeSyncWorker>().Single();
            return await worker.RunOnceAsync(CancellationToken.None);
        }

        public async Task<JsonElement> SummaryAsync(bool verify = false)
        {
            var response = await Manager.GetAsync(
                $"/api/v1/lti/placements/{LinkId}/grades?verify={verify.ToString().ToLowerInvariant()}");
            Assert.True(response.IsSuccessStatusCode,
                $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        /// <summary>A new, better result for the same submission.</summary>
        public async Task RejudgeAsync(double newScore)
        {
            using var scope = Host.Services.CreateScope();
            var core = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var job = await core.EvaluationJobs.FirstAsync(j => j.SubmissionId == SubmissionId);
            core.Results.Add(new Result
            {
                EvaluationJobId = job.Id,
                ProblemVersionId = (await core.Results
                    .FirstAsync(r => r.EvaluationJobId == job.Id)).ProblemVersionId,
                Score = newScore,
                MaxScore = 100,
                Verdict = "OK",
            });
            await core.SaveChangesAsync();
        }
    }

    private async Task<World> BuildAsync(
        ScoreVisibility scoreVisibility = ScoreVisibility.Everyone,
        bool freeze = false,
        TimeProvider? clock = null)
    {
        using var platform = new FakePlatform();
        var gradebook = new FakeGradebook();

        var host = server.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IPlatformKeys>(
                new StubbedPlatformKeys(platform.SigningKey)));
            services.AddHttpClient(nameof(PlatformTokens))
                .ConfigurePrimaryHttpMessageHandler(() => gradebook);
            services.AddHttpClient(nameof(AgsClient))
                .ConfigurePrimaryHttpMessageHandler(() => gradebook);

            if (clock is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(clock));
            }
        }));

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        // Registered against the fake gradebook's own token endpoint, so the
        // client-credentials grant goes somewhere that answers.
        var registration = new
        {
            displayName = "Gradebook platform",
            issuer = platform.Issuer,
            clientId = platform.ClientId,
            deploymentId = platform.DeploymentId,
            keySetUrl = platform.Issuer + "/certs",
            authTokenUrl = FakeGradebook.TokenUrl,
            authLoginUrl = platform.Issuer + "/auth",
            isIdentityAuthority = true,
            identityNamespace = Directory,
        };
        (await admin.PostAsJsonAsync("/api/v1/lti/platforms", registration))
            .EnsureSuccessStatusCode();

        var (slug, roundId) = await Build.ActivityAsync(server);
        var user = await DirectoryUserAsync();

        await ConfigureAsync(slug, roundId, scoreVisibility, freeze);

        // A launch, so the placement and the identity link exist the way they
        // would in life rather than being written by hand.
        var link = await LaunchAsync(host, platform, user.UserName!, slug);

        var participant = await Sign.InAsync(server, user.UserName!, await PasswordAsync(user));
        var submission = await Build.SubmitAsync(participant, slug, "print(2)\n");
        var submissionId = Guid.Parse(submission.GetProperty("id").GetString()!);
        await JudgeAsync(submissionId, 50);

        // **Signed in against `host`, not against the fixture.** The verifier
        // reaches the platform through the `AgsClient` HttpClient, and only this
        // host has the fake gradebook behind it — a manager client from the base
        // fixture sends the verification request into the real world, where it
        // fails and is skipped, and drift silently reads as zero.
        var manager = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        return new World(host, gradebook, manager, link, "sub-" + user.UserName, submissionId);
    }

    private async Task ConfigureAsync(
        string slug, string roundId, ScoreVisibility visibility, bool freeze)
    {
        using var scope = server.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var activity = await core.Activities.FirstAsync(a => a.Slug == slug);
        activity.ScoreVisibility = visibility;

        if (freeze)
        {
            var series = await core.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
            series.RankingFreezeAt = DateTime.UtcNow.AddMinutes(-5);
            series.RankingRevealAt = null;
        }

        await core.SaveChangesAsync();
    }

    /// <summary>A result for the submission, as a Runner would have produced.</summary>
    private async Task JudgeAsync(Guid submissionId, double score)
    {
        using var scope = server.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var submission = await core.Submissions
            .Include(s => s.SeriesProblem!).ThenInclude(sp => sp.Problem)
            .FirstAsync(s => s.Id == submissionId);

        var version = await core.ProblemVersions
            .Where(v => v.ProblemId == submission.SeriesProblem!.ProblemId)
            .OrderByDescending(v => v.Version)
            .FirstAsync();

        var job = await core.EvaluationJobs.FirstOrDefaultAsync(j => j.SubmissionId == submissionId);
        if (job is null)
        {
            job = new EvaluationJob { SubmissionId = submissionId };
            core.EvaluationJobs.Add(job);
            await core.SaveChangesAsync();
        }

        core.Results.Add(new Result
        {
            EvaluationJobId = job.Id,
            ProblemVersionId = version.Id,
            Score = score,
            MaxScore = 100,
            Verdict = "OK",
        });
        await core.SaveChangesAsync();
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

        var user = new User { UserName = "sync-" + Guid.NewGuid().ToString("N")[..10] };
        Assert.True((await users.CreateAsync(user, Sign.Password)).Succeeded);

        core.UserIdentities.Add(new UserIdentity
        {
            UserId = user.Id,
            ProviderId = provider.Id,
            Subject = Guid.NewGuid().ToString("N"),
        });
        await core.SaveChangesAsync();

        return user;
    }

    private static Task<string> PasswordAsync(User user)
    {
        _ = user;
        return Task.FromResult(Sign.Password);
    }

    private static async Task<Guid> LaunchAsync(
        WebApplicationFactory<Program> host, FakePlatform platform, string username, string slug)
    {
        // Cookies kept, because the claim below is made with the session the
        // launch established.
        // **Over TLS.** A launch into a frame signs somebody in with a
        // `Secure` cookie, which a client on plain HTTP is handed and never
        // sends back — every request after the launch is then anonymous.
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
                    query["nonce"]!,
                    subject: "sub-" + username,
                    activitySlug: slug,
                    username: username),
            }));

        var landing = launched.Headers.Location!.ToString();
        Assert.Contains("/lti/launched", landing);

        // The placement is bought with the ticket, the way the Client does it.
        var ticket = HttpUtility.ParseQueryString(landing[(landing.IndexOf('?') + 1)..])["ticket"];
        Assert.False(string.IsNullOrWhiteSpace(ticket), $"no ticket in {landing}");

        var claimed = await client.PostAsJsonAsync(
            "/api/v1/lti/session/claim", new { ticket });
        Assert.True(claimed.IsSuccessStatusCode,
            $"{(int)claimed.StatusCode}: {await claimed.Content.ReadAsStringAsync()}");

        var context = await claimed.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(context.GetProperty("linkId").GetString()!);
    }
}

/// <summary>
/// A clock that does not move. There is no fake <c>TimeProvider</c> in this
/// project and one package for one test is a poor trade.
/// </summary>
file sealed class FrozenClock(DateTimeOffset at) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => at;
}

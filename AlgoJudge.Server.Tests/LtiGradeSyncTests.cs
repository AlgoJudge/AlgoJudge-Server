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
[Collection("server-3")]
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
    /// An excluded submission earns no grade, and the sweep carries that to a
    /// gradebook already written to — §8 forbids a hook, so the correction
    /// arrives one sweep later rather than on the event.
    /// </summary>
    [Fact]
    public async Task An_excluded_submission_earns_no_grade()
    {
        var world = await BuildAsync();
        await world.SweepAsync();

        var first = world.Gradebook.Held[world.Subject].Score;
        Assert.True(first > 0, "the setup posted nothing to move");

        // The only submission there is, ruled out. Written where the state
        // lives — the endpoint is `ExclusionTests`' business.
        using (var scope = world.Host.Services.CreateScope())
        {
            var core = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var submission = await core.Submissions.FirstAsync(s => s.Id == world.SubmissionId);
            submission.ExcludedAt = DateTime.UtcNow;
            await core.SaveChangesAsync();
        }

        var posted = await world.SweepAsync();

        // **Zero, not "unchanged".** Dropping the submission alone leaves this
        // contestant out of the computation, and a row nobody computes is a row
        // nobody corrects — the platform would hold the old mark for ever.
        Assert.True(posted >= 1, "the withdrawn grade was never sent");
        Assert.Equal(0, world.Gradebook.Held[world.Subject].Score);
    }

    /// <summary>
    /// <b>The best attempt is the best fraction, not the biggest number.</b>
    ///
    /// <para>
    /// A gradebook column takes one grade per person and §6.2 says it is their
    /// best attempt. That was chosen by ordering on the raw score, which
    /// compares numbers marked out of different maxima — a package republished
    /// with more tests, or an external judge marking out of one. So 70 out of
    /// 100 beat 1 out of 1, and the platform was sent the **worse** of somebody's
    /// two attempts.
    /// </para>
    ///
    /// <para>
    /// Every other reader in the product had already been moved to fractions
    /// when the same defect was found on 2026-08-16. This query was missed, and
    /// no test could see it while every attempt in this file was marked out of a
    /// hundred.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_best_attempt_is_the_best_fraction_and_not_the_biggest_number()
    {
        var world = await BuildAsync();
        await world.SweepAsync();

        var first = world.Gradebook.Held[world.Subject].Score;

        // A perfect answer on a scale of one. Its raw number is smaller than
        // anything already there and its fraction is the largest there can be.
        await world.AttemptAsync(score: 1, outOf: 1);
        await world.SweepAsync();

        // The setup judged the first attempt 50 out of 100 — half marks. The
        // second is 1 out of 1, whose raw number is fifty times smaller and
        // whose fraction is twice as good, so the grade must go **up**.
        // Ordering on the raw score leaves it exactly where it was.
        var sent = world.Gradebook.Held[world.Subject].Score;
        Assert.True(sent > first, $"the gradebook kept {first} and was sent {sent}");
    }

    /// <summary>
    /// <b>A synchronised grade is not sent again, and this is the test that was
    /// missing.</b> Until it existed, every sweep moved every synchronised row
    /// back to pending — so every grade in the installation was reposted every
    /// minute, for ever, against somebody else's Moodle. It looked like working
    /// software.
    ///
    /// <para>
    /// <b>Counted for this world's own person, not across every URL.</b> It
    /// counted them all until 2026-08-22 and went red two runs in three: the
    /// sweep is global, so it posts whatever an earlier test left pending, and
    /// it posts it through the stub belonging to whichever test is sweeping.
    /// The claim here is about <i>this</i> grade, and now so is the measurement.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_settled_grade_is_not_posted_again_on_every_sweep()
    {
        var world = await BuildAsync();
        await world.SweepAsync();

        var after = world.Gradebook.Posts.GetValueOrDefault(world.Subject);
        Assert.Equal(1, after);

        await world.SweepAsync();
        await world.SweepAsync();

        Assert.Equal(after, world.Gradebook.Posts.GetValueOrDefault(world.Subject));
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

    /// <summary>
    /// A group's grade reaches every member the platform knows.
    /// <para>
    /// One submission, sent by one member, and two gradebook rows carrying the
    /// same score — because what competed was the group, and a mark is a fact
    /// about the contestant rather than about whoever happened to press the
    /// button.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_groups_grade_reaches_every_member()
    {
        var world = await BuildAsync(grouped: true);

        var posted = await world.SweepAsync();
        Assert.True(posted >= 2, $"the sweep posted {posted} score(s) for a group of two");

        Assert.True(world.Gradebook.Held.ContainsKey(world.Subject),
            "the member who submitted has no grade");
        Assert.True(world.Gradebook.Held.ContainsKey(world.Teammate!),
            "the member who submitted nothing has no grade");

        // The same score, not merely a score each: the group earned one mark.
        Assert.Equal(
            world.Gradebook.Held[world.Subject].Score,
            world.Gradebook.Held[world.Teammate!].Score);
    }

    // ── The world these tests run in ─────────────────────────────────────────

    private sealed record World(
        WebApplicationFactory<Program> Host,
        FakeGradebook Gradebook,
        HttpClient Manager,
        Guid LinkId,
        string Subject,
        Guid SubmissionId,
        /// <summary>
        /// The other member, when this world was built with a group. Their work
        /// is nobody's: the grade comes from the first member's submission, and
        /// this is who else must be given it.
        /// </summary>
        string? Teammate = null)
    {
        public async Task<int> SweepAsync()
        {
            // By type, not out of the hosted services: the fixture switches the
            // worker off there so it cannot sweep the shared database on its own
            // timer, and registers it as a plain singleton for exactly this.
            var worker = Host.Services.GetRequiredService<GradeSyncWorker>();
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

        /// <summary>
        /// <b>A second attempt, the way a rejudge actually makes one</b>: a new
        /// job with its own result, so both attempts survive and the selection
        /// rule has something to select between.
        ///
        /// <para>
        /// `RejudgeAsync` below adds a result to the *existing* job and leaves
        /// one attempt standing, which is fine for the tests that use it and
        /// useless for this one — a test written against it passed whichever way
        /// the best attempt was chosen, because there was only ever one.
        /// </para>
        /// </summary>
        public async Task AttemptAsync(double score, double outOf)
        {
            using var scope = Host.Services.CreateScope();
            var core = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existing = await core.EvaluationJobs
                .Where(j => j.SubmissionId == SubmissionId)
                .OrderByDescending(j => j.Attempt)
                .FirstAsync();
            var version = (await core.Results.FirstAsync(r => r.EvaluationJobId == existing.Id))
                .ProblemVersionId;

            var job = new EvaluationJob
            {
                SubmissionId = SubmissionId,
                Attempt = existing.Attempt + 1,
                ProblemVersionId = version,
                State = EvaluationJobState.Completed,
                FinishedAt = DateTime.UtcNow,
            };
            core.EvaluationJobs.Add(job);
            core.Results.Add(new Result
            {
                EvaluationJobId = job.Id,
                ProblemVersionId = version,
                Score = score,
                MaxScore = outOf,
                Verdict = "OK",
            });
            await core.SaveChangesAsync();
        }

        /// <summary>
        /// A new result for the same submission.
        ///
        /// <para>
        /// <b>`outOf` is a parameter now.</b> It was fixed at 100, so every
        /// attempt in these tests was marked on one scale — which is the one
        /// scale on which comparing raw scores and comparing fractions give the
        /// same answer, and therefore the one scale on which the selection rule
        /// cannot be tested.
        /// </para>
        /// </summary>
        public async Task RejudgeAsync(double newScore, double outOf = 100)
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
                MaxScore = outOf,
                Verdict = "OK",
            });
            await core.SaveChangesAsync();
        }
    }

    private async Task<World> BuildAsync(
        ScoreVisibility scoreVisibility = ScoreVisibility.Everyone,
        bool freeze = false,
        TimeProvider? clock = null,
        bool grouped = false)
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

        // **The group is made before anything is sent**, because a submission
        // stamps its group when it is made. Grouping people afterwards leaves
        // their earlier work exactly where it was — which is the rule, and which
        // this setup got wrong once and was told so by its own assertion.
        string? teammate = null;
        if (grouped)
        {
            var other = await DirectoryUserAsync();
            await LaunchAsync(host, platform, other.UserName!, slug);

            var made = await admin.PostAsJsonAsync(
                $"/api/v1/activities/{slug}/groups", new { name = "Zespół " + slug });
            made.EnsureSuccessStatusCode();
            var groupId = (await made.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString()!;

            foreach (var member in new[] { user, other })
            {
                (await admin.PutAsJsonAsync(
                    $"/api/v1/activities/{slug}/participants/{member.Id}/group",
                    new { groupId })).EnsureSuccessStatusCode();
            }

            teammate = "sub-" + other.UserName;
        }

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

        return new World(
            host, gradebook, manager, link, "sub-" + user.UserName, submissionId, teammate);
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

        var user = new User { UserName = "sync-" + Guid.NewGuid().ToString("N")[..10], ApprovedAt = DateTime.UtcNow };
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

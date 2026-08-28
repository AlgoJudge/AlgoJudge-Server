using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Who is handed which work.
/// <para>
/// <b>Three filters, and they are independent.</b> What a Runner can evaluate
/// (`problemTypes`), whether it forwards submissions outside (`external`), and
/// which pool an operator put it in (`tags`). The first two say what a machine
/// is able to do; only the third is a decision somebody takes on a Tuesday.
/// </para>
/// <para>
/// <b>The pools are matched by intersection</b> — one shared tag is enough,
/// unlike GitLab, where a runner must hold every tag a job asks for. Capability
/// is already answered above, so a tag here is a place rather than a
/// requirement, and an activity's list reads as "any of these" rather than "all
/// of these".
/// </para>
/// <para>
/// <b>Empty means `default`, on both sides</b>, and that is the whole of the
/// exclusivity: tagging a Runner takes it out of the general pool, and tagging
/// work takes it away from the general Runners. Neither half is written
/// anywhere, and neither can be forgotten.
/// </para>
/// </summary>
[Collection("server-2")]
public class RunnerRoutingTests(ServerFixture server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A submission waiting to be judged, and its id.</summary>
    private static async Task<string> SubmitAsync(ServerFixture server, string slug, string problem = "A")
    {
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n", problem);
        return submitted.GetProperty("id").GetString()!;
    }

    // ── the filter nothing had ever measured ──────────────────────────────────

    /// <summary>
    /// <b>The half §15 called "verify", and it was answered by reading.</b>
    /// `RunnerService` has matched a job against the Runner's declared problem
    /// types since it was written, and no test proved it: every Runner in this
    /// suite registered `standard-io@1` and every activity used it, so deleting
    /// the clause left the whole suite green.
    /// </summary>
    [Fact]
    public async Task A_runner_is_only_given_a_problem_type_it_declared()
    {
        var (slug, _) = await Build.ActivityAsync(server, problemType: "puzzle@1");
        var id = await SubmitAsync(server, slug);

        var wrong = await Build.RunnerAsync(server, problemTypes: ["standard-io@1"]);
        Assert.Null(await wrong.TryClaimForAsync(id));

        // And the job was there to be taken. Without this the assertion above is
        // satisfied by an empty queue, which proves nothing at all.
        var right = await Build.RunnerAsync(server, problemTypes: ["puzzle@1"]);
        Assert.NotNull(await right.TryClaimForAsync(id));
    }

    // ── the pools ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing changes for an installation that never types a tag. This is what
    /// makes the migration a non-event, and it is the row of the table most
    /// likely to be broken by a later "tidy-up" of the empty case.
    /// </summary>
    [Fact]
    public async Task An_untagged_runner_and_untagged_work_still_find_each_other()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var id = await SubmitAsync(server, slug);

        var runner = await Build.RunnerAsync(server);
        Assert.NotNull(await runner.TryClaimForAsync(id));
    }

    /// <summary>Pinning an activity to a laboratory, in both directions at once.</summary>
    [Fact]
    public async Task A_tagged_activity_is_served_only_by_a_runner_carrying_the_tag()
    {
        var (slug, _) = await Build.ActivityAsync(server, runnerTags: ["lab-a"]);
        var id = await SubmitAsync(server, slug);

        var general = await Build.RunnerAsync(server);
        Assert.Null(await general.TryClaimForAsync(id));

        var lab = await Build.RunnerAsync(server, tags: ["lab-a"]);
        Assert.NotNull(await lab.TryClaimForAsync(id));
    }

    /// <summary>
    /// <b>The half a preference would fail.</b> §15 asks for the reserved
    /// machines to be unavailable to everything else — so a Runner given a tag
    /// has to stop taking the general queue, not merely prefer its own.
    /// </summary>
    [Fact]
    public async Task A_tagged_runner_is_given_nothing_from_the_default_pool()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var id = await SubmitAsync(server, slug);

        var lab = await Build.RunnerAsync(server, tags: ["lab-a"]);
        Assert.Null(await lab.TryClaimForAsync(id));

        var general = await Build.RunnerAsync(server);
        Assert.NotNull(await general.TryClaimForAsync(id));
    }

    /// <summary>
    /// One shared tag is enough, and it is enough from either side — a Runner in
    /// more pools than the work names, and work naming more pools than the
    /// Runner is in. Under GitLab's rule the second of these would be refused.
    /// </summary>
    [Fact]
    public async Task One_shared_tag_is_enough_in_both_directions()
    {
        var (wide, _) = await Build.ActivityAsync(server, runnerTags: ["lab-a"]);
        var wideId = await SubmitAsync(server, wide);
        var many = await Build.RunnerAsync(server, tags: ["lab-a", "gpu"]);
        Assert.NotNull(await many.TryClaimForAsync(wideId));

        var (narrow, _) = await Build.ActivityAsync(server, runnerTags: ["lab-b", "gpu"]);
        var narrowId = await SubmitAsync(server, narrow);
        var one = await Build.RunnerAsync(server, tags: ["gpu"]);
        Assert.NotNull(await one.TryClaimForAsync(narrowId));
    }

    /// <summary>
    /// `default` is ordinary text anybody may type, and typing it means what
    /// leaving the field empty means. The owner asked for exactly this, and it
    /// is what lets a Runner serve the general pool <i>and</i> a laboratory.
    /// </summary>
    [Fact]
    public async Task The_default_tag_written_out_means_what_an_empty_list_means()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var id = await SubmitAsync(server, slug);

        var both = await Build.RunnerAsync(server, tags: ["lab-a", "default"]);
        Assert.NotNull(await both.TryClaimForAsync(id));

        var (lab, _) = await Build.ActivityAsync(server, runnerTags: ["lab-a"]);
        var labId = await SubmitAsync(server, lab);
        Assert.NotNull(await both.TryClaimForAsync(labId));
    }

    // ── the round overrides its activity ──────────────────────────────────────

    /// <summary>
    /// <b>The case an activity-only design gets wrong.</b> A course pinned whole
    /// sends its homework to the laboratory too — including whatever is
    /// submitted from home at night, while those machines are off. So the
    /// examination round carries the pin and the rest of the course does not.
    /// </summary>
    [Fact]
    public async Task A_rounds_own_tags_override_its_activitys()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await Build.SecondRoundAsync(server, slug, "exam", runnerTags: ["lab-a"]);

        var general = await Build.RunnerAsync(server);
        var lab = await Build.RunnerAsync(server, tags: ["lab-a"]);

        // **One submission at a time, and this is not tidiness.** A search that
        // ends in nothing has claimed every job it looked at on the way — so
        // queueing both first would let the refusal below swallow the homework
        // job, and the next assertion would fail for a reason nothing states.
        var exam = await SubmitAsync(server, slug, "B");
        Assert.Null(await general.TryClaimForAsync(exam));
        Assert.NotNull(await lab.TryClaimForAsync(exam));

        var homework = await SubmitAsync(server, slug);
        Assert.NotNull(await general.TryClaimForAsync(homework));
    }

    /// <summary>
    /// And the other way round: a course pinned to a laboratory, with one round
    /// pulled back out of it by naming `default`. There is no third state — an
    /// empty list on a round means inherit, as absent does.
    /// </summary>
    [Fact]
    public async Task A_round_naming_default_is_pulled_out_of_a_pinned_activity()
    {
        var (slug, _) = await Build.ActivityAsync(server, runnerTags: ["lab-a"]);
        await Build.SecondRoundAsync(server, slug, "hw", runnerTags: ["default"]);

        var general = await Build.RunnerAsync(server);
        var lab = await Build.RunnerAsync(server, tags: ["lab-a"]);

        var exam = await SubmitAsync(server, slug);
        Assert.Null(await general.TryClaimForAsync(exam));
        Assert.NotNull(await lab.TryClaimForAsync(exam));

        var homework = await SubmitAsync(server, slug, "B");
        Assert.NotNull(await general.TryClaimForAsync(homework));
    }

    /// <summary>An emptied list goes back to inheriting rather than storing an override.</summary>
    [Fact]
    public async Task Emptying_a_rounds_tags_puts_it_back_on_its_activitys()
    {
        var (slug, _) = await Build.ActivityAsync(server, runnerTags: ["lab-a"]);
        var roundId = await Build.SecondRoundAsync(server, slug, "hw", runnerTags: ["default"]);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/series/{roundId}", new
        {
            slug = "hw",
            name = "Homework",
            runnerTags = Array.Empty<string>(),
        }, Json));

        await using (var context = server.NewContext())
        {
            Assert.Null((await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId))).RunnerTags);
        }

        var id = await SubmitAsync(server, slug, "B");
        var general = await Build.RunnerAsync(server);
        Assert.Null(await general.TryClaimForAsync(id));

        var lab = await Build.RunnerAsync(server, tags: ["lab-a"]);
        Assert.NotNull(await lab.TryClaimForAsync(id));
    }

    // ── the second queue ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>Trials are the half that is easy to leave out.</b> There are two claim
    /// paths, and a reservation that covered only the first would leave a Runner
    /// held for an examination timing somebody's packages while it ran.
    /// </summary>
    [Fact]
    public async Task A_trial_follows_its_activitys_tags()
    {
        await ClearTrialsAsync();
        var (slug, _) = await Build.ActivityAsync(server, runnerTags: ["lab-a"]);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var packageFileId = await Build.UploadAsync(admin, "/api/v1/files", "package.zip", "bytes");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/trials", new
        {
            problemType = "standard-io@1", packageFileId, activityIdOrSlug = slug,
        }, Json));

        var general = await Build.RunnerAsync(server);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimTrialAsync(general)).StatusCode);

        var lab = await Build.RunnerAsync(server, tags: ["lab-a"]);
        Assert.Equal(HttpStatusCode.OK, (await ClaimTrialAsync(lab)).StatusCode);
    }

    /// <summary>
    /// A manager calibrating a problem in the library has no activity above the
    /// measurement, so it is general work — and a reserved Runner leaves it
    /// alone.
    /// </summary>
    [Fact]
    public async Task A_trial_with_no_activity_is_general_work()
    {
        await ClearTrialsAsync();
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var packageFileId = await Build.UploadAsync(admin, "/api/v1/files", "package.zip", "bytes");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/trials", new
        {
            problemType = "standard-io@1", packageFileId,
        }, Json));

        var lab = await Build.RunnerAsync(server, tags: ["lab-a"]);
        Assert.Equal(HttpStatusCode.NoContent, (await ClaimTrialAsync(lab)).StatusCode);

        var general = await Build.RunnerAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await ClaimTrialAsync(general)).StatusCode);
    }

    // ── the rest ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The pools are read when a job is claimed, not stamped on it when it is
    /// queued — so retagging redirects the work that is already waiting. The
    /// opposite is defensible and would leave yesterday's queue going to
    /// yesterday's Runners with nothing on any screen to say so.
    /// </summary>
    [Fact]
    public async Task Retagging_redirects_work_that_is_already_queued()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var id = await SubmitAsync(server, slug);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/activities/{slug}", new
        {
            slug,
            name = "Test activity",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            runnerTags = new[] { "lab-a" },
        }, Json));

        var general = await Build.RunnerAsync(server);
        Assert.Null(await general.TryClaimForAsync(id));

        var lab = await Build.RunnerAsync(server, tags: ["lab-a"]);
        Assert.NotNull(await lab.TryClaimForAsync(id));
    }

    /// <summary>
    /// Case and whitespace do not make two pools. Without this `Lab-A` on a
    /// Runner and `lab-a` on an activity are a queue that never drains, with
    /// nothing on any screen to say why.
    /// </summary>
    [Fact]
    public async Task Case_and_whitespace_do_not_make_two_pools()
    {
        var (slug, _) = await Build.ActivityAsync(server, runnerTags: ["  Lab-A  "]);
        var id = await SubmitAsync(server, slug);

        var lab = await Build.RunnerAsync(server, tags: ["LAB-a"]);
        Assert.NotNull(await lab.TryClaimForAsync(id));

        await using var context = server.NewContext();
        Assert.Equal(
            ["lab-a"],
            (await context.Activities.FirstAsync(a => a.Slug == slug)).RunnerTags);
    }

    /// <summary>
    /// <b>A restart does not move a Runner.</b> Registering again is how a
    /// Runner reports a restart, and every other field it reports is refreshed
    /// then — but a Runner that could re-declare its tags would put itself into
    /// an examination's pool with nobody approving it.
    /// </summary>
    [Fact]
    public async Task A_runner_declares_its_tags_once_and_a_restart_does_not_move_it()
    {
        var runner = await Build.RunnerAsync(server, tags: ["lab-a"]);

        // The same key, registering again — which is a restart, as far as the
        // Server can tell.
        await Build.PostAsync(server.CreateClient(), "/api/v1/runner/register", new
        {
            name = "stub",
            product = "AlgoJudge-Runner-Stub",
            version = "0.0.2",
            publicKey = runner.PublicKey,
            problemTypes = new[] { "standard-io@1" },
            tags = new[] { "lab-b" },
        });

        await using var context = server.NewContext();
        var stored = await context.Runners.FirstAsync(r => r.Id == Guid.Parse(runner.Id));
        Assert.Equal(["lab-a"], stored.Tags);
        // And the fields that are refreshed still are, so this is not a Server
        // that stopped listening.
        Assert.Equal("0.0.2", stored.Version);
    }

    /// <summary>
    /// The number beside the field. Tagging an activity nothing carries stops
    /// its judging in silence — the submissions are accepted, queued, and never
    /// claimed — so zero here is the only warning there can be.
    /// </summary>
    [Fact]
    public async Task A_manager_is_told_how_many_runners_reach_an_activity()
    {
        var (slug, _) = await Build.ActivityAsync(server, runnerTags: ["lab-empty"]);
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var before = await Build.GetAsync(admin, $"/api/v1/manager/activities/{slug}");
        Assert.Equal(0, before.GetProperty("matchingRunners").GetInt32());

        await Build.RunnerAsync(server, tags: ["lab-empty"]);

        var after = await Build.GetAsync(admin, $"/api/v1/manager/activities/{slug}");
        Assert.Equal(1, after.GetProperty("matchingRunners").GetInt32());
    }

    /// <summary>And per round, because a round is where the pin usually goes.</summary>
    [Fact]
    public async Task The_count_follows_a_rounds_own_tags()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        await Build.SecondRoundAsync(server, slug, "exam", runnerTags: ["lab-nobody"]);

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Build.RunnerAsync(server);

        var rounds = await Build.GetAsync(admin, $"/api/v1/manager/activities/{slug}/series");
        var bySlug = rounds.EnumerateArray().ToDictionary(r => r.GetProperty("slug").GetString()!);

        // The round inheriting the activity's empty list is reached by the
        // untagged Runner; the pinned one is reached by nobody.
        Assert.True(bySlug["r1"].GetProperty("matchingRunners").GetInt32() >= 1);
        Assert.Equal(0, bySlug["exam"].GetProperty("matchingRunners").GetInt32());
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> ClaimTrialAsync(StubRunner runner) =>
        runner.Client.PostAsJsonAsync("/api/v1/runner/trials/claim", new { leaseSeconds = 300 }, Json);

    /// <summary>
    /// The suite shares a database and a trial queue, so a test asserting that
    /// a Runner is given <b>nothing</b> has to start from an empty one — another
    /// test's leftover would be claimed instead and the assertion would be about
    /// that.
    /// </summary>
    private async Task ClearTrialsAsync()
    {
        server.CreateClient().Dispose();
        await using var context = server.NewContext();
        context.Trials.RemoveRange(await context.Trials.ToListAsync());
        await context.SaveChangesAsync();
    }
}

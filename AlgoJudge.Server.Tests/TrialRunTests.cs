using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A package somebody wants timed, from the request to the bytes being gone.
///
/// <para>
/// The rules being defended are the ones a screen cannot see: a trial is not a
/// submission, its package does not survive it, and one person cannot fill the
/// machines with them.
/// </para>
/// </summary>
[Collection("server")]
public class TrialRunTests(ServerFixture server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<Guid> UploadAsync(HttpClient client, string name, string content) =>
        Guid.Parse(await Build.UploadAsync(client, "/api/v1/files", name, content));

    /// <summary>
    /// Clears whatever this account left unfinished.
    ///
    /// <para>
    /// These tests share one database and one administrator, and the ceiling is
    /// counted per person on the **unfinished** — so a test that queues three
    /// and stops there spends the whole allowance of every test after it. That
    /// is the ceiling working; it is also a suite that passes or fails by
    /// running order, which is not a suite. Each test starts from nothing
    /// instead.
    /// </para>
    /// </summary>
    private async Task ClearTrialsAsync()
    {
        await using var context = server.NewContext();
        context.Trials.RemoveRange(await context.Trials.ToListAsync());
        await context.SaveChangesAsync();
    }

    /// <summary>In an activity, which is where a participant's trial lives.</summary>
    private static async Task<HttpResponseMessage> RequestTrialAsync(
        HttpClient client, Guid packageFileId, string type = "standard-io@1") =>
        await client.PostAsJsonAsync(
            "/api/v1/trials",
            new { problemType = type, packageFileId, activityIdOrSlug = "DEV-2026" },
            Json);

    /// <summary>Against the library, which is where a manager calibrates.</summary>
    private static async Task<HttpResponseMessage> RequestLibraryTrialAsync(
        HttpClient client, Guid packageFileId, string type = "standard-io@1") =>
        await client.PostAsJsonAsync(
            "/api/v1/trials",
            new { problemType = type, packageFileId },
            Json);

    /// <summary>
    /// The whole point: a trial is asked for, run, and the package is gone —
    /// while what was measured stays readable.
    /// </summary>
    [Fact]
    public async Task A_trial_is_measured_and_its_package_does_not_survive_it()
    {
        await ClearTrialsAsync();
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var fileId = await UploadAsync(admin, "package.zip", "not really a zip, but bytes");

        var created = await RequestTrialAsync(admin, fileId);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var trial = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var trialId = trial.GetProperty("id").GetString()!;

        Assert.Equal("queued", trial.GetProperty("state").GetString());
        Assert.True(trial.GetProperty("hasPackage").GetBoolean());
        // Never a verdict, never a score. A trial that carried either would
        // invite a screen to render it as a result.
        Assert.False(trial.TryGetProperty("score", out _));
        Assert.False(trial.TryGetProperty("verdict", out _));

        var runner = await Build.RunnerAsync(server);

        var claimed = await runner.Client.PostAsJsonAsync("/api/v1/runner/trials/claim", new { }, Json);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        var work = await claimed.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(trialId, work.GetProperty("trialId").GetString());
        Assert.Equal(fileId.ToString("D"), work.GetProperty("packageFileId").GetString());

        // The Runner may read the package it is holding, and only while it holds it.
        var download = await runner.Client.GetAsync($"/api/v1/runner/files/{fileId:D}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        var reported = await runner.Client.PostAsJsonAsync(
            $"/api/v1/runner/trials/{trialId}/report",
            new
            {
                leaseToken = work.GetProperty("leaseToken").GetString(),
                measurement = """{"groups":[{"group":1,"timeMs":240}]}""",
            },
            Json);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        Assert.False((await reported.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("duplicate").GetBoolean());

        var after = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/trials/{trialId}", Json);
        Assert.Equal("completed", after.GetProperty("state").GetString());
        Assert.Contains("timeMs", after.GetProperty("measurement").GetString());

        // **The row outlives the bytes** — which is why `PackageFileId` is nullable.
        Assert.False(after.GetProperty("hasPackage").GetBoolean());
        await using var context = server.NewContext();
        Assert.False(await context.Files.AnyAsync(f => f.Id == fileId));

        // And the Runner cannot read what it no longer holds.
        Assert.Equal(HttpStatusCode.NotFound,
            (await runner.Client.GetAsync($"/api/v1/runner/files/{fileId:D}")).StatusCode);
    }

    /// <summary>
    /// Reporting twice is not an error, and does not produce a second record —
    /// a Runner that missed the acknowledgement is still telling the truth.
    /// </summary>
    [Fact]
    public async Task Reporting_a_trial_twice_says_it_is_a_repeat()
    {
        await ClearTrialsAsync();
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var fileId = await UploadAsync(admin, "package.zip", "bytes for the repeat");
        var created = await RequestTrialAsync(admin, fileId);
        var trialId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var work = await (await runner.Client.PostAsJsonAsync("/api/v1/runner/trials/claim", new { }, Json))
            .Content.ReadFromJsonAsync<JsonElement>(Json);
        var lease = work.GetProperty("leaseToken").GetString();

        var body = new { leaseToken = lease, measurement = """{"groups":[]}""" };
        await runner.Client.PostAsJsonAsync($"/api/v1/runner/trials/{trialId}/report", body, Json);

        var again = await runner.Client.PostAsJsonAsync($"/api/v1/runner/trials/{trialId}/report", body, Json);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var second = await again.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(second.GetProperty("duplicate").GetBoolean());
        Assert.Equal("completed", second.GetProperty("state").GetString());
    }

    /// <summary>
    /// The ceiling, which is what stops `trial:run` becoming a way to occupy
    /// every Runner.
    ///
    /// <para>
    /// Counted on the **unfinished**, so it clears itself: the fourth request is
    /// refused while three are waiting, and allowed once one of them ends.
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_person_cannot_queue_more_trials_than_the_ceiling()
    {
        await ClearTrialsAsync();
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var file = await UploadAsync(admin, $"package-{i}.zip", $"bytes {i}");
            var response = await RequestTrialAsync(admin, file);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>(Json))
                .GetProperty("id").GetString()!);
        }

        var fourth = await UploadAsync(admin, "package-4.zip", "one too many");
        var refused = await RequestTrialAsync(admin, fourth);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("trial.tooMany", await refused.Content.ReadAsStringAsync());

        // Finish one, and there is room again.
        await using (var context = server.NewContext())
        {
            var trial = await context.Trials.FirstAsync(t => t.Id == Guid.Parse(ids[0]));
            trial.State = EvaluationJobState.Completed;
            trial.FinishedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await RequestTrialAsync(admin, fourth)).StatusCode);
    }

    /// <summary>
    /// A trial is not an attempt, so it must not appear anywhere a submission
    /// does. Asserted against the tables rather than a screen, because a screen
    /// only shows what somebody remembered to filter.
    /// </summary>
    [Fact]
    public async Task A_trial_is_no_submission_and_no_job()
    {
        await ClearTrialsAsync();
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var fileId = await UploadAsync(admin, "package.zip", "bytes that are not an attempt");

        await using var before = server.NewContext();
        var submissionsBefore = await before.Submissions.CountAsync();
        var jobsBefore = await before.EvaluationJobs.CountAsync();

        var created = await RequestTrialAsync(admin, fileId);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        await using var after = server.NewContext();
        Assert.Equal(submissionsBefore, await after.Submissions.CountAsync());
        Assert.Equal(jobsBefore, await after.EvaluationJobs.CountAsync());
    }

    /// <summary>
    /// Somebody else's trial does not exist, as far as the answer goes. **404
    /// rather than 403**: that a private measurement was taken at all is
    /// something its owner did not publish.
    /// </summary>
    [Fact]
    public async Task Another_persons_trial_is_not_found_rather_than_forbidden()
    {
        await ClearTrialsAsync();
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var fileId = await UploadAsync(admin, "package.zip", "private bytes");
        var trialId = (await (await RequestTrialAsync(admin, fileId))
            .Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetString()!;

        var other = await Sign.NewAccountAsync(server, "trial-onlooker");
        var looked = await other.GetAsync($"/api/v1/trials/{trialId}");

        Assert.Equal(HttpStatusCode.NotFound, looked.StatusCode);
    }

    /// <summary>
    /// The case the activity-scoped path had nowhere to put: a manager
    /// calibrating a problem in the **library**, which belongs to no activity.
    ///
    /// <para>
    /// Permitted by a global `trial:run`, which the catalogue already allowed —
    /// `TrialRun` is declared `Both` — and which had no path until the activity
    /// became optional.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manager_can_measure_a_problem_that_is_in_no_activity()
    {
        await ClearTrialsAsync();
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var fileId = await UploadAsync(admin, "package.zip", "bytes from the library");

        var created = await RequestLibraryTrialAsync(admin, fileId);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var trial = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("queued", trial.GetProperty("state").GetString());

        // **No activity, and absent rather than empty.** A trial that named one
        // it does not belong to would be a lie a screen could repeat.
        Assert.True(
            !trial.TryGetProperty("activityId", out var scope)
                || scope.ValueKind == JsonValueKind.Null,
            "a library trial names no activity");

        // A Runner takes it exactly as it takes any other: nothing about the
        // queue knows which scope asked.
        var runner = await Build.RunnerAsync(server);
        var claimed = await runner.Client.PostAsJsonAsync("/api/v1/runner/trials/claim", new { }, Json);
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);
        Assert.Equal(
            trial.GetProperty("id").GetString(),
            (await claimed.Content.ReadFromJsonAsync<JsonElement>(Json))
                .GetProperty("trialId").GetString());
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Two behaviours nobody had seen work.
///
/// <para>
/// Both were in the code and neither was exercised: a ranking freeze that lifts
/// while the Server keeps running, and the ceiling on how many times one person
/// may submit to one problem. The first is the interesting one — the disclosure
/// is computed from <c>now</c> on **every read**, so it should lift with no
/// restart and no cache to invalidate, and "should" is what a test is for.
/// </para>
/// <para>
/// Time is moved in the database rather than waited out. Waiting would make one
/// test take a day, and what is under examination is not the clock — it is
/// whether the answer is recomputed.
/// </para>
/// </summary>
[Collection("server")]
public class FreezeAndCeilingTests(ServerFixture server)
{
    // ── the ceiling ─────────────────────────────────────────────────────────

    /// <summary>
    /// A participant runs out of attempts, and is told so by name.
    /// </summary>
    [Fact]
    public async Task A_participant_runs_out_of_submissions_and_the_refusal_says_why()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        // Two attempts, set where the state lives. The manager surface would do
        // it too; what is under test is the enforcement, not the path to it.
        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.MaxSubmissionsPerProblem = 2;
            await context.SaveChangesAsync();
        }

        await Build.SubmitAsync(participant, slug, "print(1)\n");
        await Build.SubmitAsync(participant, slug, "print(2)\n");

        var third = await SubmitRawAsync(participant, slug, "print(3)\n");

        Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);
        Assert.Equal("submission.limit", await CodeOf(third));
    }

    /// <summary>
    /// The ceiling is per person, not per problem.
    /// </summary>
    [Fact]
    public async Task One_participant_using_up_their_attempts_does_not_spend_anybody_elses()
    {
        var (slug, _) = await Build.ActivityAsync(server);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.MaxSubmissionsPerProblem = 1;
            await context.SaveChangesAsync();
        }

        var first = await Build.ParticipantAsync(server, slug);
        await Build.SubmitAsync(first, slug, "print(1)\n");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SubmitRawAsync(first, slug, "print(2)\n")).StatusCode);

        var second = await Build.ParticipantAsync(server, slug);
        var theirs = await SubmitRawAsync(second, slug, "print(1)\n");

        Assert.True(
            theirs.IsSuccessStatusCode,
            $"somebody else's attempt was spent: {theirs.StatusCode}");
    }

    // ── the freeze ──────────────────────────────────────────────────────────

    /// <summary>
    /// A freeze lifts while the Server keeps running.
    /// <para>
    /// The whole mechanism is that disclosure is decided from <c>now</c> at read
    /// time — no job flips a flag, nothing is cached, and nothing has to be
    /// restarted. That is easy to write and easy to lose: one memoised board and
    /// a contest's results stay hidden past the moment they were promised.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_freeze_lifts_with_nothing_restarted_and_nothing_invalidated()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!,
            job.GetProperty("leaseToken").GetString()!);

        // Frozen an hour ago, promised tomorrow.
        await SetWindowAsync(roundId, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1));

        var during = await BoardAsync(participant, slug);
        Assert.True(Frozen(during), $"the round should be frozen: {during}");
        Assert.False(
            Scored(during),
            $"an outcome after the freeze arrives withheld: {during}");

        // The promised moment arrives. Nothing else changes — no restart, no
        // request to any other endpoint, no worker in between.
        await SetWindowAsync(roundId, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddSeconds(-1));

        var after = await BoardAsync(participant, slug);
        Assert.False(Frozen(after), $"the freeze did not lift: {after}");
        Assert.True(Scored(after), $"the outcome was not disclosed: {after}");
    }

    /// <summary>
    /// A withheld entry keeps its identity and loses only its outcome.
    /// <para>
    /// Omitting it would leave a board unable to tell "did not try" from
    /// "tried, and you may not know yet" — and the second is exactly what the
    /// `?` cell of a frozen ICPC board means.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_withheld_entry_still_says_that_somebody_tried()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);
        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!,
            job.GetProperty("leaseToken").GetString()!);

        await SetWindowAsync(roundId, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddDays(1));

        var board = await BoardAsync(participant, slug);
        var results = board.GetProperty("results").EnumerateArray().ToList();

        // The entry is there. That is the whole point: a board can tell "did
        // not try" from "tried, and you may not know yet".
        Assert.NotEmpty(results);
        var withheld = results[0];

        Assert.True(withheld.TryGetProperty("frozen", out var frozen) && frozen.GetBoolean());
        Assert.False(withheld.TryGetProperty("points", out _), "the outcome must be absent");
        Assert.False(withheld.TryGetProperty("state", out _), "and so must the state");

        // What it keeps: who, which problem, and when.
        Assert.NotNull(withheld.GetProperty("contestantId").GetString());
        Assert.NotNull(withheld.GetProperty("problemSlug").GetString());
        Assert.NotNull(withheld.GetProperty("submittedAt").GetString());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task SetWindowAsync(string roundId, DateTime freeze, DateTime reveal)
    {
        await using var context = server.NewContext();
        var round = await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
        round.RankingFreezeAt = freeze;
        round.RankingRevealAt = reveal;
        await context.SaveChangesAsync();
    }

    private static async Task<JsonElement> BoardAsync(HttpClient client, string slug)
    {
        var response = await client.GetAsync($"/api/v1/activities/{slug}/results");
        await Sign.Succeeded(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static bool Frozen(JsonElement board) =>
        board.GetProperty("series").EnumerateArray()
            .Any(round => round.GetProperty("frozen").GetBoolean());

    /// <summary>
    /// Whether any entry carries an outcome rather than only the fact of it.
    ///
    /// **Absent, not null.** The Server omits nulls entirely, so a withheld
    /// outcome has no `points` key at all — and a test that looked for a null
    /// would pass over a board that disclosed everything.
    /// </summary>
    private static bool Scored(JsonElement board) =>
        board.GetProperty("results").EnumerateArray()
            .Any(entry =>
                entry.TryGetProperty("points", out var points)
                && points.ValueKind is not JsonValueKind.Null);

    private static async Task<HttpResponseMessage> SubmitRawAsync(
        HttpClient client, string slug, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();

        using var content = new MultipartFormDataContent
        {
            { new StringContent("""{"type":"standard-io@1","language":"python3"}"""), "props" },
            { new StringContent("main.py"), "fileName" },
            { new StringContent(source), "code" },
            { new StringContent(checksum), "sha256" },
        };

        return await client.PostAsync(
            $"/api/v1/activities/{slug}/problems/A/submissions", content);
    }

    private static async Task<string> CodeOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
    }
}

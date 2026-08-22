using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// One number, on every surface that reports it.
///
/// <para>
/// The rule is the owner's, stated 2026-08-21: <b>a problem defines its own
/// scoring and that is fixed to the problem; a manager may set
/// <c>SeriesProblem.MaxPoints</c> to rescale it in one round.</b> Five places
/// contradicted it, and each looked correct on its own — which is why this suite
/// traces a single submission rather than testing five expressions.
/// </para>
///
/// <para>
/// <b>Every test here reports out of 70, deliberately.</b> The suite's runner
/// stub defaulted to <c>maxScore: 100</c> and nothing ever passed anything else,
/// so every existing test ran on the one scale where a raw score, a percentage
/// and a rescaled score are the same number. None of these defects is visible
/// there. 70 out of 100, on an assignment worth 50, is <b>35</b> — and 70, 100
/// and 35 are three different numbers.
/// </para>
/// </summary>
[Collection("server")]
public class ScoringTests(ServerFixture server)
{
    /// <summary>The Runner's own scale in these tests, and the rescaled answer.</summary>
    private const double Awarded = 70;
    private const double OutOf = 100;
    private const double Rescaled = 35;

    /// <summary>
    /// A submission judged 70 out of 100 on a problem worth 50 reads 35 / 50
    /// wherever anybody looks.
    /// </summary>
    [Fact]
    public async Task One_submission_reads_the_same_on_every_surface()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submission = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var submissionId = submission.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submissionId);
        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!,
            job.GetProperty("leaseToken").GetString()!,
            score: Awarded, verdict: "Accepted", maxScore: OutOf);

        // ── the participant's own submission ────────────────────────────────
        var detail = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/submissions/{submissionId}");

        Assert.Equal(Rescaled, detail.GetProperty("score").GetDouble());
        Assert.Equal(50, detail.GetProperty("maxScore").GetDouble());

        // **The attempt inside it, which was the Runner's raw number.** 70 in
        // the list and 35 in the header, on one screen, and neither expression
        // looked wrong on its own.
        var attempt = detail.GetProperty("attempts").EnumerateArray().First();
        Assert.Equal(Rescaled, attempt.GetProperty("score").GetDouble());

        // ── the problem page ────────────────────────────────────────────────
        var problem = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/problems/A");
        Assert.Equal(Rescaled, problem.GetProperty("bestScore").GetDouble());
        Assert.Equal(50, problem.GetProperty("maxScore").GetDouble());

        // ── the board ───────────────────────────────────────────────────────
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var results = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/results");

        var column = results.GetProperty("series").EnumerateArray()
            .SelectMany(r => r.GetProperty("problems").EnumerateArray())
            .First(p => p.GetProperty("slug").GetString() == "A");
        Assert.Equal(50, column.GetProperty("maxPoints").GetDouble());

        var entry = results.GetProperty("results").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == submissionId);
        Assert.Equal(Rescaled, entry.GetProperty("points").GetDouble());

        // ── the manager's list ──────────────────────────────────────────────
        var managed = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/submissions?page=1&pageSize=50");
        var row = managed.GetProperty("items").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == submissionId);
        Assert.Equal(Rescaled, row.GetProperty("score").GetDouble());
        Assert.Equal(50, row.GetProperty("maxScore").GetDouble());

        // ── what this person may take away about themselves ─────────────────
        //
        // It was the Runner's raw number here while every screen rescaled, so an
        // export produced precisely so somebody can check what is held about
        // them disagreed with the pages they had been reading.
        var export = await participant.GetFromJsonAsync<JsonElement>("/api/v1/account/export");
        var exported = export.GetProperty("submissions").EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == submissionId);
        Assert.Equal(Rescaled, exported.GetProperty("score").GetDouble());
        Assert.Equal(50, exported.GetProperty("maxScore").GetDouble());
    }

    /// <summary>
    /// An assignment that states no point value keeps <b>the problem's own
    /// scale</b>, which is what the column has always promised and what the code
    /// replaced with a percentage.
    ///
    /// <para>
    /// A package marking out of 70 reported a full solve as <c>100 / 100</c>:
    /// the problem's own scoring, which the rule says is fixed to the problem,
    /// silently rewritten by a convention nobody chose.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_assignment_with_no_point_value_keeps_the_problems_own_scale()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);

        // Scoped to this round: the suite shares a database, and an unfiltered
        // first row is whichever test ran before this one.
        var round = Guid.Parse(roundId);
        await using (var context = server.NewContext())
        {
            var assignment = await context.SeriesProblems.FirstAsync(sp => sp.SeriesId == round);
            assignment.MaxPoints = null;
            await context.SaveChangesAsync();
        }

        var participant = await Build.ParticipantAsync(server, slug);
        var submission = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var submissionId = submission.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submissionId);
        // **Out of 140, and not out of 100.** This test passed under the old
        // code the first time it was run, because a package marking out of a
        // hundred is the one scale on which a percentage and the package's own
        // scale are the same number — so it was asserting nothing. Found by
        // sabotage, which is what sabotage is for.
        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!,
            job.GetProperty("leaseToken").GetString()!,
            score: 70, verdict: "Accepted", maxScore: 140);

        var detail = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/submissions/{submissionId}");

        // The package's own numbers, untouched. The old code reported
        // `round(0.5 × 100)` = 50 out of 100.
        Assert.Equal(70, detail.GetProperty("score").GetDouble());
        Assert.Equal(140, detail.GetProperty("maxScore").GetDouble());
    }

    /// <summary>
    /// The same, where the package's scale is not a hundred — which is the case
    /// the old code could not tell apart from anything else.
    /// </summary>
    [Fact]
    public async Task A_problem_marked_out_of_one_is_not_reported_as_a_percentage()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);

        var round = Guid.Parse(roundId);
        await using (var context = server.NewContext())
        {
            var assignment = await context.SeriesProblems.FirstAsync(sp => sp.SeriesId == round);
            assignment.MaxPoints = null;
            await context.SaveChangesAsync();
        }

        var participant = await Build.ParticipantAsync(server, slug);
        var submission = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var submissionId = submission.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submissionId);
        // An external judge marking out of one, which is the shape that first
        // exposed the assumed scale on 2026-08-16.
        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!,
            job.GetProperty("leaseToken").GetString()!,
            score: 1, verdict: "Accepted", maxScore: 1);

        var detail = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/submissions/{submissionId}");

        Assert.Equal(1, detail.GetProperty("score").GetDouble());
        Assert.Equal(1, detail.GetProperty("maxScore").GetDouble());
    }

    /// <summary>
    /// A problem worth zero is refused, on both write paths.
    ///
    /// <para>
    /// Zero is not a problem worth nothing: it is a problem whose every number
    /// is <c>0 / 0</c>, which a board reads as full marks because zero out of
    /// zero is the whole of it. A problem nobody should score is a problem
    /// nobody should attach.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_problem_worth_nothing_is_refused_when_attached_and_when_edited()
    {
        var (_, roundId) = await Build.ActivityAsync(server);
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var problem = await Build.PostAsync(admin, "/api/v1/problems", new
        {
            slug = "z-" + Guid.NewGuid().ToString("N")[..8],
            name = "Worth nothing",
            type = "standard-io@1",
        });

        var attached = await admin.PostAsJsonAsync($"/api/v1/series/{roundId}/problems", new
        {
            problemId = problem.GetProperty("id").GetString(),
            slug = "Z",
            maxPoints = 0,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, attached.StatusCode);
        var refused = await attached.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("assignment.maxPoints.invalid", refused.GetProperty("code").GetString());

        // **And on the edit path.** A rule enforced where a thing is created and
        // not where it is changed is a rule one screen removes.
        var round = Guid.Parse(roundId);
        Guid existing;
        Guid existingProblem;
        await using (var context = server.NewContext())
        {
            var assignment = await context.SeriesProblems.FirstAsync(sp => sp.SeriesId == round);
            existing = assignment.Id;
            existingProblem = assignment.ProblemId;
        }

        var edited = await admin.PutAsJsonAsync($"/api/v1/series-problems/{existing}", new
        {
            problemId = existingProblem.ToString(),
            slug = "A",
            maxPoints = -5,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, edited.StatusCode);
        var alsoRefused = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("assignment.maxPoints.invalid", alsoRefused.GetProperty("code").GetString());
    }
}

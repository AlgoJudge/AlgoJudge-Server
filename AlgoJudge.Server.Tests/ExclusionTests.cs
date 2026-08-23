using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A manager's ruling that a submission counts towards no standing.
/// <para>
/// The line these are drawn along: <b>an exclusion retracts nothing</b>. The
/// verdict stays, the place in every list stays, and <b>the ceiling it spent
/// stays spent</b> — what it loses is every reader that computes a standing.
/// </para>
/// <para>
/// The allowance test drives a real submission through the endpoint and reads
/// the refusal: setting the column by hand and counting rows would pass whatever
/// the Server does.
/// </para>
/// </summary>
[Collection("server")]
public class ExclusionTests(ServerFixture server)
{
    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    private static Task<HttpResponseMessage> ExcludeAsync(
        HttpClient manager, string submissionId, bool excluded = true, string? reason = null) =>
        manager.PostAsJsonAsync(
            $"/api/v1/submissions/{submissionId}/excluded", new { excluded, reason });

    /// <summary>A submission that has been judged, so it has a standing to lose.</summary>
    private async Task<string> JudgedAsync(HttpClient participant, string slug, double score = 100)
    {
        var submitted = await Build.SubmitAsync(participant, slug, $"print({score})\n");
        var id = submitted.GetProperty("id").GetString()!;

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(id);
        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!,
            job.GetProperty("leaseToken").GetString()!,
            score);

        return id;
    }

    private async Task<JsonElement> BoardAsync(HttpClient client, string slug) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/results");

    // ── it leaves every standing ────────────────────────────────────────────

    /// <summary>
    /// The board loses the row entirely — not the way a freeze loses one, where
    /// the row stays and only the outcome goes.
    /// </summary>
    [Fact]
    public async Task An_excluded_submission_holds_no_row_on_the_board()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var id = await JudgedAsync(participant, slug);

        var before = await BoardAsync(admin, slug);
        Assert.Contains(
            before.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("id").GetString() == id);

        await Sign.Succeeded(await ExcludeAsync(admin, id, reason: "Wysłane spoza sali"));

        var after = await BoardAsync(admin, slug);
        Assert.DoesNotContain(
            after.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("id").GetString() == id);
    }

    /// <summary>
    /// It stops being the participant's best score. Three screens ask
    /// <see cref="Scoring.BestOf"/> this and share the expression, so one of
    /// them catches the filter going missing.
    /// </summary>
    [Fact]
    public async Task An_excluded_submission_is_not_the_participants_best_score()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var id = await JudgedAsync(participant, slug);

        var before = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/problems/A");
        Assert.Equal("solved", before.GetProperty("status").GetString());
        Assert.Equal(50, before.GetProperty("bestScore").GetDouble());

        await Sign.Succeeded(await ExcludeAsync(admin, id));

        var after = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/problems/A");

        // No standing left — but they did attempt it, and "untouched" would
        // be a different and false claim.
        Assert.Equal("attempted", after.GetProperty("status").GetString());
        Assert.False(
            after.TryGetProperty("bestScore", out var best) && best.ValueKind != JsonValueKind.Null,
            $"an excluded submission is still the best score: {after}");
    }

    // ── and stays spent ─────────────────────────────────────────────────────

    /// <summary>
    /// <b>The allowance is not refunded</b>, as <c>ActivityGroup.IsSystem</c>
    /// is not one level up. Giving an attempt back means raising the limit,
    /// visibly; an exclusion that did it quietly would be a second way to move
    /// a ceiling, and one nobody can see.
    /// </summary>
    [Fact]
    public async Task An_excluded_submission_still_spends_the_allowance()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.MaxSubmissionsPerProblem = 1;
            await context.SaveChangesAsync();
        }

        var participant = await Build.ParticipantAsync(server, slug);
        var first = await Build.SubmitAsync(participant, slug, "print(1)\n");

        await Sign.Succeeded(
            await ExcludeAsync(admin, first.GetProperty("id").GetString()!, reason: "Nie liczymy"));

        var second = await Build.TrySubmitAsync(participant, slug, "print(2)\n");

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Contains("submission.limit", await second.Content.ReadAsStringAsync());

        // And the figure the participant reads agrees with the refusal, which
        // is why `Services/Contestant` is one place rather than two.
        var problem = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/problems/A");
        Assert.Equal(0, problem.GetProperty("submissionsLeft").GetInt32());
    }

    // ── what the two sides are told ─────────────────────────────────────────

    /// <summary>
    /// The participant is told on their own submission, and told nothing more.
    /// Without the marker their screen and the ranking describe one submission
    /// differently, with no way to find out why.
    /// </summary>
    [Fact]
    public async Task The_participant_sees_the_marker_and_not_the_reason()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var id = await JudgedAsync(participant, slug);
        await Sign.Succeeded(await ExcludeAsync(admin, id, reason: "Kod identyczny z cudzym"));

        var theirs = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/submissions/{id}");

        Assert.True(theirs.GetProperty("excluded").GetBoolean());
        Assert.False(
            theirs.TryGetProperty("exclusionReason", out _),
            "the manager's reason must not reach the participant's screen");

        // It keeps everything an exclusion does not rule on.
        Assert.Equal("Accepted", theirs.GetProperty("verdict").GetString());

        var list = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/submissions?page=1&pageSize=20");
        var row = Assert.Single(
            list.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("id").GetString() == id);
        Assert.True(row.GetProperty("excluded").GetBoolean());
    }

    /// <summary>The manager's detail carries the ruling, who made it and why.</summary>
    [Fact]
    public async Task The_manager_sees_who_ruled_and_why()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var id = await JudgedAsync(participant, slug);
        await Sign.Succeeded(await ExcludeAsync(admin, id, reason: "  Poza salą  "));

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/submissions/{id}");

        Assert.True(detail.GetProperty("excluded").GetBoolean());
        Assert.Equal("Poza salą", detail.GetProperty("exclusionReason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("excludedBy").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("excludedAt").GetString()));

        // The list carries the flag too, so a manager scanning two hundred
        // rows need not open each.
        var page = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/submissions?page=1&pageSize=20&activityId={await ActivityIdAsync(slug)}");
        var row = Assert.Single(
            page.GetProperty("items").EnumerateArray(),
            r => r.GetProperty("id").GetString() == id);
        Assert.True(row.GetProperty("excluded").GetBoolean());
    }

    /// <summary>
    /// Lifting the ruling clears the reason with it: a sentence explaining a
    /// state that no longer holds is worse than none.
    /// </summary>
    [Fact]
    public async Task Lifting_the_ruling_clears_the_reason_and_restores_the_row()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var id = await JudgedAsync(participant, slug);
        await Sign.Succeeded(await ExcludeAsync(admin, id, reason: "Pomyłka"));
        await Sign.Succeeded(await ExcludeAsync(admin, id, excluded: false));

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/submissions/{id}");
        Assert.False(detail.GetProperty("excluded").GetBoolean());

        await using var context = server.NewContext();
        var stored = await context.Submissions.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(id));
        Assert.Null(stored.ExcludedAt);
        Assert.Null(stored.ExcludedByUserId);
        Assert.Null(stored.ExclusionReason);

        var board = await BoardAsync(admin, slug);
        Assert.Contains(
            board.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("id").GetString() == id);
    }

    // ── who may rule ────────────────────────────────────────────────────────

    /// <summary>
    /// A participant may not rule on their own submission, nor on anybody's.
    /// </summary>
    [Fact]
    public async Task A_caller_without_the_permission_is_refused()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var id = submitted.GetProperty("id").GetString()!;

        var refused = await ExcludeAsync(participant, id, reason: "Proszę nie liczyć");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await using var context = server.NewContext();
        var stored = await context.Submissions.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(id));
        Assert.Null(stored.ExcludedAt);
    }

    // ── erasure ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Erasure takes the reason and leaves the ruling: whether a submission
    /// counted is contest history, and clearing it would move a board after the
    /// fact. The sentence about a named person goes the way the address goes.
    /// </summary>
    [Fact]
    public async Task Erasure_clears_the_reason_and_keeps_the_ruling()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var submitted = await Build.SubmitAsync(participant, slug, "print(1)\n");
        var id = submitted.GetProperty("id").GetString()!;
        await Sign.Succeeded(await ExcludeAsync(admin, id, reason: "Rozwiązanie kogoś innego"));

        var who = (await participant.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("userId").GetString()!;

        using (var scope = server.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await context.Users.FirstAsync(u => u.Id == who);
            await scope.ServiceProvider.GetRequiredService<IAccountDeletionService>()
                .AnonymiseAsync(user, default);
            await context.SaveChangesAsync();
        }

        await using var after = server.NewContext();
        var stored = await after.Submissions.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(id));

        Assert.Null(stored.ExclusionReason);
        Assert.NotNull(stored.ExcludedAt);
    }

    private async Task<string> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id.ToString();
    }
}

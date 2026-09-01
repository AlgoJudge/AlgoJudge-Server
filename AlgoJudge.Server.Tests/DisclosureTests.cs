using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What the Server withholds, and when.
/// <para>
/// The board is assembled in the Client, so <b>anything sent has been
/// disclosed</b>. These are the three filters that decide what leaves — the
/// window, `scoreVisibility` and the freeze — tested by what does not arrive.
/// </para>
/// </summary>
[Collection("server-1")]
public class DisclosureTests(ServerFixture server)
{
    /// <summary>
    /// <b>Holding <c>submission:create</c> is not being in the activity.</b> The
    /// effective set unions every system-scope grant into every activity and both
    /// shipped templates build on the participant's keys, so anybody staff could
    /// submit anywhere — graded, listed among the manager's rows, and absent from
    /// the ranking, which builds its contestants from activity grants.
    /// </summary>
    [Fact]
    public async Task Only_somebody_enrolled_may_submit()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var stranger = await Sign.NewAccountAsync(server, "stranger-submitting");

        string strangerId;
        await using (var context = server.NewContext())
        {
            strangerId = (await context.Users.FirstAsync(u => u.UserName == "stranger-submitting")).Id;
        }

        // A system grant carrying the participant's own keys: enough to satisfy
        // the permission check in every activity, which is the whole hole.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = strangerId,
            permissions = new[] { "activity:read", "submission:create" },
        }));

        var refused = await Sign.TrySubmitAsync(stranger, "python", "print(1)\n");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("enrolment.required",
            (await refused.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString());

        // Joining is the whole of the difference.
        await Sign.Succeeded(await stranger.PostAsJsonAsync(
            "/api/v1/activities/DEV-2026/enrolment", new { }));
        await Sign.SubmitAsync(stranger, "python", "print(1)\n");
    }

    /// <summary>
    /// <b>The administrator's bypass is a bypass of permissions</b>, and being in
    /// an activity is not one. Pinned rather than remembered: it is the decision
    /// in this gate somebody will want to argue with, and the cost of it is one
    /// request — an administrator holds <c>grant:update</c> everywhere.
    /// </summary>
    [Fact]
    public async Task Not_even_an_administrator_submits_to_an_activity_they_are_not_in()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        // The seeder gives the administrator a manager grant on this activity,
        // which is a membership like any other — and that is why `IsSystem` is
        // not part of the predicate. Parked rather than deleted, so the rest of
        // the suite gets it back.
        Guid parked;
        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == "DEV-2026");
            var adminId = (await context.Users.FirstAsync(u => u.UserName == Seeder.DevAdminLogin)).Id;
            var grant = await context.Grants.FirstAsync(
                g => g.ActivityId == activity.Id && g.UserId == adminId);
            grant.State = GrantState.Invited;
            parked = grant.Id;
            await context.SaveChangesAsync();
        }

        try
        {
            var refused = await Sign.TrySubmitAsync(admin, "python", "print(1)\n");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("enrolment.required",
                (await refused.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("code").GetString());
        }
        finally
        {
            await using var context = server.NewContext();
            var grant = await context.Grants.FirstAsync(g => g.Id == parked);
            grant.State = GrantState.Active;
            await context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task The_results_feed_carries_rounds_contestants_and_one_entry_per_submission()
    {
        var participant = await Sign.InAsync(server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);
        await Sign.SubmitAsync(participant, "python", "print(1)\n");

        var results = await participant.GetFromJsonAsync<JsonElement>("/api/v1/activities/DEV-2026/results");

        var round = Assert.Single(results.GetProperty("series").EnumerateArray().ToList());
        Assert.False(round.GetProperty("frozen").GetBoolean());

        var column = Assert.Single(round.GetProperty("problems").EnumerateArray().ToList());
        Assert.Equal("A", column.GetProperty("slug").GetString());
        // The column says what the problem is worth here, not on the Runner's scale.
        Assert.Equal(50, column.GetProperty("maxPoints").GetDouble());

        // Staff are not competitors: the seeded manager grant is systemic, so
        // only the participant has a row.
        var contestants = results.GetProperty("contestants").EnumerateArray().ToList();
        Assert.All(contestants, c => Assert.NotEqual("admin", c.GetProperty("name").GetString()));

        Assert.NotEmpty(results.GetProperty("results").EnumerateArray().ToList());
        Assert.Equal(JsonValueKind.String, results.GetProperty("me").ValueKind);
    }

    /// <summary>
    /// A round whose window has not opened contributes nothing — not its
    /// columns, and not the results in it. Withholding at render time would not
    /// be withholding.
    /// </summary>
    [Fact]
    public async Task A_shut_window_contributes_nothing()
    {
        await WithSeriesAsync(series => series.RankingVisibleFrom = DateTime.UtcNow.AddDays(1));
        try
        {
            var participant = await Sign.InAsync(
                server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);

            var results = await participant.GetFromJsonAsync<JsonElement>("/api/v1/activities/DEV-2026/results");

            Assert.Empty(results.GetProperty("series").EnumerateArray().ToList());
            Assert.Empty(results.GetProperty("results").EnumerateArray().ToList());
        }
        finally
        {
            await WithSeriesAsync(series => series.RankingVisibleFrom = null);
        }
    }

    /// <summary>
    /// A frozen entry keeps its identity, its problem and its time and loses
    /// everything about how it went. Omitting it would leave a board unable to
    /// tell "did not try" from "tried, and you may not know yet".
    /// </summary>
    [Fact]
    public async Task A_frozen_result_travels_withheld_rather_than_omitted()
    {
        var participant = await Sign.InAsync(server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);
        var submitted = await Sign.SubmitAsync(participant, "python", "print(2)\n");
        var submissionId = submitted.GetProperty("id").GetString()!;

        // Freeze from before that submission, with no reveal.
        await WithSeriesAsync(series =>
        {
            series.RankingFreezeAt = DateTime.UtcNow.AddMinutes(-5);
            series.RankingRevealAt = DateTime.UtcNow.AddDays(1);
        });

        try
        {
            var results = await participant.GetFromJsonAsync<JsonElement>("/api/v1/activities/DEV-2026/results");

            var round = Assert.Single(results.GetProperty("series").EnumerateArray().ToList());
            Assert.True(round.GetProperty("frozen").GetBoolean());

            var entry = results.GetProperty("results").EnumerateArray()
                .Single(r => r.GetProperty("id").GetString() == submissionId);

            // Present, and identified.
            Assert.Equal("A", entry.GetProperty("problemSlug").GetString());
            Assert.True(entry.TryGetProperty("submittedAt", out _));
            // And withheld.
            Assert.True(entry.GetProperty("frozen").GetBoolean());
            Assert.False(entry.TryGetProperty("points", out var points) && points.ValueKind != JsonValueKind.Null);
            Assert.False(entry.TryGetProperty("state", out var state) && state.ValueKind != JsonValueKind.Null);
        }
        finally
        {
            await WithSeriesAsync(series =>
            {
                series.RankingFreezeAt = null;
                series.RankingRevealAt = null;
            });
        }
    }

    /// <summary>
    /// `ranking:read:unfrozen` is applied to the feed, not by a screen. Whoever
    /// holds it is never inside a freeze.
    /// </summary>
    [Fact]
    public async Task Whoever_may_read_past_a_freeze_is_sent_the_outcome()
    {
        var participant = await Sign.InAsync(server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);
        await Sign.SubmitAsync(participant, "python", "print(3)\n");

        await WithSeriesAsync(series =>
        {
            series.RankingFreezeAt = DateTime.UtcNow.AddMinutes(-5);
            series.RankingRevealAt = DateTime.UtcNow.AddDays(1);
        });

        try
        {
            var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
            var results = await admin.GetFromJsonAsync<JsonElement>("/api/v1/activities/DEV-2026/results");

            var round = Assert.Single(results.GetProperty("series").EnumerateArray().ToList());
            Assert.False(round.GetProperty("frozen").GetBoolean());

            Assert.All(
                results.GetProperty("results").EnumerateArray(),
                entry => Assert.False(
                    entry.TryGetProperty("frozen", out var frozen) && frozen.ValueKind == JsonValueKind.True));
        }
        finally
        {
            await WithSeriesAsync(series =>
            {
                series.RankingFreezeAt = null;
                series.RankingRevealAt = null;
            });
        }
    }

    [Fact]
    public async Task Enrolment_is_refused_where_only_an_organiser_may_enrol()
    {
        var participant = await Sign.InAsync(server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == "DEV-2026");
            activity.JoinPolicy = JoinPolicy.Closed;
            await context.SaveChangesAsync();
        }

        try
        {
            // Already enrolled by the seed, so a fresh account is needed to see
            // the refusal rather than the "already in" answer.
            var stranger = await Sign.NewAccountAsync(server, "stranger-closed");
            var response = await stranger.PostAsJsonAsync(
                "/api/v1/activities/DEV-2026/enrolment", new { });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("enrolment.closed", problem.GetProperty("code").GetString());
        }
        finally
        {
            await using var context = server.NewContext();
            var activity = await context.Activities.FirstAsync(a => a.Slug == "DEV-2026");
            activity.JoinPolicy = JoinPolicy.Open;
            await context.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Enrolling_twice_is_not_an_error()
    {
        var stranger = await Sign.NewAccountAsync(server, "joins-twice");

        var first = await stranger.PostAsJsonAsync("/api/v1/activities/DEV-2026/enrolment", new { });
        var second = await stranger.PostAsJsonAsync("/api/v1/activities/DEV-2026/enrolment", new { });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // A link gets opened twice. The second time answers with the activity as
        // they already see it rather than a conflict.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var activity = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("enrolled", activity.GetProperty("membership").GetString());
    }

    [Fact]
    public async Task A_question_reaches_its_author_before_it_is_published()
    {
        var participant = await Sign.InAsync(server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);

        var asked = await participant.PostAsJsonAsync("/api/v1/activities/DEV-2026/questions", new
        {
            topic = "Limit czasu",
            body = "Czy limit dotyczy jednego testu?",
        });
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        var question = await asked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(question.GetProperty("isPublished").GetBoolean());

        // Its author sees it while it is unpublished.
        var mine = await participant.GetFromJsonAsync<JsonElement>("/api/v1/activities/DEV-2026/questions");
        Assert.Contains(
            mine.GetProperty("items").EnumerateArray(),
            q => q.GetProperty("id").GetString() == question.GetProperty("id").GetString());

        // Somebody else does not.
        var stranger = await Sign.NewAccountAsync(server, "not-the-author");
        await stranger.PostAsJsonAsync("/api/v1/activities/DEV-2026/enrolment", new { });
        var theirs = await stranger.GetFromJsonAsync<JsonElement>("/api/v1/activities/DEV-2026/questions");
        Assert.DoesNotContain(
            theirs.GetProperty("items").EnumerateArray(),
            q => q.GetProperty("id").GetString() == question.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Deleting_an_account_anonymises_it_and_what_it_wrote()
    {
        var leaver = await Sign.NewAccountAsync(server, "leaves");
        await leaver.PostAsJsonAsync("/api/v1/activities/DEV-2026/enrolment", new { });
        await leaver.PostAsJsonAsync("/api/v1/activities/DEV-2026/questions", new
        {
            topic = "Nazwisko w temacie",
            body = "Pytanie podpisane imieniem",
        });

        var session = await leaver.GetFromJsonAsync<JsonElement>("/api/v1/account");
        var userId = session.GetProperty("userId").GetString()!;

        var deleted = await leaver.PostAsJsonAsync(
            "/api/v1/account/delete", new { password = Sign.Password });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await using var context = server.NewContext();
        var user = await context.Users.FirstAsync(u => u.Id == userId);

        Assert.True(user.Anonymized);
        Assert.Null(user.Email);
        Assert.Null(user.FirstName);
        Assert.StartsWith("deleted-", user.UserName);

        // The row is not enough: identity is in the text as well.
        var questions = await context.Questions.Where(q => q.AuthorUserId == userId).ToListAsync();
        Assert.All(questions, q => Assert.Equal("[deleted]", q.Body));
    }

    private async Task WithSeriesAsync(Action<Series> change)
    {
        await using var context = server.NewContext();
        var series = await context.Series.FirstAsync(s => s.Slug == "round-1");
        change(series);
        await context.SaveChangesAsync();
    }
}

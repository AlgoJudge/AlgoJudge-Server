using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Several people competing as one.
/// <para>
/// The rule underneath all of it: <b>a submission stamps its group when it is
/// made and never afterwards</b>. A manager may move somebody at any time, and
/// that changes what happens next and nothing that already happened — so a
/// ranking read an hour ago still reconciles with the ranking now.
/// </para>
/// </summary>
[Collection("server")]
public class GroupTests(ServerFixture server)
{
    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    private static async Task<string> GroupAsync(
        HttpClient admin, string slug, string name, bool isSystem = false)
    {
        var made = await admin.PostAsJsonAsync(
            $"/api/v1/activities/{slug}/groups", new { name, description = "Klasa 3B", isSystem });
        await Sign.Succeeded(made);
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> WhoAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("userId").GetString()!;

    private static Task<HttpResponseMessage> AssignAsync(
        HttpClient admin, string slug, string userId, string? groupId) =>
        admin.PutAsJsonAsync(
            $"/api/v1/activities/{slug}/participants/{userId}/group", new { groupId });

    // ── the group exists, and a manager runs it ─────────────────────────────

    /// <summary>
    /// Two rows in one ranking may not carry one name.
    /// </summary>
    [Fact]
    public async Task Two_groups_in_one_activity_may_not_share_a_name()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        await GroupAsync(admin, slug, "Zespół Alfa");

        var again = await admin.PostAsJsonAsync(
            $"/api/v1/activities/{slug}/groups", new { name = "Zespół Alfa" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("group.name.taken", await again.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A person holds one group, and moving them replaces it rather than adding
    /// a second — which the grant table's own unique index has always required.
    /// </summary>
    [Fact]
    public async Task A_participant_holds_one_group_and_moving_them_replaces_it()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = await WhoAsync(participant);

        var alfa = await GroupAsync(admin, slug, "Alfa " + Guid.NewGuid().ToString("N")[..6]);
        var beta = await GroupAsync(admin, slug, "Beta " + Guid.NewGuid().ToString("N")[..6]);

        await Sign.Succeeded(await AssignAsync(admin, slug, who, alfa));
        await Sign.Succeeded(await AssignAsync(admin, slug, who, beta));

        await using var context = server.NewContext();
        var grants = await context.Grants
            .Where(g => g.UserId == who && g.Activity!.Slug == slug)
            .ToListAsync();

        var one = Assert.Single(grants);
        Assert.Equal(Guid.Parse(beta), one.GroupId);

        // And out again, which is how somebody goes back to competing as
        // themselves.
        await Sign.Succeeded(await AssignAsync(admin, slug, who, null));
        await using var after = server.NewContext();
        Assert.Null((await after.Grants.FirstAsync(g => g.Id == one.Id)).GroupId);
    }

    /// <summary>
    /// A group that has sent something cannot be deleted.
    /// <para>
    /// The stamp on a submission is the record of what competed; removing the
    /// row would make every one of them say it was sent by nobody. The way to
    /// retire one is to mark it system.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_group_that_has_submitted_is_refused_deletion_and_may_be_retired_instead()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = await WhoAsync(participant);

        var group = await GroupAsync(admin, slug, "Wysyłający " + Guid.NewGuid().ToString("N")[..6]);
        await Sign.Succeeded(await AssignAsync(admin, slug, who, group));

        await Build.SubmitAsync(participant, slug, "print(1)\n");

        var refused = await admin.DeleteAsync($"/api/v1/activities/{slug}/groups/{group}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("group.hasSubmissions", await refused.Content.ReadAsStringAsync());

        // Retiring it is the answer, and it works on a group that is running.
        var retired = await admin.PutAsJsonAsync(
            $"/api/v1/activities/{slug}/groups/{group}",
            new { name = "Wysyłający", isSystem = true });
        await Sign.Succeeded(retired);
        Assert.True((await retired.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("isSystem").GetBoolean());
    }

    /// <summary>One nobody has used goes, so a mistake is not permanent.</summary>
    [Fact]
    public async Task A_group_nobody_has_used_can_be_deleted()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var group = await GroupAsync(admin, slug, "Pomyłka " + Guid.NewGuid().ToString("N")[..6]);

        var gone = await admin.DeleteAsync($"/api/v1/activities/{slug}/groups/{group}");
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
    }

    // ── the group submits, and spends one allowance ─────────────────────────

    /// <summary>
    /// A submission made in a group carries the group, and one made outside a
    /// group carries none.
    /// </summary>
    [Fact]
    public async Task A_submission_carries_the_group_it_was_sent_as()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = await WhoAsync(participant);

        var alone = Guid.Parse(
            (await Build.SubmitAsync(participant, slug, "print(1)\n"))
                .GetProperty("id").GetString()!);

        var group = await GroupAsync(admin, slug, "Razem " + Guid.NewGuid().ToString("N")[..6]);
        await Sign.Succeeded(await AssignAsync(admin, slug, who, group));

        var together = Guid.Parse(
            (await Build.SubmitAsync(participant, slug, "print(2)\n"))
                .GetProperty("id").GetString()!);

        await using var context = server.NewContext();
        Assert.Null((await context.Submissions.FirstAsync(s => s.Id == alone)).GroupId);
        Assert.Equal(
            Guid.Parse(group),
            (await context.Submissions.FirstAsync(s => s.Id == together)).GroupId);
    }

    /// <summary>
    /// <b>The group spends one allowance, not one per member.</b>
    /// <para>
    /// A ceiling of two and two people in one group: the third attempt is
    /// refused whoever sends it, and the figure the screen shows agrees with the
    /// refusal — they are computed by different services and would otherwise
    /// drift.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_members_of_one_group_share_one_allowance()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.MaxSubmissionsPerProblem = 2;
            await context.SaveChangesAsync();
        }

        var first = await Build.ParticipantAsync(server, slug);
        var second = await Build.ParticipantAsync(server, slug);
        var group = await GroupAsync(admin, slug, "Para " + Guid.NewGuid().ToString("N")[..6]);
        await Sign.Succeeded(await AssignAsync(admin, slug, await WhoAsync(first), group));
        await Sign.Succeeded(await AssignAsync(admin, slug, await WhoAsync(second), group));

        await Build.SubmitAsync(first, slug, "print(1)\n");
        await Build.SubmitAsync(second, slug, "print(2)\n");

        // The screen and the refusal have to agree, so both are read.
        var problem = await first.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/problems/A");
        Assert.Equal(0, problem.GetProperty("submissionsLeft").GetInt32());

        var refused = await Build.TrySubmitAsync(first, slug, "print(3)\n");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("submission.limit", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Leaving a group does not hand somebody a fresh allowance, and does not
    /// charge them the group's either.
    /// <para>
    /// The ungrouped count is what they sent <b>while not in a group</b>. Count
    /// everything they ever sent and leaving costs them the group's spending;
    /// count only the group's and leaving is a way round the ceiling.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Leaving_a_group_keeps_a_persons_own_history_and_not_the_groups()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.MaxSubmissionsPerProblem = 2;
            await context.SaveChangesAsync();
        }

        var participant = await Build.ParticipantAsync(server, slug);
        var who = await WhoAsync(participant);

        // One as themselves.
        await Build.SubmitAsync(participant, slug, "print(1)\n");

        // Two as the group, which spends the group's allowance to its ceiling.
        var group = await GroupAsync(admin, slug, "Krótko " + Guid.NewGuid().ToString("N")[..6]);
        await Sign.Succeeded(await AssignAsync(admin, slug, who, group));
        await Build.SubmitAsync(participant, slug, "print(2)\n");
        await Build.SubmitAsync(participant, slug, "print(3)\n");
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Build.TrySubmitAsync(participant, slug, "print(4)\n")).StatusCode);

        // Out again: one of their own two is spent, so one is left — not none,
        // and not two.
        await Sign.Succeeded(await AssignAsync(admin, slug, who, null));
        var problem = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/problems/A");
        Assert.Equal(1, problem.GetProperty("submissionsLeft").GetInt32());
    }

    // ── the ranking shows groups ────────────────────────────────────────────

    /// <summary>
    /// A group holds one row and its members hold none, and that row is fed by
    /// every member's work.
    /// </summary>
    [Fact]
    public async Task A_group_holds_one_row_and_its_members_hold_none()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        var first = await Build.ParticipantAsync(server, slug);
        var second = await Build.ParticipantAsync(server, slug);
        var outsider = await Build.ParticipantAsync(server, slug);
        var (a, b) = (await WhoAsync(first), await WhoAsync(second));

        var group = await GroupAsync(admin, slug, "Zespół " + Guid.NewGuid().ToString("N")[..6]);
        await Sign.Succeeded(await AssignAsync(admin, slug, a, group));
        await Sign.Succeeded(await AssignAsync(admin, slug, b, group));

        await Build.SubmitAsync(first, slug, "print(1)\n");
        await Build.SubmitAsync(second, slug, "print(2)\n");
        await Build.SubmitAsync(outsider, slug, "print(3)\n");

        var board = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/results");
        var rows = board.GetProperty("contestants").EnumerateArray().ToList();

        var theirs = Assert.Single(rows, r => r.GetProperty("id").GetString() == group);
        Assert.Equal("group", theirs.GetProperty("kind").GetString());
        Assert.Equal("Klasa 3B", theirs.GetProperty("description").GetString());

        // Neither member has a row of their own; the one outside a group does.
        Assert.DoesNotContain(rows, r => r.GetProperty("id").GetString() == a);
        Assert.DoesNotContain(rows, r => r.GetProperty("id").GetString() == b);
        Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "user");

        // Both members' work lands in the group's row and nowhere else.
        var results = board.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count(r => r.GetProperty("contestantId").GetString() == group));
        Assert.DoesNotContain(results, r => r.GetProperty("contestantId").GetString() == a);
    }

    /// <summary>
    /// A member reads the board as their group, or their own row never
    /// highlights: the reader is not a contestant, the group is.
    /// </summary>
    [Fact]
    public async Task A_member_reads_the_board_as_their_group()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = await WhoAsync(participant);

        var group = await GroupAsync(admin, slug, "Ja " + Guid.NewGuid().ToString("N")[..6]);
        await Sign.Succeeded(await AssignAsync(admin, slug, who, group));

        var board = await participant.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{slug}/results");
        Assert.Equal(group, board.GetProperty("me").GetString());
    }

    /// <summary>
    /// A system group is in neither the contestants nor the results — the rule
    /// <c>Grant.IsSystem</c> applies to a person, one level up — and it still
    /// submits, which is what a check from the inside is for.
    /// </summary>
    [Fact]
    public async Task A_system_group_appears_nowhere_and_still_submits()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = await WhoAsync(participant);

        var group = await GroupAsync(
            admin, slug, "Kontrola " + Guid.NewGuid().ToString("N")[..6], isSystem: true);
        await Sign.Succeeded(await AssignAsync(admin, slug, who, group));

        await Build.SubmitAsync(participant, slug, "print(1)\n");

        var board = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/results");

        Assert.DoesNotContain(
            board.GetProperty("contestants").EnumerateArray(),
            r => r.GetProperty("id").GetString() == group);
        Assert.DoesNotContain(
            board.GetProperty("results").EnumerateArray(),
            r => r.GetProperty("contestantId").GetString() == group);
        // And the member does not reappear as themselves instead.
        Assert.DoesNotContain(
            board.GetProperty("contestants").EnumerateArray(),
            r => r.GetProperty("id").GetString() == who);
    }

    /// <summary>
    /// The roster is printed only where the activity says to — and the activity
    /// is told through its own API.
    /// <para>
    /// <b>This wrote the column straight into the database until 2026-08-26</b>,
    /// because nothing else could: <c>ShowGroupMembers</c> reached no DTO, so the
    /// setting <c>ResultsService</c> reads was writable from nowhere and every
    /// activity had held the default since groups arrived. The test passed
    /// throughout — <b>a test that reaches past the API to arrange its own
    /// premise cannot notice that the API has no way to arrange it</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_roster_is_printed_only_where_the_activity_says_to()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = await WhoAsync(participant);

        var group = await GroupAsync(admin, slug, "Skład " + Guid.NewGuid().ToString("N")[..6]);
        await Sign.Succeeded(await AssignAsync(admin, slug, who, group));

        var off = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/results");
        Assert.Empty(Row(off, group).GetProperty("members").EnumerateArray());

        await Sign.Succeeded(await SetRosterAsync(admin, slug, shown: true));

        var on = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/results");
        Assert.NotEmpty(Row(on, group).GetProperty("members").EnumerateArray());

        // And back off again: a switch that only travels one way is half a
        // switch, and `null` meaning "leave it alone" makes `false` the easy
        // half to lose.
        await Sign.Succeeded(await SetRosterAsync(admin, slug, shown: false));

        var again = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/results");
        Assert.Empty(Row(again, group).GetProperty("members").EnumerateArray());
    }

    /// <summary>
    /// The manager's own read carries it back.
    /// <para>
    /// Without this the settings screen can save the switch and cannot draw it,
    /// so every visit shows it off however it was left.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_roster_setting_reads_back_where_the_form_looks_for_it()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        var fresh = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/manager/activities/{slug}");
        Assert.False(fresh.GetProperty("showGroupMembers").GetBoolean());

        await Sign.Succeeded(await SetRosterAsync(admin, slug, shown: true));

        var saved = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/manager/activities/{slug}");
        Assert.True(saved.GetProperty("showGroupMembers").GetBoolean());
    }

    /// <summary>
    /// Saves the activity the way its settings screen does: read what is there,
    /// send it back with one field changed.
    /// </summary>
    private static async Task<HttpResponseMessage> SetRosterAsync(
        HttpClient admin, string slug, bool shown)
    {
        var activity = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/manager/activities/{slug}");
        return await admin.PutAsJsonAsync($"/api/v1/activities/{slug}", new
        {
            slug = activity.GetProperty("slug").GetString(),
            name = activity.GetProperty("name").GetString(),
            type = activity.GetProperty("type").GetString(),
            rankingType = activity.GetProperty("rankingType").GetString(),
            timeZone = activity.GetProperty("timeZone").GetString(),
            showGroupMembers = shown,
        });
    }

    /// <summary>
    /// Moving somebody leaves what they already sent where it was.
    /// <para>
    /// The rule the whole stamping arrangement exists for: a board read an hour
    /// ago still reconciles with the board now.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Moving_somebody_leaves_what_they_already_sent_where_it_was()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = await WhoAsync(participant);

        var before = await GroupAsync(admin, slug, "Przed " + Guid.NewGuid().ToString("N")[..6]);
        var after = await GroupAsync(admin, slug, "Po " + Guid.NewGuid().ToString("N")[..6]);

        await Sign.Succeeded(await AssignAsync(admin, slug, who, before));
        await Build.SubmitAsync(participant, slug, "print(1)\n");

        await Sign.Succeeded(await AssignAsync(admin, slug, who, after));
        await Build.SubmitAsync(participant, slug, "print(2)\n");

        var results = (await admin.GetFromJsonAsync<JsonElement>(
                $"/api/v1/activities/{slug}/results"))
            .GetProperty("results").EnumerateArray().ToList();

        Assert.Single(results, r => r.GetProperty("contestantId").GetString() == before);
        Assert.Single(results, r => r.GetProperty("contestantId").GetString() == after);
    }

    // ── the participant sees their group ────────────────────────────────────

    /// <summary>
    /// Somebody in a group reads its name, its description and who else is in
    /// it; somebody competing alone reads nothing.
    /// <para>
    /// Shown because sending as the group is compulsory rather than a choice:
    /// without it a person cannot tell why an allowance they never spent has
    /// gone down, or why their name is not in the ranking.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_participant_reads_their_own_group_and_only_their_own()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        var member = await Build.ParticipantAsync(server, slug);
        var teammate = await Build.ParticipantAsync(server, slug);
        var outsider = await Build.ParticipantAsync(server, slug);

        var group = await GroupAsync(admin, slug, "Nasza " + Guid.NewGuid().ToString("N")[..6]);
        await Sign.Succeeded(await AssignAsync(admin, slug, await WhoAsync(member), group));
        await Sign.Succeeded(await AssignAsync(admin, slug, await WhoAsync(teammate), group));

        var mine = await member.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}");
        var read = mine.GetProperty("group");
        Assert.Equal(group, read.GetProperty("id").GetString());
        Assert.Equal("Klasa 3B", read.GetProperty("description").GetString());
        Assert.Equal(2, read.GetProperty("members").GetArrayLength());

        // Somebody competing as themselves has none, rather than an empty one.
        var theirs = await outsider.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}");
        Assert.True(
            !theirs.TryGetProperty("group", out var absent)
                || absent.ValueKind == JsonValueKind.Null,
            "somebody outside a group is given one");
    }

    private static JsonElement Row(JsonElement board, string contestantId) =>
        board.GetProperty("contestants").EnumerateArray()
            .First(r => r.GetProperty("id").GetString() == contestantId);
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a running series puts out of reach.
/// <para>
/// Two filters, and they are different in kind. <b>Place</b>: a round with
/// address rules is served inside the room and absent everywhere else.
/// <b>Rank</b>: while it runs, anything below it is locked — visible, and
/// saying why.
/// </para>
/// <para>
/// <b>Neither is a permission.</b> The model has no subtraction in it; these
/// are applied after authorization, and every one of these tests is about a
/// reader who <i>may</i> and cannot reach it from here.
/// </para>
/// </summary>
[Collection("server-3")]
public class LockdownTests(ServerFixture server)
{
    private const string Room = "10.0.5.0/24";
    private const string Inside = "10.0.5.17";
    private const string Outside = "203.0.113.9";

    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    /// <summary>Where this client is standing. Set on the socket, not in a header the Server reads.</summary>
    private static HttpClient At(HttpClient client, string address)
    {
        client.DefaultRequestHeaders.Remove(ServerFixture.PeerHeader);
        client.DefaultRequestHeaders.Add(ServerFixture.PeerHeader, address);
        return client;
    }

    /// <summary>An address the Server cannot determine at all.</summary>
    private static HttpClient Nowhere(HttpClient client)
    {
        client.DefaultRequestHeaders.Remove(ServerFixture.PeerHeader);
        client.DefaultRequestHeaders.Add(ServerFixture.PeerHeader, "not-an-address");
        return client;
    }

    /// <summary>
    /// Sets a round's rank, scope and ranges, and opens it the way the scheduler
    /// does.
    /// <para>
    /// <b>`Installation` here, `Activity` in the model.</b> These tests were
    /// written against a floor that reached everywhere and still assert that; the
    /// product's default is the narrow one, and the tests that care say so.
    /// </para>
    /// </summary>
    private async Task RestrictAsync(
        string roundId, int importance, string? network = null, bool enabled = true,
        SeriesImportanceScope scope = SeriesImportanceScope.Installation)
    {
        var id = Guid.Parse(roundId);
        await using var context = server.NewContext();

        // The rules are written as rows of their own rather than through the
        // navigation: the parent carries `xmin` as a concurrency token, and
        // loading it with its collection to add a child made the update of the
        // row a fight it did not need to have.
        context.SeriesAddressRules.RemoveRange(
            await context.SeriesAddressRules.Where(r => r.SeriesId == id).ToListAsync());
        if (network is not null)
        {
            context.SeriesAddressRules.Add(new SeriesAddressRule
            {
                SeriesId = id,
                Network = IPNetwork.Parse(network),
            });
        }

        await context.Series.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Importance, importance)
            .SetProperty(s => s.ImportanceScope, scope)
            .SetProperty(s => s.RestrictionsEnabled, enabled)
            .SetProperty(s => s.IsOpen, true)
            .SetProperty(s => s.PausedAt, (DateTime?)null));

        await context.SaveChangesAsync();
    }

    private static async Task<List<string>> SeriesSlugsAsync(HttpClient client, string slug)
    {
        var rounds = await client.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/series");
        return rounds.EnumerateArray().Select(s => s.GetProperty("slug").GetString()!).ToList();
    }

    /// <summary>
    /// The activity by name.
    /// <para>
    /// <b>Not the first page of the list.</b> The whole suite shares one
    /// database and creates hundreds of activities, so paging found these
    /// reliably on their own and not at all in a full run — which is how three
    /// of these tests came to pass alone and fail together. The list is
    /// checked separately, by <see cref="ListedRowAsync"/>, which pages until it
    /// finds the row.
    /// </para>
    /// </summary>
    private static async Task<JsonElement> ActivityAsync(HttpClient client, string slug) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}");

    /// <summary>The same activity as the <b>list</b> shows it, wherever it fell.</summary>
    private static async Task<JsonElement?> ListedRowAsync(HttpClient client, string slug)
    {
        for (var page = 1; page <= 40; page++)
        {
            var answer = await client.GetFromJsonAsync<JsonElement>(
                $"/api/v1/activities?page={page}&pageSize=50");
            var items = answer.GetProperty("items").EnumerateArray().ToList();
            if (items.Count == 0) return null;
            foreach (var row in items)
            {
                if (row.GetProperty("slug").GetString() == slug) return row;
            }
        }
        return null;
    }

    /// <summary>Whether this answer carries a lockdown.</summary>
    private static bool IsLocked(JsonElement activity, out JsonElement locked) =>
        activity.TryGetProperty("locked", out locked) && locked.ValueKind != JsonValueKind.Null;

    // ── place ───────────────────────────────────────────────────────────────

    /// <summary>Inside the room the restricted round is served like any other.</summary>
    [Fact]
    public async Task A_restricted_round_is_served_from_a_listed_address()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        await RestrictAsync(roundId, SeriesImportance.OfficialContest, Room);

        var participant = At(await Build.ParticipantAsync(server, slug), Inside);

        Assert.Contains("r1", await SeriesSlugsAsync(participant, slug));
    }

    /// <summary>
    /// From anywhere else it is <b>absent</b>, not refused.
    /// <para>
    /// Absent rather than locked, because its dates and its problem count are
    /// exactly what it is withholding. "Not now, because of X" is a different
    /// message and belongs to the other filter.
    /// </para>
    /// </summary>
    [Fact]
    public async Task It_is_absent_from_anywhere_else()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        await RestrictAsync(roundId, SeriesImportance.OfficialContest, Room);

        var participant = At(await Build.ParticipantAsync(server, slug), Outside);

        Assert.DoesNotContain("r1", await SeriesSlugsAsync(participant, slug));
    }

    /// <summary>
    /// And it locks nothing while it is out of reach.
    /// <para>
    /// The reader is not in the contest, so the contest does not displace their
    /// coursework. A rank they cannot reach cannot be their floor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_round_it_cannot_reach_locks_nothing_for_that_reader()
    {
        var (contest, roundId) = await Build.ActivityAsync(server);
        var (course, _) = await Build.ActivityAsync(server);

        await RestrictAsync(roundId, SeriesImportance.OfficialContest, Room);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var participant = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{contest}/enrolment", new { }));
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        At(participant, Outside);

        var row = await ActivityAsync(participant, course);
        Assert.False(IsLocked(row, out _), $"a contest this reader cannot reach locked their course: {row}");
    }

    /// <summary>
    /// <b>An address the Server cannot read admits nobody and locks nobody.</b>
    /// <para>
    /// The half that matters is the second: a proxy that stops forwarding must
    /// not take a whole cohort's coursework with it. Nothing is gained by
    /// stripping the address either — the examination is what is lost.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_unknown_address_is_admitted_nowhere_and_locks_nothing()
    {
        var (contest, roundId) = await Build.ActivityAsync(server);
        var (course, _) = await Build.ActivityAsync(server);

        await RestrictAsync(roundId, SeriesImportance.OfficialContest, Room);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var participant = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{contest}/enrolment", new { }));
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        Nowhere(participant);

        Assert.DoesNotContain("r1", await SeriesSlugsAsync(participant, contest));

        var row = await ActivityAsync(participant, course);
        Assert.False(IsLocked(row, out _), $"an unreadable address locked a course: {row}");
    }

    // ── rank ────────────────────────────────────────────────────────────────

    /// <summary>
    /// While it runs, a lower-ranked activity is locked and the reason names it.
    /// </summary>
    [Fact]
    public async Task A_lower_ranked_activity_is_locked_and_says_which_round_did_it()
    {
        var (contest, roundId) = await Build.ActivityAsync(server);
        var (course, _) = await Build.ActivityAsync(server);

        await using (var context = server.NewContext())
        {
            var round = await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
            round.Name = "Finał krajowy";
            await context.SaveChangesAsync();
        }
        await RestrictAsync(roundId, SeriesImportance.OfficialContest, Room);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var participant = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{contest}/enrolment", new { }));
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        At(participant, Inside);

        var row = await ActivityAsync(participant, course);
        Assert.True(IsLocked(row, out var locked), $"the course should be locked: {row}");
        Assert.Equal("Finał krajowy", locked.GetProperty("seriesName").GetString());

        // **And the list says so too**, which is the surface the rule exists
        // for: a row that vanished during an examination reads as a fault, and
        // one that says why reads as a rule.
        var listed = await ListedRowAsync(participant, course);
        Assert.NotNull(listed);
        Assert.True(IsLocked(listed!.Value, out _), $"the list did not carry it: {listed}");

        // And the contest itself is not: it is what set the floor.
        var its = await ActivityAsync(participant, contest);
        Assert.False(IsLocked(its, out _), $"the round that set the floor locked itself: {its}");
    }

    /// <summary>
    /// <b>Equal rank survives.</b> Two contests in one room is the case §7 of
    /// the origin specification recorded as unanswered, and this is the answer:
    /// the floor is a maximum, and everything standing on it stays.
    /// </summary>
    [Fact]
    public async Task Two_rounds_of_equal_rank_both_stay()
    {
        var (first, firstRound) = await Build.ActivityAsync(server);
        var (second, secondRound) = await Build.ActivityAsync(server);

        await RestrictAsync(firstRound, SeriesImportance.Exam, Room);
        await RestrictAsync(secondRound, SeriesImportance.Exam, Room);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var participant = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{first}/enrolment", new { }));
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{second}/enrolment", new { }));

        At(participant, Inside);

        foreach (var slug in new[] { first, second })
        {
            var row = await ActivityAsync(participant, slug);
            Assert.False(IsLocked(row, out _), $"{slug} was locked by something of its own rank: {row}");

            // **And the round itself**, which is the assertion that bites the
            // comparison. The activity-level answer is a different expression —
            // "does it run anything at the floor" — so checking only that left
            // `<` and `<=` indistinguishable, and the first run of this test
            // proved it by passing under the wrong one.
            var rounds = await participant.GetFromJsonAsync<JsonElement>(
                $"/api/v1/activities/{slug}/series");
            var round = Assert.Single(rounds.EnumerateArray());
            Assert.False(
                round.TryGetProperty("locked", out var own) && own.ValueKind != JsonValueKind.Null,
                $"{slug}'s own round was locked by its own rank: {round}");
        }
    }

    /// <summary>
    /// It follows the grant, not the room: somebody in the same laboratory who
    /// is not taking part loses nothing.
    /// </summary>
    [Fact]
    public async Task Somebody_not_taking_part_is_untouched()
    {
        var (contest, roundId) = await Build.ActivityAsync(server);
        var (course, _) = await Build.ActivityAsync(server);

        await RestrictAsync(roundId, SeriesImportance.OfficialContest, Room);

        // Enrolled in the course only, and sitting in the room.
        var participant = At(await Build.ParticipantAsync(server, course), Inside);

        var row = await ActivityAsync(participant, course);
        Assert.False(IsLocked(row, out _), $"a contest they are not in locked their course: {row}");
    }

    /// <summary>
    /// Staff of the round doing the displacing keep everything — otherwise
    /// whoever runs the examination loses the panel they run it from.
    /// </summary>
    [Fact]
    public async Task Staff_of_the_locking_round_are_exempt()
    {
        var (contest, roundId) = await Build.ActivityAsync(server);
        var (course, _) = await Build.ActivityAsync(server);

        await RestrictAsync(roundId, SeriesImportance.OfficialContest, Room);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var staff = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await staff.PostAsJsonAsync($"/api/v1/activities/{contest}/enrolment", new { }));
        await Sign.Succeeded(await staff.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        var who = (await staff.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("userId").GetString()!;

        await using (var context = server.NewContext())
        {
            var activityId = (await context.Activities.FirstAsync(a => a.Slug == contest)).Id;
            var grant = await context.Grants.FirstAsync(g => g.ActivityId == activityId && g.UserId == who);
            grant.IsSystem = true;
            await context.SaveChangesAsync();
        }

        At(staff, Inside);

        var row = await ActivityAsync(staff, course);
        Assert.False(
            IsLocked(row, out _),
            $"the person running the contest was locked out of everything else: {row}");
    }

    // ── the two switches ────────────────────────────────────────────────────

    /// <summary>
    /// Either switch lifts both filters at once, and keeps the configuration.
    /// <para>
    /// The failure this exists for is a wrong list on the morning of a contest,
    /// so the answer cannot be "delete it and rebuild it afterwards".
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Either_switch_lifts_everything(bool instanceWide)
    {
        var (contest, roundId) = await Build.ActivityAsync(server);
        var (course, _) = await Build.ActivityAsync(server);

        await RestrictAsync(roundId, SeriesImportance.OfficialContest, Room);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var participant = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{contest}/enrolment", new { }));
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        At(participant, Outside);
        Assert.DoesNotContain("r1", await SeriesSlugsAsync(participant, contest));

        try
        {
            await using (var context = server.NewContext())
            {
                if (instanceWide)
                {
                    var instance = await context.Instance.FirstAsync();
                    instance.SeriesRestrictionsEnabled = false;
                }
                else
                {
                    var round = await context.Series.FirstAsync(s => s.Id == Guid.Parse(roundId));
                    round.RestrictionsEnabled = false;
                }
                await context.SaveChangesAsync();
            }

            // Out of the room, and it is there again.
            Assert.Contains("r1", await SeriesSlugsAsync(participant, contest));

            // And the rules are still stored, so turning it back on restores it.
            await using var check = server.NewContext();
            Assert.Single(await check.SeriesAddressRules
                .Where(r => r.SeriesId == Guid.Parse(roundId)).ToListAsync());
        }
        finally
        {
            // The instance row is shared by the whole suite.
            await using var context = server.NewContext();
            var instance = await context.Instance.FirstAsync();
            instance.SeriesRestrictionsEnabled = true;
            await context.SaveChangesAsync();
        }
    }

    // ── the paths a lockdown must reach ──────────────────────────────────────

    /// <summary>
    /// <b>A statement is reachable while one of its holders is</b>, and refused
    /// when none is.
    /// <para>
    /// The sharp one. A problem is often attached in several places and the file
    /// is authorised by "any holder", so without this the statement of a locked
    /// examination is served through whichever open course also holds it —
    /// addressed by file id, past every list that hides it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_statement_is_refused_only_when_every_holder_is_out_of_reach()
    {
        var (contest, contestRound) = await Build.ActivityAsync(server);
        var (course, courseRound) = await Build.ActivityAsync(server);

        // One problem, attached in both.
        Guid problemId;
        string statementId;
        await using (var context = server.NewContext())
        {
            var assignment = await context.SeriesProblems
                .Include(sp => sp.Problem)
                .FirstAsync(sp => sp.Series!.Id == Guid.Parse(contestRound));
            problemId = assignment.ProblemId;

            var version = await context.ProblemVersions
                .Where(v => v.ProblemId == problemId)
                .OrderByDescending(v => v.Version)
                .FirstAsync();
            statementId = (await context.FileReferences
                .Where(r => r.ProblemVersionId == version.Id && r.Scope == FileScope.Participant)
                .Select(r => r.FileId)
                .FirstAsync()).ToString();
        }

        var admin = await AdminAsync(server);
        await Build.PostAsync(admin, $"/api/v1/series/{courseRound}/problems", new
        {
            // "B": the course already holds one of its own at "A". What matters
            // is that one **problem** now hangs in two places.
            problemId = problemId.ToString(), slug = "B", maxPoints = 50,
        });

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var participant = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{contest}/enrolment", new { }));
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        // Two rooms, one problem. The examination is in this one; the course's
        // own round is restricted to another building.
        await RestrictAsync(contestRound, SeriesImportance.Exam, Room);
        await RestrictAsync(courseRound, SeriesImportance.Normal, "10.0.9.0/24");

        // **Inside the examination room one holder is reachable, so it is
        // served** — even though the course holding the same problem is not.
        // That is the owner's rule: any accessible holder, and the file does not
        // ask which door it was opened through.
        At(participant, Inside);
        Assert.True(
            (await participant.GetAsync($"/api/v1/files/{statementId}")).IsSuccessStatusCode,
            "a statement was refused while one of its holders was reachable");

        // Standing in neither room, every holder is out of reach.
        At(participant, Outside);
        var refused = await participant.GetAsync($"/api/v1/files/{statementId}");
        Assert.False(
            refused.IsSuccessStatusCode,
            "the statement was served though every round holding it was out of reach");
    }

    /// <summary>A locked activity accepts no submission, and says why.</summary>
    [Fact]
    public async Task A_locked_activity_refuses_a_submission()
    {
        var (contest, roundId) = await Build.ActivityAsync(server);
        var (course, _) = await Build.ActivityAsync(server);

        await RestrictAsync(roundId, SeriesImportance.Exam, Room);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var participant = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{contest}/enrolment", new { }));
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        At(participant, Inside);

        var refused = await Build.TrySubmitAsync(participant, course, "print(1)\n");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(LockdownCodes.Displaced, await refused.Content.ReadAsStringAsync());
    }

    // ── what a manager may configure ────────────────────────────────────────

    /// <summary>
    /// A round with no dates may restrict nothing: one with no end imposes a
    /// lockdown that never lifts.
    /// </summary>
    [Fact]
    public async Task A_round_without_both_dates_may_not_restrict_anything()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        var refused = await admin.PostAsJsonAsync($"/api/v1/activities/{slug}/series", new
        {
            slug = "open-ended",
            name = "Bez końca",
            startDate = DateTime.UtcNow.ToString("O"),
            importance = SeriesImportance.Exam,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("series.restrictions.needDates", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// <b><c>10.0.5.17/24</c> is refused.</b> It is a typo for a whole
    /// laboratory and it is the typo somebody makes — host bits set on a range.
    /// </summary>
    [Fact]
    public async Task A_range_with_host_bits_set_is_refused()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        var refused = await admin.PutAsJsonAsync($"/api/v1/series/{roundId}", new
        {
            slug = "r1",
            name = "Round 1",
            startDate = DateTime.UtcNow.AddHours(-1).ToString("O"),
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            addressRules = new[] { new { network = "10.0.5.17/24", note = (string?)null } },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("series.address.invalid", await refused.Content.ReadAsStringAsync());
        Assert.NotNull(slug);
    }

    // ── two rounds in one activity ──────────────────────────────────────────
    //
    // **The shape nothing tested.** Every activity these tests build ran one
    // round, so the question an activity-scoped rank answers — what a round does
    // to its neighbours — was never asked. Everything below stands two rounds in
    // one activity and one activity beside it.

    /// <summary>The rounds of an activity, by their slug.</summary>
    private static async Task<Dictionary<string, JsonElement>> RoundsAsync(
        HttpClient client, string slug)
    {
        var rounds = await client.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{slug}/series");
        return rounds.EnumerateArray().ToDictionary(s => s.GetProperty("slug").GetString()!, s => s);
    }

    private static bool RoundIsLocked(JsonElement round, out string by)
    {
        by = "";
        if (!round.TryGetProperty("locked", out var locked) || locked.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        by = locked.GetProperty("seriesName").GetString() ?? "";
        return true;
    }

    /// <summary>
    /// A course with an examination running in it and an ordinary round beside
    /// it, plus an unrelated course, and somebody in both.
    /// </summary>
    private async Task<(string Course, string Other, HttpClient Reader, string Login)> TwoRoundsAsync(
        SeriesImportanceScope scope, int examRank = SeriesImportance.Exam)
    {
        var (course, _) = await Build.ActivityAsync(server);
        var examRound = await Build.SecondRoundAsync(server, course);
        var (other, _) = await Build.ActivityAsync(server);

        await using (var context = server.NewContext())
        {
            var round = await context.Series.FirstAsync(s => s.Id == Guid.Parse(examRound));
            round.Name = "Kolokwium 2";
            await context.SaveChangesAsync();
        }
        await RestrictAsync(examRound, examRank, scope: scope);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var reader = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{other}/enrolment", new { }));
        At(reader, Inside);

        return (course, other, reader, login);
    }

    /// <summary>
    /// An examination displaces the other rounds of its own activity, and the
    /// activity itself stays open — it is running the examination.
    /// </summary>
    [Fact]
    public async Task An_activity_scoped_round_locks_its_neighbours_and_not_its_activity()
    {
        var (course, _, reader, _) = await TwoRoundsAsync(SeriesImportanceScope.Activity);

        var shell = await ActivityAsync(reader, course);
        Assert.False(IsLocked(shell, out _),
            $"the activity running the examination locked itself: {shell}");

        var rounds = await RoundsAsync(reader, course);
        Assert.False(RoundIsLocked(rounds["r2"], out _), "the examination locked itself");

        Assert.True(RoundIsLocked(rounds["r1"], out var by),
            $"the ordinary round was not displaced: {rounds["r1"]}");
        Assert.Equal("Kolokwium 2", by);

        // Its problems go with it, the way a closed round's do. Absent or null:
        // the serializer omits a null, so asking for the property would throw.
        Assert.True(
            !rounds["r1"].TryGetProperty("problems", out var withheld)
                || withheld.ValueKind == JsonValueKind.Null,
            $"a displaced round still carried its problems: {rounds["r1"]}");
    }

    /// <summary>
    /// And nothing outside that activity notices. This is the whole point of the
    /// scope: a lecturer marking one round an examination must not lock their
    /// students out of every other course on the installation.
    /// </summary>
    [Fact]
    public async Task An_activity_scoped_round_leaves_every_other_activity_alone()
    {
        var (_, other, reader, _) = await TwoRoundsAsync(SeriesImportanceScope.Activity);

        var shell = await ActivityAsync(reader, other);
        Assert.False(IsLocked(shell, out _), $"an unrelated course was locked: {shell}");

        var rounds = await RoundsAsync(reader, other);
        Assert.False(RoundIsLocked(rounds["r1"], out var by),
            $"an unrelated course's round was displaced by \"{by}\"");

        // And it still takes submissions, which is the thing somebody notices.
        var sent = await Build.TrySubmitAsync(reader, other, "print(1)\n");
        Assert.True(sent.IsSuccessStatusCode,
            $"an unrelated course refused a submission: {await sent.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// <b>The installation scope still reaches out</b>, which is what makes the
    /// two worth having as a choice rather than a replacement.
    /// </summary>
    [Fact]
    public async Task An_installation_scoped_round_still_locks_other_activities()
    {
        var (_, other, reader, _) = await TwoRoundsAsync(SeriesImportanceScope.Installation);

        var shell = await ActivityAsync(reader, other);
        Assert.True(IsLocked(shell, out var locked),
            $"an installation-scoped examination left another course open: {shell}");
        Assert.Equal("Kolokwium 2", locked.GetProperty("seriesName").GetString());
    }

    /// <summary>Equal rank survives inside one activity, as it does across two.</summary>
    [Fact]
    public async Task Two_rounds_of_equal_rank_in_one_activity_both_stay()
    {
        var (course, first) = await Build.ActivityAsync(server);
        var second = await Build.SecondRoundAsync(server, course);

        await RestrictAsync(first, SeriesImportance.Exam, scope: SeriesImportanceScope.Activity);
        await RestrictAsync(second, SeriesImportance.Exam, scope: SeriesImportanceScope.Activity);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var reader = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));
        At(reader, Inside);

        var rounds = await RoundsAsync(reader, course);
        foreach (var (slug, round) in rounds)
        {
            Assert.False(RoundIsLocked(round, out var by),
                $"{slug} was displaced by \"{by}\", which is its own rank");
        }
    }

    /// <summary>Staff of the activity keep the rounds they are running.</summary>
    [Fact]
    public async Task Staff_are_exempt_from_their_own_activitys_floor()
    {
        var (course, _, reader, login) = await TwoRoundsAsync(SeriesImportanceScope.Activity);

        var displaced = await RoundsAsync(reader, course);
        Assert.True(RoundIsLocked(displaced["r1"], out _), "the fixture stopped displacing anything");

        await using (var context = server.NewContext())
        {
            var grant = await context.Grants
                .FirstAsync(g => g.Activity!.Slug == course && g.User!.UserName == login);
            grant.IsSystem = true;
            await context.SaveChangesAsync();
        }

        var rounds = await RoundsAsync(reader, course);
        Assert.False(RoundIsLocked(rounds["r1"], out var by),
            $"staff lost their own activity's round to \"{by}\"");
    }

    // ── the round-granular paths ────────────────────────────────────────────
    //
    // An activity-scoped floor never locks the activity, so "all of it or none
    // of it" stops being an answer. Each of these used to refuse the whole
    // activity and now has to drop one round out of it.

    /// <summary>
    /// The board loses the displaced round's column and keeps the rest — a
    /// ranking read during an examination shows what is still being fought.
    /// </summary>
    [Fact]
    public async Task The_board_drops_a_displaced_round_and_keeps_the_others()
    {
        var (course, _) = await Build.ActivityAsync(server);
        var examRound = await Build.SecondRoundAsync(server, course);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var reader = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        // Both rounds have something in them before anything is out of reach.
        Assert.True((await Build.TrySubmitAsync(reader, course, "print(1)\n")).IsSuccessStatusCode);
        Assert.True((await Build.TrySubmitAsync(reader, course, "print(2)\n", "B")).IsSuccessStatusCode);

        await RestrictAsync(examRound, SeriesImportance.Exam, scope: SeriesImportanceScope.Activity);
        At(reader, Inside);

        var board = await reader.GetFromJsonAsync<JsonElement>($"/api/v1/activities/{course}/results");
        // By name: a board's rounds carry an id and a name, never a slug.
        var columns = board.GetProperty("series").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToList();

        Assert.Contains("Round r2", columns);
        Assert.DoesNotContain("Round 1", columns);
    }

    /// <summary>
    /// Their own work in a displaced round goes with it — re-reading last week's
    /// accepted solution during an examination is what this exists to stop — and
    /// their work in the round they are sitting stays.
    /// </summary>
    [Fact]
    public async Task Own_submissions_from_a_displaced_round_are_withheld()
    {
        var (course, _) = await Build.ActivityAsync(server);
        var examRound = await Build.SecondRoundAsync(server, course);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var reader = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        var old = await Build.SubmitAsync(reader, course, "print(1)\n");
        Assert.True((await Build.TrySubmitAsync(reader, course, "print(2)\n", "B")).IsSuccessStatusCode);

        await RestrictAsync(examRound, SeriesImportance.Exam, scope: SeriesImportanceScope.Activity);
        At(reader, Inside);

        var listed = await reader.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{course}/submissions?page=1&pageSize=50");
        var problems = listed.GetProperty("items").EnumerateArray()
            .Select(s => s.GetProperty("problemSlug").GetString()).ToList();

        Assert.Contains("B", problems);
        Assert.DoesNotContain("A", problems);

        // **And by id, which is how a bookmark walks past a list.**
        var byId = await reader.GetAsync(
            $"/api/v1/activities/{course}/submissions/{old.GetProperty("id").GetString()}");
        Assert.Equal(HttpStatusCode.Forbidden, byId.StatusCode);
        Assert.Contains(LockdownCodes.Displaced, await byId.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A question about a displaced round goes with the round. One about the
    /// activity stays: an announcement is how the organiser explains a lockdown.
    /// </summary>
    [Fact]
    public async Task Questions_about_a_displaced_round_are_withheld_and_asking_is_refused()
    {
        var (course, ordinaryRound) = await Build.ActivityAsync(server);
        var examRound = await Build.SecondRoundAsync(server, course);

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var reader = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));

        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{course}/questions", new
        {
            topic = "O rundzie", body = "Pytanie o rundę", seriesId = ordinaryRound,
        }));
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{course}/questions", new
        {
            topic = "O kursie", body = "Pytanie o kurs",
        }));

        await RestrictAsync(examRound, SeriesImportance.Exam, scope: SeriesImportanceScope.Activity);
        At(reader, Inside);

        var listed = await reader.GetFromJsonAsync<JsonElement>(
            $"/api/v1/activities/{course}/questions?page=1&pageSize=50");
        var topics = listed.GetProperty("items").EnumerateArray()
            .Select(q => q.GetProperty("topic").GetString()).ToList();

        Assert.Contains("O kursie", topics);
        Assert.DoesNotContain("O rundzie", topics);

        // Asking was the one way in that nothing guarded.
        var refused = await reader.PostAsJsonAsync($"/api/v1/activities/{course}/questions", new
        {
            topic = "Jeszcze jedno", body = "Nowe pytanie", seriesId = ordinaryRound,
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// <b>The floor is that activity's.</b> A problem displaced in one course is
    /// still published in another that holds it, and the statement is served
    /// there — the rule stays "any accessible holder", now judged per activity.
    /// </summary>
    [Fact]
    public async Task A_statement_follows_the_holding_activitys_own_floor()
    {
        var (course, ordinaryRound) = await Build.ActivityAsync(server);
        var examRound = await Build.SecondRoundAsync(server, course);
        var (open, openRound) = await Build.ActivityAsync(server);

        Guid problemId;
        string statementId;
        await using (var context = server.NewContext())
        {
            var assignment = await context.SeriesProblems
                .FirstAsync(sp => sp.SeriesId == Guid.Parse(ordinaryRound));
            problemId = assignment.ProblemId;

            var version = await context.ProblemVersions
                .Where(v => v.ProblemId == problemId)
                .OrderByDescending(v => v.Version)
                .FirstAsync();
            statementId = (await context.FileReferences
                .Where(r => r.ProblemVersionId == version.Id && r.Scope == FileScope.Participant)
                .Select(r => r.FileId)
                .FirstAsync()).ToString();
        }

        var admin = await AdminAsync(server);
        await Build.PostAsync(admin, $"/api/v1/series/{openRound}/problems", new
        {
            problemId = problemId.ToString(), slug = "C", maxPoints = 50,
        });

        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        var reader = await Sign.NewAccountAsync(server, login);
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{course}/enrolment", new { }));
        await Sign.Succeeded(await reader.PostAsJsonAsync($"/api/v1/activities/{open}/enrolment", new { }));

        await RestrictAsync(examRound, SeriesImportance.Exam, scope: SeriesImportanceScope.Activity);
        At(reader, Inside);

        // Served: the other course holds it and nothing displaces it there.
        Assert.True(
            (await reader.GetAsync($"/api/v1/files/{statementId}")).IsSuccessStatusCode,
            "a statement was refused though an untouched course still published it");

        // Refused once that course is out of reach too — here by leaving it.
        await using (var context = server.NewContext())
        {
            await context.Grants
                .Where(g => g.Activity!.Slug == open && g.User!.UserName == login)
                .ExecuteDeleteAsync();
        }
        var refused = await reader.GetAsync($"/api/v1/files/{statementId}");
        Assert.False(refused.IsSuccessStatusCode,
            "the statement was served though every holder the reader has is displaced");
    }

    /// <summary>
    /// Submitting into the displaced round is refused and into the examination
    /// is not — one activity, two answers.
    /// </summary>
    [Fact]
    public async Task Submitting_is_refused_for_the_displaced_round_only()
    {
        var (course, _, reader, _) = await TwoRoundsAsync(SeriesImportanceScope.Activity);

        var refused = await Build.TrySubmitAsync(reader, course, "print(1)\n");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(LockdownCodes.Displaced, await refused.Content.ReadAsStringAsync());

        var accepted = await Build.TrySubmitAsync(reader, course, "print(2)\n", "B");
        Assert.True(accepted.IsSuccessStatusCode,
            $"the examination refused its own submission: {await accepted.Content.ReadAsStringAsync()}");
    }

    /// <summary>A scope this Server does not know is refused rather than stored.</summary>
    [Fact]
    public async Task An_unknown_scope_is_refused()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        var refused = await admin.PutAsJsonAsync($"/api/v1/series/{roundId}", new
        {
            slug = "r1",
            name = "Round 1",
            startDate = DateTime.UtcNow.AddHours(-1).ToString("O"),
            endDate = DateTime.UtcNow.AddDays(1).ToString("O"),
            importance = SeriesImportance.Exam,
            importanceScope = "everywhere",
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("series.importanceScope.unknown", await refused.Content.ReadAsStringAsync());
        Assert.NotNull(slug);
    }
}

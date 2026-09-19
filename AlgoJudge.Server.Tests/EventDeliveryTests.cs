using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Whether the thing that changed said so, and to the people it concerns.
/// <para>
/// Two guards already stand either side of this and neither can see it.
/// <c>EventCatalogTests</c> proves every declared name is <i>sent by
/// something</i>; the Client's <c>check:events</c> proves the two sides
/// <i>spell the names alike</i>. Both were green while a paused round told
/// nobody in it, a question reached no manager, and a blocked account moved no
/// list — because the question neither of them asks is whether <b>this</b>
/// domain action announces <b>that</b> change.
/// </para>
/// <para>
/// So these drive the action over HTTP, exactly as a person would, and read
/// what came out of the hub. Asserting the <b>recipients</b> as well as the
/// frame is the point: an event sent to the wrong audience is the failure that
/// looks identical to a working one from the sender's side, and
/// <c>managerSeriesChanged</c> going to staff while the participants heard
/// nothing is precisely that failure.
/// </para>
/// </summary>
[Collection("server-2")]
public class EventDeliveryTests(ServerFixture server)
{
    /// <summary>
    /// A host whose hub counts instead of writing to sockets.
    /// <para>
    /// It shares the fixture's database, so anything built through
    /// <see cref="Build"/> beforehand is visible — but only what is driven
    /// through <b>this</b> host is counted. Every action a test is asserting
    /// about therefore has to go through the client this returns.
    /// </para>
    /// </summary>
    private (WebApplicationFactory<Program> Host, CountingEventHub Hub) Counting()
    {
        var hub = new CountingEventHub();
        var host = server.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IEventHub>(hub)));
        return (host, hub);
    }

    /// <summary>
    /// Someone enrolled in the activity, signed in against the counting host,
    /// and their id — which is what an audience is a list of.
    /// </summary>
    private async Task<(HttpClient Client, string Id)> EnrolledAsync(
        WebApplicationFactory<Program> host, string slug)
    {
        var login = "p-" + Guid.NewGuid().ToString("N")[..10];
        // Created against the shared database; signed in against the host whose
        // hub is being read.
        (await Sign.NewAccountAsync(server, login)).Dispose();
        var client = await Sign.InAsync(host, login, Sign.Password);
        await Sign.Succeeded(await client.PostAsJsonAsync($"/api/v1/activities/{slug}/enrollment", new { }));
        return (client, await IdOfAsync(login));
    }

    private async Task<string> IdOfAsync(string login)
    {
        await using var context = server.NewContext();
        return (await context.Users.AsNoTracking().FirstAsync(u => u.UserName == login)).Id;
    }

    private static List<string> ToldOf(CountingEventHub hub, string type) =>
        [.. hub.Addressed.Where(a => a.Type == type).SelectMany(a => a.To)];

    private static List<SeriesChangedData> Changes(CountingEventHub hub, string change) =>
        [.. hub.Sent
            .Where(s => s.Type == EventTypes.SeriesChanged)
            .Select(s => s.Data)
            .OfType<SeriesChangedData>()
            .Where(d => d.Change == change)];

    // ── the round, as the people taking it experience it ─────────────────────

    /// <summary>
    /// <b>The reported defect.</b>
    /// <para>
    /// Pausing announced <c>managerSeriesChanged</c> and nothing else, and that
    /// event's audience is <c>activity:update</c> — the staff. A contestant's
    /// clock therefore kept counting down a round that was standing still, the
    /// statements stayed on screen, and the first they learned of it was a
    /// refused submission.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_paused_round_reaches_the_people_in_it()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var (host, hub) = Counting();
        using var _ = host;

        var (participant, participantId) = await EnrolledAsync(host, slug);
        using var __ = participant;
        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/pause", new { hideProblems = false }));

        Assert.Single(Changes(hub, "paused"));
        Assert.Contains(participantId, ToldOf(hub, EventTypes.SeriesChanged));
    }

    /// <summary>
    /// <b>And resuming gives the statements back over the wire, not only in the
    /// database.</b>
    /// <para>
    /// This is the one that catches a half-loaded round. The payload withholds
    /// the problems unless <c>ISeriesGate.MayReadProblems</c> allows them, and
    /// that reads <c>round.Activity</c> and <c>round.SeriesProblems</c> — neither
    /// of which <c>ManagerWriteService.Round()</c> includes. An announcer handed
    /// the caller's entity would answer "not open", send an empty round, and
    /// every screen would redraw as though the statements were still hidden.
    /// Nothing would throw.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_resumed_round_carries_the_statements_back()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);

        // Paused with the statements taken away, so resuming has something to
        // give back. Done off the counting host: only the resume is asserted.
        using (var setup = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword))
        {
            await Sign.Succeeded(await setup.PostAsJsonAsync(
                $"/api/v1/series/{roundId}/pause", new { hideProblems = true }));
        }

        var (host, hub) = Counting();
        using var _ = host;

        var (participant, participantId) = await EnrolledAsync(host, slug);
        using var __ = participant;
        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/resume", new { extendEnd = false }));

        var resumed = Assert.Single(Changes(hub, "resumed"));
        Assert.NotNull(resumed.Series.Problems);
        Assert.NotEmpty(resumed.Series.Problems!);
        Assert.Contains(participantId, ToldOf(hub, EventTypes.SeriesChanged));
    }

    // ── the clarifications desk ──────────────────────────────────────────────

    /// <summary>
    /// A question arrives on the manager's list without them asking for it.
    /// <para>
    /// <c>QuestionService</c> did not take <c>IEventHub</c> at all, so the one
    /// screen whose whole purpose is to be watched during a contest was the one
    /// screen nothing ever woke.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_question_reaches_a_manager_who_is_looking_at_the_list()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var (host, hub) = Counting();
        using var __ = host;

        var (asker, _) = await EnrolledAsync(host, slug);
        using var ___ = asker;

        await Sign.Succeeded(await asker.PostAsJsonAsync($"/api/v1/activities/{slug}/questions", new
        {
            topic = "Limit czasu",
            body = "Czy limit dotyczy jednego testu?",
        }));

        Assert.Contains(
            await IdOfAsync(Seeder.DevAdminLogin),
            ToldOf(hub, EventTypes.QuestionChanged));
    }

    // ── the panel's submissions list ─────────────────────────────────────────

    /// <summary>
    /// <b>A submission appearing reaches the screen that lists submissions.</b>
    /// <para>
    /// <c>submissionChanged</c> was sent from two places — canceling an attempt
    /// and ruling one out of the ranking — and from nowhere else. Everything
    /// that actually moves a submission announced only the participant's
    /// <c>submissionStateChanged</c>, which is routed to a different dispatcher,
    /// so a contest's submissions screen sat still while the contest ran. The
    /// Client had been built for the opposite: its handler patches the row in
    /// place precisely so a rejudge walking through queued and running would not
    /// reload the page three times.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_new_submission_reaches_the_panel_that_lists_them()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var (host, hub) = Counting();
        using var _ = host;

        var (participant, _) = await EnrolledAsync(host, slug);
        using var __ = participant;

        await Build.SubmitAsync(participant, slug, "print(1)\n");

        Assert.Contains(
            await IdOfAsync(Seeder.DevAdminLogin),
            ToldOf(hub, EventTypes.SubmissionChanged));
    }

    // ── the people list ──────────────────────────────────────────────────────

    /// <summary>
    /// Blocking somebody moves the list it was done from.
    /// <para>
    /// <c>SetBlockedAsync</c> announced nothing, and <c>userChanged</c> — the
    /// event the users screen listens for — went only to the subject, who is the
    /// one person on the installation not looking at that screen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_blocked_account_reaches_the_screen_that_blocked_it()
    {
        var (host, hub) = Counting();
        using var _ = host;

        var login = "b-" + Guid.NewGuid().ToString("N")[..10];
        (await Sign.NewAccountAsync(server, login)).Dispose();
        var subject = await IdOfAsync(login);

        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/users/{subject}/blocked", new { blocked = true, reason = "Na wniosek" }));

        Assert.Contains(await IdOfAsync(Seeder.DevAdminLogin), ToldOf(hub, EventTypes.UserChanged));
    }

    /// <summary>
    /// A question asked by somebody in the activity, ready to be answered.
    /// </summary>
    private async Task<(string Id, string AskerId)> QuestionAsync(
        WebApplicationFactory<Program> host, string slug)
    {
        var (asker, askerId) = await EnrolledAsync(host, slug);
        using var _ = asker;
        var asked = await Build.PostAsync(asker, $"/api/v1/activities/{slug}/questions", new
        {
            topic = "Czy wolno użyć biblioteki standardowej?",
            body = "Pytam o STL.",
        });
        return (asked.GetProperty("id").GetString()!, askerId);
    }

    /// <summary>
    /// <b><c>questionPublished</c> could never be sent.</b> The type was chosen
    /// from whether an answer existed, and a question is only announced widely
    /// once it is published — which is refused while it is unanswered. So every
    /// wide frame took the <c>questionAnswered</c> arm, which the participant's
    /// list <i>patches</i> rather than refetches: a row that was not on screen
    /// stayed off it until the page was reloaded by hand.
    /// </summary>
    [Fact]
    public async Task Publishing_an_answer_tells_the_activity_a_question_appeared()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var (host, hub) = Counting();
        using var _ = host;

        var (questionId, _) = await QuestionAsync(host, slug);
        var (reader, readerId) = await EnrolledAsync(host, slug);
        using var __ = reader;
        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/questions/{questionId}/answer", new { body = "Tak, wolno.", publish = true }));

        Assert.Contains(readerId, ToldOf(hub, EventTypes.QuestionPublished));
    }

    /// <summary>
    /// <b>Withdrawing told nobody anything</b>, so an answer taken back during a
    /// contest stayed on every screen that already had it. Everybody but the
    /// asker loses the row; the asker keeps theirs, private again.
    /// </summary>
    [Fact]
    public async Task Withdrawing_an_answer_takes_the_row_off_everybody_elses_screen()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var (host, hub) = Counting();
        using var _ = host;

        var (questionId, askerId) = await QuestionAsync(host, slug);
        var (reader, readerId) = await EnrolledAsync(host, slug);
        using var __ = reader;
        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/questions/{questionId}/answer", new { body = "Tak, wolno.", publish = true }));
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/questions/{questionId}/published", new { published = false }));

        // **Found by what it carries, not by when it arrived.** Publishing sends
        // one frame per recipient — `isRead` is the reader's own — so several
        // `questionPublished` frames are in flight and a bag has no order to
        // read them in. The withdrawal is the one naming a row to drop.
        var withdrawal = Assert.Single(hub.Frames, f =>
            f.Type == EventTypes.QuestionPublished
            && JsonSerializer.Serialize(f.Data).Contains("deletedId"));

        Assert.Contains(readerId, withdrawal.To);
        Assert.DoesNotContain(askerId, withdrawal.To);
    }

    /// <summary>
    /// <b>The participant's frame carried the manager's projection</b> — how many
    /// people had read the question, and the asker's user id — to every
    /// participant in the activity.
    /// </summary>
    [Fact]
    public async Task A_participants_question_frame_carries_none_of_the_staff_fields()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var (host, hub) = Counting();
        using var _ = host;

        var (questionId, _) = await QuestionAsync(host, slug);
        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/questions/{questionId}/answer", new { body = "Tak, wolno.", publish = true }));

        var frame = hub.Sent.First(s => s.Type == EventTypes.QuestionPublished).Data;
        // **Lower-cased, because the default serializer keeps PascalCase.** The
        // first version of this asserted on camelCase names that the text never
        // held either way, so it passed whichever projection was sent — the
        // sabotage proved it, not a reading.
        var wire = JsonSerializer.Serialize(frame).ToLowerInvariant();

        Assert.DoesNotContain("readcount", wire);
        Assert.DoesNotContain("authoruserid", wire);
        // And it is still the question, not an empty object.
        Assert.Contains("biblioteki standardowej", wire);
    }

    /// <summary>
    /// <b>Posting was the one write on this entity that told no manager.</b>
    /// Asking, answering, publishing and deleting all announce to the staff;
    /// a second manager's list stood still while announcements went out.
    /// </summary>
    [Fact]
    public async Task An_announcement_reaches_the_panel_it_was_written_in()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var (host, hub) = Counting();
        using var _ = host;

        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/activities/{slug}/announcements",
            new { topic = "Zajęcia odwołane", body = "W czwartek nie ma zajęć." }));

        Assert.Contains(hub.Sent, s => s.Type == EventTypes.QuestionChanged);
    }
}

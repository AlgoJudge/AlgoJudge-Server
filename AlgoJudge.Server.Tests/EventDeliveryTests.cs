using System.Net.Http.Json;
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
/// <c>EventCatalogueTests</c> proves every declared name is <i>sent by
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
        await Sign.Succeeded(await client.PostAsJsonAsync($"/api/v1/activities/{slug}/enrolment", new { }));
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

    // ── the round, as the people sitting in it experience it ─────────────────

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
}

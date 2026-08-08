using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The two workers, driven against a clock rather than waited for.
/// <para>
/// Nothing here sleeps. A test that waited thirty seconds for a scheduler tick
/// would be a test somebody eventually deletes.
/// </para>
/// </summary>
[Collection("server")]
public class WorkerTests(ServerFixture server)
{
    // ── the series scheduler ────────────────────────────────────────────────

    [Fact]
    public async Task A_round_whose_start_has_passed_is_opened_exactly_once()
    {
        var roundId = await NewRoundAsync(
            start: DateTime.UtcNow.AddMinutes(-1), end: DateTime.UtcNow.AddHours(1));

        var scheduler = server.Services.GetRequiredService<SeriesScheduler>();

        var first = await scheduler.TickAsync(CancellationToken.None);
        Assert.True(first >= 1);

        await using (var context = server.NewContext())
        {
            var round = await context.Series.FirstAsync(s => s.Id == roundId);
            Assert.True(round.IsOpen);
            Assert.NotNull(round.StartAnnouncedAt);
        }

        // The marker is what makes it exactly-once: a second pass finds nothing
        // to do for this round, so nobody is told twice.
        await using (var context = server.NewContext())
        {
            var before = await context.Series.FirstAsync(s => s.Id == roundId);
            var announcedAt = before.StartAnnouncedAt;

            await scheduler.TickAsync(CancellationToken.None);

            await using var after = server.NewContext();
            var round = await after.Series.FirstAsync(s => s.Id == roundId);
            Assert.Equal(announcedAt, round.StartAnnouncedAt);
        }
    }

    [Fact]
    public async Task A_round_whose_start_is_in_the_future_is_left_alone()
    {
        var roundId = await NewRoundAsync(
            start: DateTime.UtcNow.AddDays(1), end: DateTime.UtcNow.AddDays(2));

        await server.Services.GetRequiredService<SeriesScheduler>().TickAsync(CancellationToken.None);

        await using var context = server.NewContext();
        var round = await context.Series.FirstAsync(s => s.Id == roundId);
        Assert.False(round.IsOpen);
        Assert.Null(round.StartAnnouncedAt);
    }

    [Fact]
    public async Task A_round_whose_end_has_passed_is_closed_and_never_opened_first()
    {
        // Created wholly in the past: the Server was down for the whole of it,
        // or somebody entered last week's round today.
        var roundId = await NewRoundAsync(
            start: DateTime.UtcNow.AddHours(-3), end: DateTime.UtcNow.AddHours(-2));

        await server.Services.GetRequiredService<SeriesScheduler>().TickAsync(CancellationToken.None);

        await using var context = server.NewContext();
        var round = await context.Series.FirstAsync(s => s.Id == roundId);

        Assert.False(round.IsOpen);
        Assert.NotNull(round.EndAnnouncedAt);
        // Marked opened too, so nothing later tries to open a round that is over.
        Assert.NotNull(round.StartAnnouncedAt);
    }

    [Fact]
    public async Task A_paused_round_is_not_opened_by_the_clock()
    {
        var roundId = await NewRoundAsync(
            start: DateTime.UtcNow.AddMinutes(-5), end: DateTime.UtcNow.AddHours(1));

        await using (var context = server.NewContext())
        {
            var round = await context.Series.FirstAsync(s => s.Id == roundId);
            round.PausedAt = DateTime.UtcNow.AddMinutes(-10);
            await context.SaveChangesAsync();
        }

        await server.Services.GetRequiredService<SeriesScheduler>().TickAsync(CancellationToken.None);

        await using var after = server.NewContext();
        var paused = await after.Series.FirstAsync(s => s.Id == roundId);
        // A manager stopped it. The clock does not overrule that.
        Assert.False(paused.IsOpen);
        Assert.Null(paused.StartAnnouncedAt);
    }

    [Fact]
    public async Task A_freeze_that_has_been_revealed_is_announced_once()
    {
        var roundId = await NewRoundAsync(
            start: DateTime.UtcNow.AddHours(-2), end: DateTime.UtcNow.AddHours(2));

        await using (var context = server.NewContext())
        {
            var frozen = await context.Series.FirstAsync(s => s.Id == roundId);
            frozen.RankingFreezeAt = DateTime.UtcNow.AddHours(-1);
            frozen.RankingRevealAt = DateTime.UtcNow.AddMinutes(-1);
            await context.SaveChangesAsync();
        }

        await server.Services.GetRequiredService<SeriesScheduler>().TickAsync(CancellationToken.None);

        await using var after = server.NewContext();
        var round = await after.Series.FirstAsync(s => s.Id == roundId);
        Assert.NotNull(round.UnfrozenAnnouncedAt);
    }

    // ── the file collector ──────────────────────────────────────────────────

    [Fact]
    public async Task A_young_orphan_survives_and_an_old_one_does_not()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

        var young = Guid.Parse(await Build.UploadAsync(admin, "/api/v1/files", "young.txt", "just uploaded\n"));
        var old = Guid.Parse(await Build.UploadAsync(admin, "/api/v1/files", "old.txt", "abandoned\n"));

        // The old one was uploaded two days ago and nothing ever pointed at it.
        await using (var context = server.NewContext())
        {
            var file = await context.Files.FirstAsync(f => f.Id == old);
            file.CreatedAt = DateTime.UtcNow.AddDays(-2);
            await context.SaveChangesAsync();
        }

        var collector = server.Services.GetRequiredService<FileCollector>();

        // Dry run first: it says what it would do and does none of it.
        var rehearsal = await collector.CollectAsync(dryRun: true, CancellationToken.None);
        Assert.True(rehearsal.Candidates >= 1);
        Assert.Equal(0, rehearsal.Deleted);

        await using (var context = server.NewContext())
        {
            Assert.True(await context.Files.AnyAsync(f => f.Id == old));
        }

        var report = await collector.CollectAsync(dryRun: false, CancellationToken.None);
        Assert.True(report.Deleted >= 1);
        Assert.True(report.BytesReclaimed > 0);

        await using (var context = server.NewContext())
        {
            Assert.False(await context.Files.AnyAsync(f => f.Id == old));
            // An abandoned edit must not cost storage for ever, but the window
            // has to outlast a slow upload followed by a long think.
            Assert.True(await context.Files.AnyAsync(f => f.Id == young));
        }
    }

    [Fact]
    public async Task A_referenced_file_is_never_collected_however_old_it_is()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var packageId = Guid.Parse(await Build.PackageIdOfAsync(server, slug));

        // Old enough to be swept, if anything but a reference decided.
        await using (var context = server.NewContext())
        {
            var file = await context.Files.FirstAsync(f => f.Id == packageId);
            file.CreatedAt = DateTime.UtcNow.AddYears(-1);
            await context.SaveChangesAsync();
        }

        await server.Services.GetRequiredService<FileCollector>()
            .CollectAsync(dryRun: false, CancellationToken.None);

        await using var after = server.NewContext();
        // A problem version's files are never superseded, so this is kept as
        // long as the version exists — which is what a pinned version means.
        Assert.True(await after.Files.AnyAsync(f => f.Id == packageId));
    }

    /// <summary>
    /// The rule that makes the other two reachable: a superseded reference is
    /// marked, not deleted, so its file does not become an orphan and vanish in
    /// twenty-four hours.
    /// </summary>
    [Fact]
    public async Task A_superseded_reference_still_protects_its_file()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var submitted = await Build.SubmitAsync(participant, slug, "print('logs')\n");

        var runner = await Build.RunnerAsync(server);
        var job = await runner.ClaimUntilAsync(submitted.GetProperty("id").GetString()!);

        var first = Guid.Parse(await runner.UploadAsync("runner.log", "boot one\n"));
        await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/files/attach", new { fileId = first.ToString(), name = "runner.log" });

        var second = await runner.UploadAsync("runner.log", "boot two\n");
        await runner.Client.PostAsJsonAsync(
            "/api/v1/runner/files/attach", new { fileId = second, name = "runner.log" });

        // The first upload is old, and its reference is superseded — but the
        // reference is still there, so it is not an orphan.
        await using (var context = server.NewContext())
        {
            var file = await context.Files.FirstAsync(f => f.Id == first);
            file.CreatedAt = DateTime.UtcNow.AddDays(-3);
            await context.SaveChangesAsync();
        }

        await server.Services.GetRequiredService<FileCollector>()
            .CollectAsync(dryRun: false, CancellationToken.None);

        await using var after = server.NewContext();
        Assert.True(
            await after.Files.AnyAsync(f => f.Id == first),
            "a superseded reference keeps its file out of the twenty-four-hour rule");

        await runner.ReportAsync(
            job.GetProperty("jobId").GetString()!, job.GetProperty("leaseToken").GetString()!);
    }

    // ── sessions ────────────────────────────────────────────────────────────

    /// <summary>
    /// `getUserSessions` had nothing to read until requests started recording
    /// one. The count of open sockets is not among the stored fields on purpose.
    /// </summary>
    [Fact]
    public async Task Using_the_product_records_a_session_a_manager_can_read()
    {
        var login = "has-session-" + Guid.NewGuid().ToString("N")[..6];
        var client = await Sign.NewAccountAsync(server, login);

        // Two requests that get somewhere.
        await client.GetAsync("/api/v1/account");
        await client.GetAsync("/api/v1/activities");

        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var userId = await UserIdAsync(login);

        var sessions = await admin.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/v1/users/{userId}/sessions");

        var session = Assert.Single(sessions.EnumerateArray().ToList());
        Assert.NotNull(session.GetProperty("startedAt").GetString());
        // An API path, not the screen somebody was looking at.
        Assert.StartsWith("/", session.GetProperty("lastRequestPath").GetString());
        // Nobody holds a socket, and zero is the honest answer rather than a
        // number left over from when they did.
        Assert.Equal(0, session.GetProperty("connections").GetInt32());

        await using var context = server.NewContext();
        var user = await context.Users.FirstAsync(u => u.Id == userId);
        Assert.NotNull(user.LastSeenAt);
    }

    private async Task<string> UserIdAsync(string login)
    {
        await using var context = server.NewContext();
        var user = await context.Users.FirstAsync(u => u.UserName == login);
        return user.Id;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A round in its own activity, shut and unannounced — exactly what the
    /// scheduler is supposed to find.
    /// </summary>
    private async Task<Guid> NewRoundAsync(DateTime start, DateTime end)
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = "W" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();

        await Build.PostAsync(admin, "/api/v1/activities", new
        {
            slug,
            name = "Worker test",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            joinPolicy = "open",
            languages = new[] { "python" },
        });

        var round = await Build.PostAsync(admin, $"/api/v1/activities/{slug}/series", new
        {
            slug = "r1",
            name = "R1",
            startDate = start.ToString("O"),
            endDate = end.ToString("O"),
        });

        var id = Guid.Parse(round.GetProperty("id").GetString()!);

        // Creating a round settles the flag from its dates, which is right for a
        // manager's write — but leaves nothing for the scheduler to find. This
        // puts it back to what an unannounced round looks like.
        await using var context = server.NewContext();
        var stored = await context.Series.FirstAsync(s => s.Id == id);
        stored.IsOpen = false;
        stored.StartAnnouncedAt = null;
        stored.EndAnnouncedAt = null;
        stored.WindowAnnouncedAt = null;
        stored.UnfrozenAnnouncedAt = null;
        await context.SaveChangesAsync();

        return id;
    }
}

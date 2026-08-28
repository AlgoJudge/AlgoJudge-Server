using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Services;
using AlgoJudge.Server.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// How long an address is kept, and who is told it was kept.
/// <para>
/// All three of these were defects rather than missing features. The column
/// that says when a session expires has existed since the schema was written,
/// is indexed, and carried a comment naming a reaper that did not exist —
/// nothing set it and nothing swept it. Erasure closed a deleted account's open
/// sessions and left every address behind. And the one document that answers
/// "what do you hold about me" did not mention any of it.
/// </para>
/// </summary>
[Collection("server-1")]
public class SessionRetentionTests(ServerFixture server)
{
    private static UserSession Aged(string userId, DateTime expiresAt) => new()
    {
        UserId = userId,
        StartedAt = expiresAt.AddDays(-30),
        LastRequestAt = expiresAt.AddDays(-30),
        ExpiresAt = expiresAt,
        IpAddress = IPAddress.Parse("10.0.5.17"),
        UserAgent = "Mozilla/5.0 (a browser somebody used)",
    };

    /// <summary>
    /// A real session says when it expires.
    /// <para>
    /// <b>Written because its absence was invisible.</b> Every other test here
    /// sets <c>ExpiresAt</c> by hand to make its own case, so deleting the one
    /// line that sets it in the middleware left all of them green — and the
    /// column would have gone back to being what it was for a year: declared,
    /// indexed, never written, with every address kept for ever.
    /// </para>
    /// <para>
    /// The window is pushed out on every touch rather than fixed at creation, so
    /// a session in daily use never expires and one abandoned in June is swept
    /// in July.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_session_this_Server_made_says_when_it_expires()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = (await participant.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("userId").GetString()!;

        await using var context = server.NewContext();
        var session = await context.UserSessions
            .Where(s => s.UserId == who)
            .OrderByDescending(s => s.StartedAt)
            .FirstAsync();

        Assert.NotNull(session.ExpiresAt);

        // Thirty days, from the last request rather than from the first. A day
        // of slack either side, because the assertion is about the window and
        // not about a clock.
        var window = session.ExpiresAt!.Value - (session.LastRequestAt ?? session.StartedAt);
        Assert.InRange(window, TimeSpan.FromDays(29), TimeSpan.FromDays(31));
    }

    /// <summary>
    /// A browser that names itself is recorded as having done so.
    /// <para>
    /// Driven through a real request rather than by building a row, because the
    /// header has to survive the whole way: the Client sets it, CORS allows it —
    /// `AllowAnyHeader` already — and `IRequestOrigin` parses it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_session_records_the_device_the_browser_named()
    {
        // **Signed in by hand, with the header already set.** The helpers build
        // the client themselves, and a session is touched at most once a minute
        // — so a header added after signing in reaches a session that was minted
        // seconds ago and throttled away. A browser sends this from its first
        // request, and so does this test.
        var login = "device-" + Guid.NewGuid().ToString("N")[..10];
        await Sign.NewAccountAsync(server, login);

        var device = Guid.NewGuid();
        var browser = server.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                HandleCookies = true,
            });
        browser.DefaultRequestHeaders.Add("Device-Id", device.ToString());

        var signedIn = await browser.PostAsJsonAsync(
            "/api/v1/identity/login?useSessionCookies=true",
            new { email = login, password = Sign.Password });
        await Sign.Succeeded(signedIn);

        var who = (await browser.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("userId").GetString()!;

        await using var context = server.NewContext();
        var sessions = await context.UserSessions
            .Where(s => s.UserId == who)
            .ToListAsync();

        Assert.Contains(sessions, s => s.DeviceId == device);
    }

    /// <summary>
    /// Past the window the address goes and the row stays.
    /// <para>
    /// <b>The row is the point.</b> Deleting it would take "when did this person
    /// sign in, and how often" with it, which is a fair question to ask of an
    /// account under dispute. What goes is what describes a person rather than
    /// an event.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_session_past_its_window_keeps_its_row_and_loses_its_address()
    {
        var sweeper = server.Services.GetRequiredService<AddressSweeper>();

        Guid staleId, freshId;
        await using (var context = server.NewContext())
        {
            var user = await context.Users.FirstAsync();
            var stale = Aged(user.Id, DateTime.UtcNow.AddDays(-1));
            var fresh = Aged(user.Id, DateTime.UtcNow.AddDays(+1));
            context.UserSessions.AddRange(stale, fresh);
            await context.SaveChangesAsync();
            (staleId, freshId) = (stale.Id, fresh.Id);
        }

        await sweeper.SweepSessionsAsync(CancellationToken.None);

        await using (var context = server.NewContext())
        {
            var swept = await context.UserSessions.FirstAsync(s => s.Id == staleId);
            Assert.Null(swept.IpAddress);
            Assert.Null(swept.UserAgent);
            Assert.NotNull(swept.EndedAt);
            // Still there, and still says when somebody was here.
            Assert.NotEqual(default, swept.StartedAt);

            // One inside its window is untouched: a sweep that took everything
            // would pass this test's first half and be useless.
            var kept = await context.UserSessions.FirstAsync(s => s.Id == freshId);
            Assert.NotNull(kept.IpAddress);
            Assert.NotNull(kept.UserAgent);
            Assert.Null(kept.EndedAt);
        }
    }

    /// <summary>
    /// A second pass does not read the same rows again.
    /// <para>
    /// The sweep selects on the fields still being there rather than on the
    /// expiry alone. Without that, every session ever created would be loaded
    /// on every pass for the rest of the installation's life, to change nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_session_already_swept_is_not_swept_again()
    {
        var sweeper = server.Services.GetRequiredService<AddressSweeper>();

        await using (var context = server.NewContext())
        {
            var user = await context.Users.FirstAsync();
            context.UserSessions.Add(Aged(user.Id, DateTime.UtcNow.AddDays(-1)));
            await context.SaveChangesAsync();
        }

        var first = await sweeper.SweepSessionsAsync(CancellationToken.None);
        var second = await sweeper.SweepSessionsAsync(CancellationToken.None);

        Assert.True(first >= 1);
        Assert.Equal(0, second);
    }

    /// <summary>
    /// Erasing an account takes the addresses with it — <b>every session, not
    /// only the open ones</b>.
    /// <para>
    /// It closed what was open and left the addresses on every row that person
    /// had ever made. `Submission.UserId` documents deletion as anonymisation;
    /// an anonymisation that leaves personal data behind is not one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Erasing_an_account_takes_the_addresses_of_every_session_it_had()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = (await participant.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("userId").GetString()!;

        await using (var context = server.NewContext())
        {
            var open = Aged(who, DateTime.UtcNow.AddDays(+1));
            var closed = Aged(who, DateTime.UtcNow.AddDays(+1));
            closed.EndedAt = DateTime.UtcNow.AddDays(-2);
            context.UserSessions.AddRange(open, closed);
            await context.SaveChangesAsync();
        }

        using var scope = server.Services.CreateScope();
        var deletion = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();
        await using (var context = scope.ServiceProvider
            .GetRequiredService<Database.ApplicationDbContext>())
        {
            var user = await context.Users.FirstAsync(u => u.Id == who);
            await deletion.AnonymiseAsync(user, CancellationToken.None);
            await context.SaveChangesAsync();
        }

        await using (var context = server.NewContext())
        {
            var theirs = await context.UserSessions.Where(s => s.UserId == who).ToListAsync();
            Assert.NotEmpty(theirs);
            Assert.All(theirs, s =>
            {
                Assert.Null(s.IpAddress);
                Assert.Null(s.UserAgent);
                Assert.NotNull(s.EndedAt);
            });
        }
    }

    /// <summary>
    /// And the person can read what is held about them.
    /// <para>
    /// The Server has collected addresses since the schema was written and the
    /// export has never mentioned them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_export_says_where_this_person_connected_from()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        var who = (await participant.GetFromJsonAsync<JsonElement>("/api/v1/account"))
            .GetProperty("userId").GetString()!;

        await using (var context = server.NewContext())
        {
            context.UserSessions.Add(Aged(who, DateTime.UtcNow.AddDays(+1)));
            await context.SaveChangesAsync();
        }

        var answer = await participant.GetAsync("/api/v1/account/export");
        await Sign.Succeeded(answer);

        using var exported = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        var sessions = exported.RootElement.GetProperty("sessions").EnumerateArray().ToList();

        // Null-tolerant, because the person's own live session is in here too
        // and `HttpClient` sends no user agent — a real absence rather than a
        // missing member, which is the shape the export should have anyway.
        Assert.Contains(sessions, s => s.GetProperty("ipAddress").GetString() == "10.0.5.17");
        Assert.Contains(
            sessions,
            s => s.GetProperty("userAgent").GetString()?.Contains("somebody used") == true);
    }
}

using AlgoJudge.Server.Api.Contracts;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using AlgoJudge.Server.Workers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Who hears an event, and the rule it is decided by.
/// <para>
/// The socket must never be a second path to data a fetch would refuse, and it
/// must not be a narrower one either — a person who may read something and is
/// never told it has changed sees a stale screen and no reason for it. Both
/// halves were wrong: <c>EventAudience</c> honoured <c>system:administrator</c>
/// in an activity grant, which <c>PermissionService</c> deliberately does not,
/// and <c>SeriesScheduler</c> kept a third copy of the rule that read activity
/// grants only.
/// </para>
/// </summary>
[Collection("server-2")]
public class EventAudienceTests(ServerFixture server)
{
    /// <summary>
    /// The reported defect. An activity grant carrying the administrator string
    /// made its holder a recipient of every installation-wide event — the
    /// Runners, the problem library, everybody's grants — while
    /// <c>PermissionService.IsAdministratorAsync</c> correctly refused the same
    /// person the same data over REST.
    /// </summary>
    [Fact]
    public async Task An_activity_grant_carrying_the_administrator_string_is_not_an_administrator_anywhere()
    {
        var (userId, activityId) = await PersonInAnActivityAsync("""["system:administrator"]""", null);

        using var scope = server.Services.CreateScope();
        var audience = scope.ServiceProvider.GetRequiredService<IEventAudience>();

        var anywhere = await audience.AnywhereAsync(Permissions.RunnerRead, default);
        Assert.DoesNotContain(userId, anywhere);

        // And it is not a blanket refusal: within the activity the grant names,
        // the string still says nothing, because it is not a permission the
        // activity scope can grant on its own.
        var inActivity = await audience.InActivityAsync(activityId, Permissions.RunnerRead, default);
        Assert.DoesNotContain(userId, inActivity);
    }

    /// <summary>
    /// The same string in a <b>system</b> grant is the bypass, and must stay one
    /// — the fix is a scope check, not a removal.
    /// </summary>
    [Fact]
    public async Task A_system_grant_carrying_the_administrator_string_still_hears_everything()
    {
        var (userId, _) = await PersonInAnActivityAsync(null, """["system:administrator"]""");

        using var scope = server.Services.CreateScope();
        var audience = scope.ServiceProvider.GetRequiredService<IEventAudience>();

        Assert.Contains(userId, await audience.AnywhereAsync(Permissions.RunnerRead, default));
    }

    /// <summary>
    /// What only deleting <c>SeriesScheduler.MembersAsync</c> fixes: it read
    /// <c>ActivityId == activityId</c> and nothing else, so somebody holding a
    /// system grant — a manager of the installation rather than of one course —
    /// was never told a round had opened.
    /// </summary>
    [Fact]
    public async Task A_system_grant_holder_hears_a_round_open()
    {
        var login = "sys-" + Guid.NewGuid().ToString("N")[..10];
        await Sign.NewAccountAsync(server, login);

        string userId;
        await using (var context = server.NewContext())
        {
            userId = (await context.Users.FirstAsync(u => u.UserName == login)).Id;
            context.Grants.Add(new Grant
            {
                UserId = userId,
                ActivityId = null,
                Permissions = $"""["{Permissions.ActivityRead}"]""",
                State = GrantState.Active,
            });
            await context.SaveChangesAsync();
        }

        var hub = new CountingEventHub();
        using var host = server.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IEventHub>(hub)));

        var roundId = await NewRoundAsync(host, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(1));

        await host.Services.GetRequiredService<SeriesScheduler>().TickAsync(CancellationToken.None);

        var told = hub.Addressed
            .Where(a => a.Type == EventTypes.SeriesChanged)
            .SelectMany(a => a.To)
            .ToList();

        Assert.Contains(userId, told);
        Assert.NotEqual(Guid.Empty, roundId);
    }

    /// <summary>
    /// A person and an activity, with whichever grants the test wants.
    /// Scoped to what it creates: the database is shared.
    /// </summary>
    private async Task<(string UserId, Guid ActivityId)> PersonInAnActivityAsync(
        string? inActivity, string? system)
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var login = "aud-" + Guid.NewGuid().ToString("N")[..10];
        await Sign.NewAccountAsync(server, login);

        await using var context = server.NewContext();
        var activityId = (await context.Activities.FirstAsync(a => a.Slug == slug)).Id;
        var userId = (await context.Users.FirstAsync(u => u.UserName == login)).Id;

        if (inActivity is not null)
        {
            context.Grants.Add(new Grant
            {
                UserId = userId,
                ActivityId = activityId,
                Permissions = inActivity,
                State = GrantState.Active,
            });
        }

        if (system is not null)
        {
            context.Grants.Add(new Grant
            {
                UserId = userId,
                ActivityId = null,
                Permissions = system,
                State = GrantState.Active,
            });
        }

        await context.SaveChangesAsync();
        return (userId, activityId);
    }

    /// <summary>
    /// A round the scheduler will find, in its own activity. The same undo as
    /// <c>WorkerTests</c>: creating a round settles the flags from its dates,
    /// which leaves the scheduler nothing to announce.
    /// </summary>
    private async Task<Guid> NewRoundAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
        DateTime start,
        DateTime end)
    {
        var admin = await Sign.InAsync(host, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = "A" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();

        await Build.PostAsync(admin, "/api/v1/activities", new
        {
            slug,
            name = "Audience test",
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

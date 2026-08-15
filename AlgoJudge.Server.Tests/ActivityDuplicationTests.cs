using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Copying an activity for a new run of it.
///
/// <para>
/// <b>A copy is a shape, not a history.</b> It carries the rounds, the problems
/// assigned to them and the settings somebody chose; it carries nobody's
/// submissions, nobody's grants and no record of having ever opened. The dates
/// move so that the copy happens when the person copying said it does.
/// </para>
/// </summary>
[Collection("server")]
public class ActivityDuplicationTests(ServerFixture server)
{
    [Fact]
    public async Task A_copy_moves_every_date_by_the_same_amount()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, _) = await Build.ActivityAsync(server);
        var source = await DatedAsync(slug);

        var startsAt = source.Start!.Value.AddDays(371);
        var copy = await DuplicateAsync(manager, source.ActivityId, startsAt);

        await using var context = server.NewContext();
        var rounds = await context.Series.AsNoTracking()
            .Where(s => s.ActivityId == copy)
            .OrderBy(s => s.Order)
            .ToListAsync();

        var moved = await context.Series.AsNoTracking()
            .Where(s => s.ActivityId == source.ActivityId)
            .OrderBy(s => s.Order)
            .ToListAsync();

        Assert.Equal(moved.Count, rounds.Count);

        var delta = TimeSpan.FromDays(371);

        // **Every date on the entity, found by reflection rather than listed.**
        // A round has seven of them and the list grows; a test naming them one by
        // one passes for ever after somebody adds the eighth and forgets to shift
        // it, and the failure shows up as a copy that reveals a ranking early.
        var dated = typeof(Series).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(DateTime?))
            .Where(p => !Announcements.Contains(p.Name) && p.Name != nameof(Series.PausedAt))
            .ToList();

        Assert.True(dated.Count >= 6, $"expected the dated fields to be found, saw {dated.Count}");

        foreach (var (before, after) in moved.Zip(rounds))
        {
            foreach (var property in dated)
            {
                var was = (DateTime?)property.GetValue(before);
                var now = (DateTime?)property.GetValue(after);

                if (was is null)
                {
                    Assert.True(now is null, $"{property.Name} was null and the copy has one");
                    continue;
                }

                Assert.True(now is not null, $"{property.Name} was set and the copy has none");
                Assert.Equal(was.Value + delta, now!.Value, TimeSpan.FromMinutes(1));
            }
        }
    }

    /// <summary>
    /// <b>The copy has never done anything.</b> Carrying an announcement marker
    /// over would make the scheduler treat the copy as already announced, and
    /// stay silent about a round nobody was ever told about.
    /// </summary>
    [Fact]
    public async Task A_copy_has_never_opened_and_never_announced()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, _) = await Build.ActivityAsync(server);
        var source = await DatedAsync(slug, alreadyRun: true);

        var copy = await DuplicateAsync(manager, source.ActivityId, source.Start!.Value.AddDays(371));

        await using var context = server.NewContext();
        var rounds = await context.Series.AsNoTracking().Where(s => s.ActivityId == copy).ToListAsync();

        foreach (var round in rounds)
        {
            Assert.False(round.IsOpen, "a copy starts closed");
            Assert.Null(round.PausedAt);
            foreach (var name in Announcements)
            {
                var marker = typeof(Series).GetProperty(name)!.GetValue(round);
                Assert.True(marker is null, $"{name} travelled into the copy");
            }
        }
    }

    /// <summary>
    /// <b>The reason the column exists.</b> A copy of last year carries last
    /// year's dates for as long as it takes somebody to shift them, and the very
    /// next sweep would open every round it has and announce them to a course
    /// full of people.
    /// </summary>
    [Fact]
    public async Task The_scheduler_leaves_an_unpublished_copy_alone()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, _) = await Build.ActivityAsync(server);
        var source = await DatedAsync(slug);

        // **Dated so that its first round is open right now**, which is the
        // dangerous case and the only one that exercises the opening sweep: a
        // copy whose window has already closed is skipped for being over rather
        // than for being unpublished, and would prove nothing.
        var copy = await DuplicateAsync(manager, source.ActivityId, DateTime.UtcNow.AddHours(-1));

        var scheduler = server.Services.GetRequiredService<SeriesScheduler>();
        await scheduler.TickAsync(CancellationToken.None);

        await using (var context = server.NewContext())
        {
            var rounds = await context.Series.AsNoTracking()
                .Where(s => s.ActivityId == copy).ToListAsync();
            Assert.All(rounds, round =>
            {
                Assert.False(round.IsOpen, "an unpublished copy was opened by the scheduler");
                Assert.Null(round.StartAnnouncedAt);
            });
        }

        // And publishing is what changes that, so the filter is a gate rather
        // than a way of never opening anything.
        (await manager.PostAsJsonAsync(
            $"/api/v1/activities/{copy}/published", new { published = true }))
            .EnsureSuccessStatusCode();

        await scheduler.TickAsync(CancellationToken.None);

        await using (var context = server.NewContext())
        {
            var rounds = await context.Series.AsNoTracking()
                .Where(s => s.ActivityId == copy).ToListAsync();

            Assert.Contains(rounds, round => round.IsOpen);
        }
    }

    [Fact]
    public async Task A_copy_carries_the_problems_and_none_of_the_password()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, _) = await Build.ActivityAsync(server);
        var source = await DatedAsync(slug);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Id == source.ActivityId);
            activity.JoinPassword = "last-years-password";
            await context.SaveChangesAsync();
        }

        var copy = await DuplicateAsync(manager, source.ActivityId, source.Start!.Value.AddDays(371));

        await using var check = server.NewContext();
        var copied = await check.Activities.AsNoTracking().FirstAsync(a => a.Id == copy);

        // **Known by everybody who took the original.** A new cohort joinable by
        // the previous one is not a copy of the settings, it is a leak.
        Assert.Null(copied.JoinPassword);
        Assert.Null(copied.PublishedAt);

        var assignments = await check.SeriesProblems.AsNoTracking()
            .Where(sp => sp.ActivityId == copy).CountAsync();
        var original = await check.SeriesProblems.AsNoTracking()
            .Where(sp => sp.ActivityId == source.ActivityId).CountAsync();

        Assert.Equal(original, assignments);
        Assert.True(original > 0, "the fixture activity has no problems, so this proves nothing");

        // Nothing that happened travels: no submissions, and nobody's rights.
        Assert.Empty(await check.Submissions.AsNoTracking()
            .Where(s => s.SeriesProblem!.ActivityId == copy).ToListAsync());
        Assert.Empty(await check.Grants.AsNoTracking()
            .Where(g => g.ActivityId == copy).ToListAsync());
    }

    [Fact]
    public async Task A_slug_somebody_else_holds_is_refused()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, _) = await Build.ActivityAsync(server);
        var source = await DatedAsync(slug);

        var refused = await manager.PostAsJsonAsync(
            $"/api/v1/activities/{source.ActivityId}/duplicate",
            new { slug, startsAt = DateTime.UtcNow });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Copying_needs_the_rights_over_what_is_being_copied()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, _) = await Build.ActivityAsync(server);
        var source = await DatedAsync(slug);

        // **Somebody who may make activities**, which is the interesting case:
        // being allowed to create one is not being allowed to take a copy of
        // somebody else's. A participant would be refused one step earlier and
        // would prove only that the endpoint is behind something.
        var (author, client) = await AuthorAsync();

        var refused = await client.PostAsJsonAsync(
            $"/api/v1/activities/{source.ActivityId}/duplicate",
            new { slug = "copy-" + Guid.NewGuid().ToString("N")[..8], startsAt = DateTime.UtcNow });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // And the same person may copy one they do hold rights over, so the
        // refusal above is about this activity rather than about them.
        await MayEditAsync(author, source.ActivityId);
        var allowed = await client.PostAsJsonAsync(
            $"/api/v1/activities/{source.ActivityId}/duplicate",
            new { slug = "copy-" + Guid.NewGuid().ToString("N")[..8], startsAt = DateTime.UtcNow });

        Assert.True(allowed.IsSuccessStatusCode,
            $"{(int)allowed.StatusCode}: {await allowed.Content.ReadAsStringAsync()}");
        _ = manager;
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private static readonly string[] Announcements =
    [
        nameof(Series.StartAnnouncedAt), nameof(Series.EndAnnouncedAt),
        nameof(Series.WindowAnnouncedAt), nameof(Series.UnfrozenAnnouncedAt),
    ];

    /// <summary>
    /// An account holding <c>activity:create</c> at system scope and nothing
    /// else, signed in.
    /// </summary>
    private async Task<(string UserId, HttpClient Client)> AuthorAsync()
    {
        var login = "author-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);

        string userId;
        await using (var context = server.NewContext())
        {
            userId = (await context.Users.FirstAsync(u => u.UserName == login)).Id;
            context.Grants.Add(new Grant
            {
                UserId = userId,
                IsSystem = true,
                Permissions = """["activity:create"]""",
                State = GrantState.Active,
                CreatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        return (userId, client);
    }

    private async Task MayEditAsync(string userId, Guid activityId)
    {
        await using var context = server.NewContext();
        context.Grants.Add(new Grant
        {
            UserId = userId,
            ActivityId = activityId,
            Permissions = """["activity:update"]""",
            State = GrantState.Active,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private record Source(Guid ActivityId, DateTime? Start);

    /// <summary>
    /// The fixture activity with every dated field filled in, because a copy that
    /// only moves two of them passes a test that only sets two.
    /// </summary>
    private async Task<Source> DatedAsync(string slug, bool alreadyRun = false)
    {
        // **At least two rounds, spaced apart.** With one round the earliest
        // start and the latest are the same date, so a shift anchored on the
        // wrong end of the activity is indistinguishable from a correct one.
        // Written in its own context and by id: attaching one to a graph that is
        // already tracked makes EF check a row version that is not there yet.
        await using (var extra = server.NewContext())
        {
            var found = await extra.Activities.AsNoTracking()
                .Include(a => a.Series)
                .FirstAsync(a => a.Slug == slug);

            if (found.Series.Count < 2)
            {
                extra.Series.Add(new Series
                {
                    ActivityId = found.Id,
                    Slug = "second-" + Guid.NewGuid().ToString("N")[..8],
                    Name = "The round after the first",
                    Order = found.Series.Count + 1,
                });
                await extra.SaveChangesAsync();
            }
        }

        await using var context = server.NewContext();
        var activity = await context.Activities
            .Include(a => a.Series)
            .FirstAsync(a => a.Slug == slug);

        var start = DateTime.UtcNow.AddDays(-30);
        activity.StartDate = start;
        activity.EndDate = start.AddDays(20);

        foreach (var (round, index) in activity.Series.OrderBy(s => s.Order).Select((s, i) => (s, i)))
        {
            round.StartDate = start.AddDays(index);
            round.EndDate = start.AddDays(index).AddHours(4);
            round.RankingFreezeAt = start.AddDays(index).AddHours(3);
            round.RankingRevealAt = start.AddDays(index).AddHours(5);
            round.RankingVisibleFrom = start.AddDays(index);
            round.RankingVisibleTo = start.AddDays(index).AddDays(30);

            if (alreadyRun)
            {
                round.IsOpen = true;
                round.PausedAt = start.AddDays(index).AddHours(1);
                round.StartAnnouncedAt = start.AddDays(index);
                round.EndAnnouncedAt = start.AddDays(index).AddHours(4);
                round.WindowAnnouncedAt = start.AddDays(index);
                round.UnfrozenAnnouncedAt = start.AddDays(index).AddHours(5);
            }
        }

        // Published, like everything that already exists.
        activity.PublishedAt ??= DateTime.UtcNow.AddDays(-40);
        await context.SaveChangesAsync();

        return new Source(activity.Id, activity.Series.Min(s => s.StartDate));
    }

    private static async Task<Guid> DuplicateAsync(
        HttpClient manager, Guid id, DateTime startsAt)
    {
        var response = await manager.PostAsJsonAsync(
            $"/api/v1/activities/{id}/duplicate",
            new { slug = "copy-" + Guid.NewGuid().ToString("N")[..10], startsAt });

        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }
}

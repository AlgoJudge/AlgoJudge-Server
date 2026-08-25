using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Copying one round, with the problems assigned to it, into this activity or
/// another.
///
/// <para>
/// <b>The same rule as an activity copy, one level down</b>: the shape travels
/// and the history does not. What is new here is the second activity — a round
/// leaving the one it was written in — and the assignment slug, which is unique
/// across an <i>activity</i> and therefore collides on every problem when a
/// round is copied in place.
/// </para>
/// </summary>
[Collection("server")]
public class SeriesDuplicationTests(ServerFixture server)
{
    [Fact]
    public async Task A_copy_carries_every_field_that_is_meant_to_travel()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (fromSlug, roundId) = await Build.ActivityAsync(server);
        var (toSlug, _) = await Build.ActivityAsync(server);

        var fromId = await ActivityIdAsync(fromSlug);
        var toId = await ActivityIdAsync(toSlug);
        await Build.DistinctiveAsync(server, fromId);
        await DatedAsync(Guid.Parse(roundId));

        // **The target's own assignment is moved out of the way**, so this test
        // is about the fields travelling. Every fixture activity attaches its
        // problem as `A`, and the freeing of a taken slug has a test of its own.
        await using (var renaming = server.NewContext())
        {
            await renaming.SeriesProblems.Where(sp => sp.ActivityId == toId)
                .ExecuteUpdateAsync(u => u.SetProperty(sp => sp.Slug, "Z"));
        }

        var copyId = await DuplicateAsync(manager, roundId, toId, DateTime.UtcNow.AddDays(371));

        await using var context = server.NewContext();
        var before = await Loaded(context, Guid.Parse(roundId));
        var after = await Loaded(context, copyId);

        CopiedFields.AssertCarried(before, after);

        // What is deliberately not the source's: it lives somewhere else now,
        // under a name and at a place the caller chose.
        Assert.Equal(toId, after.ActivityId);
        Assert.NotEqual(before.Slug, after.Slug);

        var mine = before.SeriesProblems.OrderBy(x => x.Order).ToList();
        var theirs = after.SeriesProblems.OrderBy(x => x.Order).ToList();
        Assert.NotEmpty(mine);
        Assert.Equal(mine.Count, theirs.Count);

        foreach (var (x, y) in mine.Zip(theirs))
        {
            CopiedFields.AssertCarried(x, y);
            // Nothing in the target held this slug, so it is not suffixed.
            Assert.Equal(x.Slug, y.Slug);
            Assert.Equal(toId, y.ActivityId);
        }
    }

    /// <summary>
    /// <b>The room travels.</b> Next year may be a different room, and the copy
    /// is still made restricted: a manager who has to remove a rule knows the
    /// original was closed, where one handed an open copy of a closed round is
    /// told nothing at all. A dropped restriction fails open.
    /// </summary>
    [Fact]
    public async Task The_address_rules_travel_with_the_round()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (fromSlug, roundId) = await Build.ActivityAsync(server);
        var (toSlug, _) = await Build.ActivityAsync(server);

        await Build.DistinctiveAsync(server, await ActivityIdAsync(fromSlug));
        await DatedAsync(Guid.Parse(roundId));

        var copyId = await DuplicateAsync(
            manager, roundId, await ActivityIdAsync(toSlug), DateTime.UtcNow.AddDays(371));

        await using var context = server.NewContext();
        var rules = await context.SeriesAddressRules.AsNoTracking()
            .Where(r => r.SeriesId == copyId)
            .Select(r => r.Network.ToString())
            .ToListAsync();

        Assert.Equal(["192.168.7.0/24"], rules);
    }

    /// <summary>
    /// <b>An assignment slug is unique across the activity, not the round.</b>
    /// So a round copied into its own activity collides on every problem it
    /// holds, and freeing them is what makes copying in place work at all.
    /// </summary>
    [Fact]
    public async Task A_copy_in_place_frees_the_assignment_slugs()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, roundId) = await Build.ActivityAsync(server);
        var activityId = await ActivityIdAsync(slug);

        var copyId = await DuplicateAsync(manager, roundId, null, DateTime.UtcNow.AddDays(7));

        await using var context = server.NewContext();
        var source = await context.SeriesProblems.AsNoTracking()
            .Where(sp => sp.SeriesId == Guid.Parse(roundId))
            .Select(sp => sp.Slug).ToListAsync();
        var copied = await context.SeriesProblems.AsNoTracking()
            .Where(sp => sp.SeriesId == copyId)
            .Select(sp => sp.Slug).ToListAsync();

        Assert.NotEmpty(source);
        Assert.Equal(source.Count, copied.Count);
        Assert.Empty(source.Intersect(copied));

        // And the whole activity still holds one row per slug, which is what the
        // unique index says and what the suffixing exists to keep true.
        var all = await context.SeriesProblems.AsNoTracking()
            .Where(sp => sp.ActivityId == activityId)
            .Select(sp => sp.Slug).ToListAsync();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    /// <summary>
    /// Every dated field moves by the same amount, found by reflection rather
    /// than listed — a round has seven of them and the list grows.
    /// </summary>
    [Fact]
    public async Task Every_date_moves_by_the_same_amount()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (_, roundId) = await Build.ActivityAsync(server);
        var (toSlug, _) = await Build.ActivityAsync(server);
        await DatedAsync(Guid.Parse(roundId));

        await using var reading = server.NewContext();
        var source = await reading.Series.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(roundId));
        var startsAt = source.StartDate!.Value.AddDays(371);

        var copyId = await DuplicateAsync(
            manager, roundId, await ActivityIdAsync(toSlug), startsAt);

        await using var context = server.NewContext();
        var copy = await context.Series.AsNoTracking().FirstAsync(s => s.Id == copyId);

        Assert.Equal(startsAt, copy.StartDate!.Value, TimeSpan.FromSeconds(1));

        // The activity is in Europe/Warsaw and the shift crosses a daylight-saving
        // boundary, so an offset measured in absolute time would move the end by
        // an hour against the start. Every date is asked, not the two the author
        // of this test happened to think of.
        var dated = CopiedFields.PropertiesOf(typeof(Series))
            .Where(p => p.PropertyType == typeof(DateTime?))
            .Where(p => CopiedFields.Checked[typeof(Series)].Contains(p.Name))
            .ToList();
        Assert.True(dated.Count >= 6, $"expected the dated fields to be found, saw {dated.Count}");

        var delta = copy.StartDate!.Value - source.StartDate!.Value;

        foreach (var property in dated)
        {
            var was = (DateTime?)property.GetValue(source);
            var now = (DateTime?)property.GetValue(copy);

            if (was is null)
            {
                Assert.True(now is null, $"{property.Name} was null and the copy has one");
                continue;
            }

            Assert.True(now is not null, $"{property.Name} was set and the copy has none");
            Assert.Equal(was.Value + delta, now!.Value, TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public async Task A_copy_has_never_opened_and_holds_nobody_s_work()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (_, roundId) = await Build.ActivityAsync(server);
        var (toSlug, _) = await Build.ActivityAsync(server);

        var copyId = await DuplicateAsync(
            manager, roundId, await ActivityIdAsync(toSlug), DateTime.UtcNow.AddDays(2));

        await using var context = server.NewContext();
        var copy = await context.Series.AsNoTracking().FirstAsync(s => s.Id == copyId);

        // The source is open and announced — `Build.ActivityAsync` opens it the
        // way the scheduler would — so this is a difference rather than two
        // defaults agreeing.
        var source = await context.Series.AsNoTracking().FirstAsync(s => s.Id == Guid.Parse(roundId));
        Assert.True(source.IsOpen, "the fixture round is shut, so this proves nothing");

        Assert.False(copy.IsOpen);
        Assert.Null(copy.PausedAt);
        foreach (var name in CopiedFields.Reset[typeof(Series)].Where(n => n.EndsWith("AnnouncedAt")))
        {
            Assert.Null(typeof(Series).GetProperty(name)!.GetValue(copy));
        }

        Assert.Empty(await context.Submissions.AsNoTracking()
            .Where(s => s.SeriesProblem!.SeriesId == copyId).ToListAsync());
    }

    [Fact]
    public async Task A_slug_already_used_in_the_target_is_refused()
    {
        var manager = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (_, roundId) = await Build.ActivityAsync(server);
        var (toSlug, targetRound) = await Build.ActivityAsync(server);

        await using var context = server.NewContext();
        var taken = (await context.Series.AsNoTracking()
            .FirstAsync(s => s.Id == Guid.Parse(targetRound))).Slug;

        var refused = await manager.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/duplicate",
            new { targetActivityId = await ActivityIdAsync(toSlug), slug = taken, startsAt = DateTime.UtcNow });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// <b>Rights over both ends, and they are different questions.</b> Without
    /// the second, copying would be a way of setting work in a course somebody
    /// else runs.
    /// </summary>
    [Fact]
    public async Task Copying_needs_the_rights_over_the_activity_it_goes_into()
    {
        var (fromSlug, roundId) = await Build.ActivityAsync(server);
        var (toSlug, _) = await Build.ActivityAsync(server);
        var fromId = await ActivityIdAsync(fromSlug);
        var toId = await ActivityIdAsync(toSlug);

        var (author, client) = await AuthorAsync();
        await MayRunAsync(author, fromId);

        var refused = await client.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/duplicate",
            new { targetActivityId = toId, slug = Fresh(), startsAt = DateTime.UtcNow });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // And the same person may copy into an activity they do run, so the
        // refusal is about that activity rather than about them.
        await MayRunAsync(author, toId);
        var allowed = await client.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/duplicate",
            new { targetActivityId = toId, slug = Fresh(), startsAt = DateTime.UtcNow });

        Assert.True(allowed.IsSuccessStatusCode,
            $"{(int)allowed.StatusCode}: {await allowed.Content.ReadAsStringAsync()}");
    }

    // ── Getting there ────────────────────────────────────────────────────────

    private static string Fresh() => "copy-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<Guid> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id;
    }

    private static Task<Series> Loaded(ApplicationDbContext context, Guid id) =>
        context.Series.AsNoTracking()
            .Include(s => s.SeriesProblems)
            .Include(s => s.AddressRules)
            .FirstAsync(s => s.Id == id);

    /// <summary>
    /// Every dated field of one round filled in, because a copy that moves two
    /// of them passes a test that only sets two.
    /// </summary>
    private async Task DatedAsync(Guid roundId)
    {
        await using var context = server.NewContext();
        var start = DateTime.UtcNow.AddDays(-30);

        // `ExecuteUpdateAsync` rather than tracking: `Series` carries `xmin`.
        await context.Series.Where(s => s.Id == roundId).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.StartDate, (DateTime?)start)
            .SetProperty(s => s.EndDate, (DateTime?)start.AddHours(4))
            .SetProperty(s => s.RankingFreezeAt, (DateTime?)start.AddHours(3))
            .SetProperty(s => s.RankingRevealAt, (DateTime?)start.AddHours(5))
            .SetProperty(s => s.RankingVisibleFrom, (DateTime?)start)
            .SetProperty(s => s.RankingVisibleTo, (DateTime?)start.AddDays(30)));
    }

    private static async Task<Guid> DuplicateAsync(
        HttpClient manager, string roundId, Guid? targetActivityId, DateTime startsAt)
    {
        var response = await manager.PostAsJsonAsync(
            $"/api/v1/series/{roundId}/duplicate",
            new { targetActivityId, slug = Fresh(), startsAt });

        Assert.True(response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    /// <summary>An account holding nothing at system scope, signed in.</summary>
    private async Task<(string UserId, HttpClient Client)> AuthorAsync()
    {
        var login = "author-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);

        await using var context = server.NewContext();
        var userId = (await context.Users.FirstAsync(u => u.UserName == login)).Id;
        return (userId, client);
    }

    /// <summary>What running one activity means: editing it, and attaching to it.</summary>
    private async Task MayRunAsync(string userId, Guid activityId)
    {
        await using var context = server.NewContext();
        context.Grants.Add(new Grant
        {
            UserId = userId,
            ActivityId = activityId,
            Permissions = """["activity:update","problem:attach"]""",
            State = GrantState.Active,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }
}

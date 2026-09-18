using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Whether a search box finds what it offers to find.
/// <para>
/// Four of them promised more than the Server searched, found by the filter
/// audit of 2026-09-14 and left open then. A search that narrows to nothing is
/// the cruellest kind of wrong answer: the list empties, which reads as <i>this
/// person has asked nothing</i> or <i>this machine is gone</i> rather than as
/// <i>the box does not look there</i>.
/// </para>
/// <para>
/// <b>What is searched is what the row draws.</b> That is the rule these hold
/// to, and it is why the display name is written out column by column in every
/// predicate: <c>Projections.DisplayName</c> is a method over a loaded row and
/// does not translate, so the query has to spell out what it produces.
/// </para>
/// </summary>
[Collection("server-2")]
public class SearchTests(ServerFixture server)
{
    private static Task<HttpClient> AdminAsync(ServerFixture server) =>
        Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    /// <summary>An account with a first and last name, which is what a screen draws.</summary>
    private async Task<(string Id, string First, string Last)> NamedAsync(string first, string last)
    {
        var login = $"s{Guid.NewGuid():N}"[..12].ToLowerInvariant();

        using var scope = server.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var person = new User
        {
            UserName = login,
            Email = $"{login}@example.invalid",
            EmailConfirmed = true,
            ApprovedAt = DateTime.UtcNow,
            FirstName = first,
            LastName = last,
        };
        Assert.True((await users.CreateAsync(person, Sign.Password)).Succeeded);

        return (person.Id, first, last);
    }

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient client, string path) =>
        (await Build.GetAsync(client, path)).GetProperty("items").EnumerateArray().ToList();

    // ── people ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The name as it is written on the row, not one column of it.</b>
    /// <para>
    /// Every arm tested its own column, so a needle spanning the space between
    /// the two names matched neither and the table emptied — on exactly the
    /// thing a manager copies out of a class list.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_person_is_found_by_their_whole_name()
    {
        var (id, first, last) = await NamedAsync("Anna", $"Kowalska{Guid.NewGuid():N}"[..16]);
        var admin = await AdminAsync(server);

        var whole = await ItemsAsync(admin, $"/api/v1/users/managed?pageSize=50&search={first} {last}");
        Assert.Contains(whole, row => row.GetProperty("id").GetString() == id);

        // And each half on its own still works, which is what it did before.
        var half = await ItemsAsync(admin, $"/api/v1/users/managed?pageSize=50&search={last}");
        Assert.Contains(half, row => row.GetProperty("id").GetString() == id);
    }

    /// <summary>
    /// <b>A lockout that has elapsed is not a block.</b>
    /// <para>
    /// The filter kept rows whose lockout was over and the projection badged any
    /// lockout there had ever been, so an account that got its password wrong
    /// ten times an hour ago came back red, with an Unblock button, while
    /// <b>Include blocked</b> was switched off. The switch looked broken because
    /// the rows it hides were never the rows the badge was drawn on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_lockout_that_has_elapsed_is_not_a_block()
    {
        var (id, _, last) = await NamedAsync("Elapsed", $"Lockout{Guid.NewGuid():N}"[..15]);

        await using (var context = server.NewContext())
        {
            var person = await context.Users.FirstAsync(u => u.Id == id);
            person.LockoutEnd = DateTimeOffset.UtcNow.AddHours(-1);
            await context.SaveChangesAsync();
        }

        var admin = await AdminAsync(server);

        // Listed with blocked accounts excluded, because it is not one.
        var listed = await ItemsAsync(admin, $"/api/v1/users/managed?pageSize=50&search={last}");
        var row = Assert.Single(listed);

        // And it says so: the badge is drawn from this field.
        Assert.True(!row.TryGetProperty("blockedAt", out var blocked)
            || blocked.ValueKind == JsonValueKind.Null);
    }

    /// <summary>And a lockout still in force is still a block, both ways.</summary>
    [Fact]
    public async Task A_lockout_still_in_force_is_still_a_block()
    {
        var (id, _, last) = await NamedAsync("Standing", $"Lockout{Guid.NewGuid():N}"[..15]);
        var admin = await AdminAsync(server);

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/users/{id}/blocked", new { blocked = true, reason = "Na wniosek" }));

        var hidden = await ItemsAsync(admin, $"/api/v1/users/managed?pageSize=50&search={last}");
        Assert.Empty(hidden);

        var shown = await ItemsAsync(
            admin, $"/api/v1/users/managed?pageSize=50&includeBlocked=true&search={last}");
        var row = Assert.Single(shown);
        Assert.True(row.TryGetProperty("blockedAt", out var standing)
            && standing.ValueKind != JsonValueKind.Null);
    }

    // ── runners ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The one thing on that screen an operator sets themselves.</b>
    /// <para>
    /// Everything else about a Runner is its own self-report; the tags are what
    /// decides which pool a machine serves, and so the likeliest thing anybody
    /// types. The box has offered it since the screen existed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_runner_is_found_by_a_tag_an_operator_set()
    {
        var runner = await Build.RunnerAsync(server);
        var admin = await AdminAsync(server);
        var tag = $"lab-{Guid.NewGuid():N}"[..12].ToLowerInvariant();

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/runners/{runner.Id}/tags", new { tags = new[] { tag } }));

        var found = await ItemsAsync(admin, $"/api/v1/runners?pageSize=100&search={tag}");
        var row = Assert.Single(found);
        Assert.Equal(runner.Id.ToString(), row.GetProperty("id").GetString());
    }

    // ── questions ───────────────────────────────────────────────────────────

    /// <summary>
    /// The box says topic, body or author, and now all three are true.
    /// </summary>
    [Fact]
    public async Task A_question_is_found_by_its_authors_name()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var (id, first, last) = await NamedAsync("Jan", $"Pytajacy{Guid.NewGuid():N}"[..16]);
        var asker = await Sign.InAsync(server, (await NameOfAsync(id))!, Sign.Password);

        await Sign.Succeeded(await asker.PostAsJsonAsync($"/api/v1/activities/{slug}/enrollment", new { }));
        await Sign.Succeeded(await asker.PostAsJsonAsync($"/api/v1/activities/{slug}/questions", new
        {
            topic = "Limit czasu",
            body = "Czy limit dotyczy jednego testu?",
        }));

        var admin = await AdminAsync(server);
        var activityId = await ActivityIdAsync(slug);

        var byLast = await ItemsAsync(
            admin, $"/api/v1/questions?pageSize=50&activityId={activityId}&search={last}");
        Assert.Single(byLast);

        // And by the whole name, which is what the Author column draws.
        var byWhole = await ItemsAsync(
            admin, $"/api/v1/questions?pageSize=50&activityId={activityId}&search={first} {last}");
        Assert.Single(byWhole);
    }

    /// <summary>
    /// <b>An announcement is not findable by the staff name it withholds.</b>
    /// <para>
    /// The projection nulls an announcement's author deliberately. A search that
    /// matched it anyway would let a name be found that the answer does not
    /// carry, which is a disclosure difference rather than a filtering one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_announcements_author_is_not_findable_by_name()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        await Sign.Succeeded(await admin.PostAsJsonAsync($"/api/v1/activities/{slug}/announcements", new
        {
            topic = "Przerwa techniczna",
            body = "Zawody wznowimy o 14:00.",
        }));

        var activityId = await ActivityIdAsync(slug);

        // The announcement is there, found by its own words.
        var byTopic = await ItemsAsync(
            admin, $"/api/v1/questions?pageSize=50&activityId={activityId}&search=Przerwa");
        var row = Assert.Single(byTopic);
        Assert.Equal("announcement", row.GetProperty("kind").GetString());
        // Absent or null: the projection withholds it either way, and the
        // serializer drops a null rather than writing one.
        Assert.True(!row.TryGetProperty("authorName", out var author)
            || author.ValueKind == JsonValueKind.Null);

        // And not by the name of whoever wrote it.
        var byAuthor = await ItemsAsync(
            admin, $"/api/v1/questions?pageSize=50&activityId={activityId}&search={Seeder.DevAdminLogin}");
        Assert.Empty(byAuthor);
    }

    // ── submissions ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The User column draws a name and this searched the login.</b>
    /// <para>
    /// So typing what was in front of you emptied the list. It happened to work
    /// only for accounts with no name at all, which is where the display falls
    /// back to the login.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_submission_is_found_by_the_name_its_row_draws()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var (id, first, last) = await NamedAsync("Maria", $"Zglaszajaca{Guid.NewGuid():N}"[..18]);
        var participant = await Sign.InAsync(server, (await NameOfAsync(id))!, Sign.Password);

        await Sign.Succeeded(await participant.PostAsJsonAsync(
            $"/api/v1/activities/{slug}/enrollment", new { }));
        await Build.SubmitAsync(participant, slug, "print(1)\n");

        var admin = await AdminAsync(server);
        var activityId = await ActivityIdAsync(slug);
        var url = $"/api/v1/submissions?pageSize=50&activityId={activityId}";

        var drawn = await ItemsAsync(admin, $"{url}&search={first} {last}");
        var row = Assert.Single(drawn);
        Assert.Equal($"{first} {last}", row.GetProperty("userName").GetString());

        var byLast = await ItemsAsync(admin, $"{url}&search={last}");
        Assert.Single(byLast);
    }

    /// <summary>
    /// And by the problem's name, not only by the one letter it is filed under.
    /// </summary>
    [Fact]
    public async Task A_submission_is_found_by_the_problems_name()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);
        await Build.SubmitAsync(participant, slug, "print(1)\n");

        var admin = await AdminAsync(server);
        var activityId = await ActivityIdAsync(slug);

        var listed = await ItemsAsync(admin, $"/api/v1/submissions?pageSize=50&activityId={activityId}");
        var name = Assert.Single(listed).GetProperty("problemName").GetString()!;

        var found = await ItemsAsync(
            admin, $"/api/v1/submissions?pageSize=50&activityId={activityId}&search={name}");
        Assert.Single(found);
    }

    // ── helpers that need the database ──────────────────────────────────────

    private async Task<string?> NameOfAsync(string id)
    {
        await using var context = server.NewContext();
        return (await context.Users.AsNoTracking().FirstAsync(u => u.Id == id)).UserName;
    }

    private async Task<string> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id.ToString();
    }
}

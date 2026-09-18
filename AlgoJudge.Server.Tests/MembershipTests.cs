using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// Being <b>in</b> an activity, which no permission answers.
/// <para>
/// A system grant unions into every activity, because that is what system scope
/// means — so an installation that maps an identity provider's group to the
/// participant template, which is the shape <c>FederatedSignInService</c> writes,
/// hands every such account a participant's keys everywhere. Submitting asked
/// whether they were actually in the activity; the board, the rounds, the
/// statements and the question queue did not. Found by the authorization audit
/// of 2026-09-09.
/// </para>
/// </summary>
[Collection("server-2")]
public class MembershipTests(ServerFixture server)
{
    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    /// <summary>An account holding a participant's keys at system scope, and no grant anywhere.</summary>
    private async Task<HttpClient> OutsiderAsync()
    {
        var login = "o-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);

        string id;
        await using (var context = server.NewContext())
        {
            id = (await context.Users.AsNoTracking().FirstAsync(u => u.UserName == login)).Id;
        }

        var admin = await AdminAsync(server);
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = id,
            permissions = Array.Empty<string>(),
            roleId = await Build.RoleIdAsync(admin, "participant"),
        }));

        return client;
    }

    [Theory]
    [InlineData("results")]
    [InlineData("series")]
    [InlineData("questions")]
    public async Task A_participant_at_system_scope_is_not_in_every_activity(string resource)
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var outsider = await OutsiderAsync();

        var read = await outsider.GetAsync($"/api/v1/activities/{slug}/{resource}");

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        var refusal = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("enrollment.required", refusal.GetProperty("code").GetString());
    }

    /// <summary>
    /// And once they join, the same reads answer. The rule is membership, not a
    /// second permission nobody was given.
    /// </summary>
    [Fact]
    public async Task And_is_in_the_one_they_joined()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var participant = await Build.ParticipantAsync(server, slug);

        var read = await participant.GetAsync($"/api/v1/activities/{slug}/series");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// **Staff are not members and do not have to be.** An administrator holds
    /// the catalog by the bypass, which makes them staff everywhere.
    /// </summary>
    [Fact]
    public async Task Staff_read_without_joining()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var admin = await AdminAsync(server);

        var read = await admin.GetAsync($"/api/v1/activities/{slug}/series");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// <b>And an activity you may not see answers 404, not 403.</b> The
    /// activity's own page has always refused that way, because the address is
    /// guessable; every sub-resource beside it resolved the same row and then
    /// answered 403, so the shape of the refusal said which slugs exist.
    /// </summary>
    [Fact]
    public async Task An_unlisted_activity_is_not_confirmed_to_exist()
    {
        var (slug, _) = await Build.ActivityAsync(server);

        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.Unlisted = true;
            await context.SaveChangesAsync();
        }

        var outsider = await OutsiderAsync();

        var read = await outsider.GetAsync($"/api/v1/activities/{slug}/results");
        var parent = await outsider.GetAsync($"/api/v1/activities/{slug}");

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        // The point is that they agree: one refusal, whichever door is tried.
        Assert.Equal(parent.StatusCode, read.StatusCode);
    }

    /// <summary>
    /// <b>An activity being prepared takes nobody.</b> `GetAsync` has always
    /// answered 404 for an unpublished one; enrolling checked the archive and
    /// the join policy and not this, and then returned the projection it had
    /// just granted access to.
    /// </summary>
    [Fact]
    public async Task An_unpublished_activity_accepts_no_enrollment()
    {
        var admin = await AdminAsync(server);
        var slug = "UNPUB-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        var created = await admin.PostAsJsonAsync("/api/v1/activities", new
        {
            slug,
            name = "Being prepared",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            joinPolicy = "open",
            languages = new[] { "python" },
        });
        await Sign.Succeeded(created);

        // Creating publishes; withdrawing is what a manager preparing one does.
        await using (var context = server.NewContext())
        {
            var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
            activity.PublishedAt = null;
            await context.SaveChangesAsync();
        }

        var stranger = await Sign.NewAccountAsync(server, "s-" + Guid.NewGuid().ToString("N")[..10]);
        var joined = await stranger.PostAsJsonAsync($"/api/v1/activities/{slug}/enrollment", new { });

        Assert.Equal(HttpStatusCode.NotFound, joined.StatusCode);
    }
}

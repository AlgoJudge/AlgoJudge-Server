using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The shipped <c>manager</c> template, granted the way the product grants it.
/// <para>
/// It is an <b>activity</b> grant: <c>ActivityService</c> writes exactly this
/// when somebody creates an activity, the seeder writes it on the seeded one,
/// and the template describes itself as <i>"Runs an activity: problems,
/// submissions, questions, enrolment"</i>. So what it is worth on an activity is
/// what it is worth at all.
/// </para>
/// <para>
/// <b>Nothing in the browser suite can answer this.</b> The Client's fake models
/// a grant's scope and never a permission's, so it hands out every key a grant
/// lists whatever the catalogue says the key means.
/// </para>
/// </summary>
[Collection("server-1")]
public class ManagerTemplateTests(ServerFixture server)
{
    private static async Task<HttpClient> AdminAsync(ServerFixture server) =>
        await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);

    private async Task<(HttpClient Client, string Id)> AccountAsync()
    {
        var login = "m-" + Guid.NewGuid().ToString("N")[..10];
        var client = await Sign.NewAccountAsync(server, login);
        await using var context = server.NewContext();
        var id = (await context.Users.AsNoTracking().FirstAsync(u => u.UserName == login)).Id;
        return (client, id);
    }

    private async Task<string> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id.ToString();
    }


    /// <summary>
    /// A managed row names the zone its activity keeps its clock in.
    ///
    /// <para>
    /// The panel lists rows from several activities at once, and the Client
    /// draws every instant in the reader's own zone. Without this field a
    /// manager arguing about whether a submission beat a deadline could not see
    /// the clock the deadline was set on — the row would name their zone and
    /// nothing else.
    /// </para>
    /// <para>
    /// It is asserted against a zone that is <b>not</b> the default, because the
    /// projection falls back to <c>UTC</c> when the activity is not loaded, and
    /// a test written against the fallback would pass on a missing
    /// <c>Include</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_managed_row_names_its_activitys_time_zone()
    {
        var admin = await AdminAsync(server);
        var (slug, _) = await Build.ActivityAsync(server);

        // A zone that is not the fallback, and not the one every other fixture
        // uses. Half an hour off the hour as well, which is the shape a naive
        // reader of an offset gets wrong.
        var current = await Build.GetAsync(admin, $"/api/v1/manager/activities/{slug}");
        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/activities/{slug}", new
        {
            slug,
            name = current.GetProperty("name").GetString(),
            type = current.GetProperty("type").GetString(),
            rankingType = current.GetProperty("rankingType").GetString(),
            timeZone = "Asia/Kolkata",
        }));

        await Build.SubmitAsync(await Build.ParticipantAsync(server, slug), slug, "print(1)\n");

        var page = await Build.GetAsync(admin, $"/api/v1/submissions?page=1&pageSize=50&activitySlug={slug}");
        var row = page.GetProperty("items").EnumerateArray().First();
        Assert.Equal("Asia/Kolkata", row.GetProperty("timeZone").GetString());

        var questions = await Build.GetAsync(admin, "/api/v1/questions?page=1&pageSize=50");
        foreach (var question in questions.GetProperty("items").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(question.GetProperty("timeZone").GetString()),
                "a question row names a zone");
        }
    }

    /// <summary>
    /// A manager of one activity, pointed at the shipped role on it — the way
    /// the panel enrols one, rather than by copying its permissions in.
    /// </summary>
    private async Task<HttpClient> ManagerOfAsync(string slug)
    {
        var admin = await AdminAsync(server);
        var (client, id) = await AccountAsync();

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = id,
            activityId = await ActivityIdAsync(slug),
            permissions = Array.Empty<string>(),
            roleId = await Build.RoleIdAsync(admin, "manager"),
        }));

        return client;
    }

    private static List<string> Keys(JsonElement answer) =>
        answer.EnumerateArray().Select(k => k.GetString() ?? "").ToList();

    /// <summary>
    /// <b>The panel is told they hold it.</b> <c>anywhere</c> is the question the
    /// manager panel asks, and it unions every grant's keys — so this passes
    /// today, and it is half of why the screen is offered.
    /// </summary>
    [Fact]
    public async Task What_a_manager_is_told_they_hold_includes_the_problem_library()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);

        var keys = Keys(await Build.GetAsync(manager, "/api/v1/permissions/mine/anywhere"));

        Assert.Contains("problem:read:own", keys);
        Assert.Contains("problem:create", keys);
    }

    /// <summary>
    /// <b>And the library refuses them.</b> Those five keys are declared
    /// <c>PermissionScope.Global</c>, <c>EffectiveAsync</c> counts an activity
    /// grant only when an activity is being asked about, and every call in
    /// <c>ProblemService</c> asks with none. So the template's own description
    /// promises a library its holder cannot open.
    /// </summary>
    [Fact]
    public async Task A_manager_may_open_the_problem_library_the_template_promises()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);

        var listed = await manager.GetAsync("/api/v1/problems?page=1&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    }

    /// <summary>
    /// The same one level down: creating a problem is what a manager preparing a
    /// round does first.
    /// </summary>
    [Fact]
    public async Task A_manager_may_create_a_problem()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);

        var created = await manager.PostAsJsonAsync("/api/v1/problems", new
        {
            slug = "p-" + Guid.NewGuid().ToString("N")[..8],
            name = "A problem a manager prepared",
            type = "standard-io@1",
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    // ── the panel's unfiltered lists ────────────────────────────────────────
    //
    // The panel asks these with no activity, because it is the list of
    // everything this person may see. Requiring the permission at the query's
    // own scope answered 403 to every manager granted on an activity; the
    // narrowing each of them already had — or, for grants, did not — is the
    // answer instead.

    /// <summary>200 and narrowed, not a refusal — and not another activity's work.</summary>
    [Fact]
    public async Task The_unfiltered_submissions_list_is_narrowed_rather_than_refused()
    {
        var (mine, _) = await Build.ActivityAsync(server);
        var (theirs, _) = await Build.ActivityAsync(server);

        await Build.SubmitAsync(await Build.ParticipantAsync(server, mine), mine, "print(1)\n");
        await Build.SubmitAsync(await Build.ParticipantAsync(server, theirs), theirs, "print(2)\n");

        var manager = await ManagerOfAsync(mine);

        var page = await Build.GetAsync(manager, "/api/v1/submissions?page=1&pageSize=100");
        var activities = page.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("activitySlug").GetString())
            .Distinct()
            .ToList();

        Assert.Equal([mine], activities);
    }

    /// <summary>The same for questions, which had the same shape.</summary>
    [Fact]
    public async Task The_unfiltered_questions_list_is_narrowed_rather_than_refused()
    {
        var (mine, _) = await Build.ActivityAsync(server);
        var (theirs, _) = await Build.ActivityAsync(server);

        foreach (var (slug, topic) in new[] { (mine, "Mine"), (theirs, "Theirs") })
        {
            var asker = await Build.ParticipantAsync(server, slug);
            await Sign.Succeeded(await asker.PostAsJsonAsync($"/api/v1/activities/{slug}/questions", new
            {
                topic,
                body = "Czy limit dotyczy jednego testu?",
            }));
        }

        var manager = await ManagerOfAsync(mine);

        var page = await Build.GetAsync(manager, "/api/v1/questions?page=1&pageSize=100");
        var topics = page.GetProperty("items").EnumerateArray()
            .Select(row => row.GetProperty("topic").GetString())
            .ToList();

        Assert.Contains("Mine", topics);
        Assert.DoesNotContain("Theirs", topics);
    }

    /// <summary>
    /// Grants had no narrowing at all, so this one is new behaviour rather than
    /// unreachable behaviour. **A system grant stays out of it**: running a
    /// course is not running the installation.
    /// </summary>
    [Fact]
    public async Task The_unfiltered_grants_list_shows_the_activity_and_not_the_installation()
    {
        var (mine, _) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(mine);
        var here = await ActivityIdAsync(mine);

        var page = await Build.GetAsync(manager, "/api/v1/grants?page=1&pageSize=100");
        var rows = page.GetProperty("items").EnumerateArray().ToList();

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
            Assert.Equal(here, row.GetProperty("activityId").GetString()));
    }

    /// <summary>
    /// **Holding it nowhere is still a refusal.** An empty page would tell
    /// somebody who may not look that there is nothing to see.
    /// </summary>
    [Fact]
    public async Task Somebody_who_manages_nothing_is_refused_rather_than_shown_an_empty_page()
    {
        var (_, id) = await AccountAsync();
        Assert.NotEmpty(id);
        var nobody = await Sign.InAsync(server, Seeder.DevParticipantLogin, Seeder.DevParticipantPassword);

        var listed = await nobody.GetAsync("/api/v1/submissions?page=1&pageSize=20");

        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
    }
}

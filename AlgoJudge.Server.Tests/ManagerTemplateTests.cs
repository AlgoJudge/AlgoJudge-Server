using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The shipped <c>manager</c> template, granted the way the product grants it.
/// <para>
/// It is an <b>activity</b> grant: <c>ActivityService</c> writes exactly this
/// when somebody creates an activity, the seeder writes it on the seeded one,
/// and the template describes itself as <i>"Runs an activity: problems,
/// submissions, questions, enrollment"</i>. So what it is worth on an activity is
/// what it is worth at all.
/// </para>
/// <para>
/// <b>Nothing in the browser suite can answer this.</b> The Client's fake models
/// a grant's scope and never a permission's, so it hands out every key a grant
/// lists whatever the catalog says the key means.
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
    /// Both rows are asserted against a zone that is <b>not</b> the default, and
    /// by equality, because each projection falls back to <c>UTC</c> when the
    /// activity is not loaded: a test written against the fallback would pass on
    /// a missing <c>Include</c>. Submissions and questions are two projections
    /// with two <c>Include</c>s, so one of them proves nothing about the other.
    /// </para>
    /// <para>
    /// The question is created here rather than looked for. Reading whatever the
    /// shared world happens to hold made the assertion skip its own body in a
    /// filtered run, which reads exactly like passing.
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

        var participant = await Build.ParticipantAsync(server, slug);
        await Build.SubmitAsync(participant, slug, "print(1)\n");
        await Sign.Succeeded(await participant.PostAsJsonAsync($"/api/v1/activities/{slug}/questions", new
        {
            topic = "Zone",
            body = "Czy limit dotyczy jednego testu?",
        }));

        var page = await Build.GetAsync(admin, $"/api/v1/submissions?page=1&pageSize=50&activitySlug={slug}");
        var row = page.GetProperty("items").EnumerateArray().First();
        Assert.Equal("Asia/Kolkata", row.GetProperty("timeZone").GetString());

        var questions = await Build.GetAsync(
            admin, $"/api/v1/questions?page=1&pageSize=50&activityId={await ActivityIdAsync(slug)}");
        var asked = questions.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Asia/Kolkata", asked.GetProperty("timeZone").GetString());
    }

    /// <summary>
    /// A manager of one activity, pointed at the shipped role on it — the way
    /// the panel enrolls one, rather than by copying its permissions in.
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
            roleIds = new[] { await Build.RoleIdAsync(admin, "manager") },
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
    /// Grants had no narrowing at all, so this one is new behavior rather than
    /// unreachable behavior. **A system grant stays out of it**: running a
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

    /// <summary>
    /// A problem of the administrator's, with one version, at a stated visibility.
    /// </summary>
    private async Task<(string Id, string VersionId)> LibraryProblemAsync(string visibility)
    {
        var admin = await AdminAsync(server);
        var problem = await Build.PostAsync(admin, "/api/v1/problems", new
        {
            slug = "lib-" + Guid.NewGuid().ToString("N")[..10],
            name = "Somebody else's problem",
            type = "standard-io@1",
        });
        var id = problem.GetProperty("id").GetString()!;

        var statement = await Build.UploadAsync(admin, "/api/v1/files", "content.md", "# Not yours\n");
        var version = await Build.PostAsync(admin, $"/api/v1/problems/{id}/versions", new
        {
            statements = new[] { new { fileId = statement } },
        });

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/problems/{id}/visibility", new { visibility, sharedWith = Array.Empty<string>() }));

        return (id, version.GetProperty("id").GetString()!);
    }

    /// <summary>
    /// <b>Enrolling somebody by hand means naming them.</b> The role carries
    /// <c>activity:enroll</c> and <c>grant:update</c>, and until 2026-09-14 it
    /// could spend neither: the only lookup that turns a person into an id asks
    /// <c>user:read:all</c> at system scope, which an activity grant never
    /// reaches, so both pickers in the panel came back empty.
    /// </summary>
    [Fact]
    public async Task A_manager_may_look_a_person_up_to_enroll_them()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);

        var found = await manager.GetAsync($"/api/v1/users?q={Seeder.DevParticipantLogin}");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        var people = (await found.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Contains(people, p => p.GetProperty("username").GetString() == Seeder.DevParticipantLogin);
    }

    /// <summary>
    /// <b>Naming a role as what an activity enrolls into hands out everything in
    /// it</b>, to everybody who joins afterwards. Without the excess rule a
    /// manager could point their own course's participant role at the shipped
    /// <c>administrator</c> one and let the next person through the door take
    /// the installation.
    /// </summary>
    [Fact]
    public async Task An_activitys_enrollment_role_cannot_carry_what_the_manager_does_not_hold()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);
        var admin = await AdminAsync(server);
        var current = await Build.GetAsync(manager, $"/api/v1/manager/activities/{slug}");

        var refused = await manager.PutAsJsonAsync($"/api/v1/activities/{slug}", new
        {
            slug,
            name = current.GetProperty("name").GetString(),
            type = current.GetProperty("type").GetString(),
            rankingType = current.GetProperty("rankingType").GetString(),
            timeZone = current.GetProperty("timeZone").GetString(),
            participantRoleIds = new[] { await Build.RoleIdAsync(admin, "admin") },
        });

        // Refused by the first arm of the shared rule — `system:administrator`
        // is installation-wide and a role reachable from an activity cannot
        // carry it — which answers 422 rather than the excess rule's 403. What
        // matters is that it did not take, so the activity is read back.
        Assert.False(refused.IsSuccessStatusCode, await refused.Content.ReadAsStringAsync());

        var after = await Build.GetAsync(manager, $"/api/v1/manager/activities/{slug}");
        Assert.Empty(after.GetProperty("participantRoleIds").EnumerateArray());
    }

    /// <summary>
    /// <b><c>problem:attach</c> says where, never which.</b> It is held in an
    /// activity and says this person may put problems into its rounds; the
    /// library has its own access list, and attaching was the one entry point
    /// that never asked — so any id would do.
    /// </summary>
    [Fact]
    public async Task A_manager_cannot_attach_a_problem_they_may_not_read()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);
        var (theirs, _) = await LibraryProblemAsync("private");

        var refused = await manager.PostAsJsonAsync($"/api/v1/series/{roundId}/problems", new
        {
            problemId = theirs, slug = "Z",
        });

        // 404 rather than 403: a problem somebody may not see must not be
        // confirmed to exist by the shape of the refusal.
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    /// <summary>
    /// A version id says which problem it belongs to, and this never asked — so
    /// a round could be pinned to a statement nobody attached.
    /// </summary>
    [Fact]
    public async Task A_pin_must_name_a_version_of_the_problem_being_attached()
    {
        var (slug, roundId) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);
        var (mine, _) = await LibraryProblemAsync("instance");
        var (_, foreignVersion) = await LibraryProblemAsync("instance");

        var refused = await manager.PostAsJsonAsync($"/api/v1/series/{roundId}/problems", new
        {
            problemId = mine, slug = "Y", pinnedProblemVersionId = foreignVersion,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    /// <summary>
    /// <b>A package is addressed by file id, and the arm that serves it asked
    /// only for the verb.</b> Anybody who manages any activity holds
    /// <c>problem:update</c> somewhere, so every package and every model
    /// solution in the installation was readable to all of them — around the
    /// library's own access list, which the package endpoint enforces.
    /// </summary>
    [Fact]
    public async Task A_manager_cannot_read_the_package_of_a_problem_they_may_not_see()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);
        var admin = await AdminAsync(server);

        // The activity's own seeded problem is the administrator's and private,
        // which is what every hand-made problem starts as.
        var packageId = await Build.PackageIdOfAsync(server, slug);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/files/{packageId}")).StatusCode);

        var refused = await manager.GetAsync($"/api/v1/files/{packageId}");

        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
    }

    /// <summary>
    /// And the other half of the same hole: a <b>model solution</b>.
    /// <para>
    /// The package and the manager-scoped files under a version are served by
    /// two arms of one method, and each asked only whether the caller may edit
    /// problems <i>somewhere</i>. This is the arm the package test does not
    /// reach — a model solution is the file that decides a contest, and it was
    /// readable by every manager in the installation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manager_cannot_read_the_model_solution_of_a_problem_they_may_not_see()
    {
        var (slug, _) = await Build.ActivityAsync(server);
        var manager = await ManagerOfAsync(slug);
        var admin = await AdminAsync(server);

        // Attached to the same version the round is pinned to, in the scope a
        // model solution lives in.
        var fileId = Guid.Parse(await Build.UploadAsync(
            admin, "/api/v1/files", "model.cpp", "int main() { return 0; }"));

        await using (var context = server.NewContext())
        {
            var assignment = await context.SeriesProblems
                .Include(sp => sp.Activity)
                .FirstAsync(sp => sp.Activity!.Slug == slug);
            context.FileReferences.Add(new FileReference
            {
                FileId = fileId,
                OwnerKind = FileOwnerKind.ProblemVersion,
                ProblemVersionId = assignment.PinnedProblemVersionId,
                Scope = FileScope.Manager,
                Name = "model.cpp",
            });
            await context.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        var refused = await manager.GetAsync($"/api/v1/files/{fileId}");

        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
    }
}

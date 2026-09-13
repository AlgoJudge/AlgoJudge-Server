using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Authorization;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// A grant points at a role, and editing the role reaches everybody pointing at
/// it.
///
/// <para>
/// <b>This reverses a decision kept three times</b>, and the argument it was
/// kept on is real: a copy fails closed, one person at a time, while a live role
/// fails open, silently, for everybody at once. Four things answer that, and
/// each has a test here — the scope a role belongs to, the excess rule applied
/// to editing one, the staff flag recomputed for every linked grant, and the
/// count of what an edit reaches.
/// </para>
/// </summary>
[Collection("server-3")]
public class RoleTests(ServerFixture server)
{
    /// <summary>
    /// <b>The whole change, in one test.</b> Somebody holds a role; the role
    /// gains a permission; they hold it — and nothing touched their grant.
    /// </summary>
    [Fact]
    public async Task Editing_a_role_changes_what_its_holder_may_do()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await NewActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);

        var role = await CreateRoleAsync(admin, "graders-" + Suffix(), activityId,
            ["activity:read", "submission:read:all"]);

        var (grader, graderId) = await AccountAsync("grader");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = graderId,
            activityId,
            permissions = Array.Empty<string>(),
            roleId = role,
        }));

        Assert.DoesNotContain("submission:rejudge", await MineAsync(grader, activityId));

        // The grant is not touched — only the role.
        var before = await GrantRowAsync(graderId, activityId);
        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/roles/{role}", new
        {
            name = await RoleNameAsync(role),
            permissions = new[] { "activity:read", "submission:read:all", "submission:rejudge" },
        }));

        Assert.Contains("submission:rejudge", await MineAsync(grader, activityId));

        var after = await GrantRowAsync(graderId, activityId);
        Assert.Equal(before.Permissions, after.Permissions);
        Assert.Equal(before.RoleId, after.RoleId);
    }

    /// <summary>
    /// One extra key handed to one person does not cut them off from the role's
    /// corrections. That is the whole reason a grant may carry both.
    /// </summary>
    [Fact]
    public async Task A_grant_keeps_its_own_additions_when_its_role_changes()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await NewActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);

        var role = await CreateRoleAsync(admin, "helpers-" + Suffix(), activityId, ["activity:read"]);

        var (helper, helperId) = await AccountAsync("helper");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = helperId,
            activityId,
            permissions = new[] { "question:answer" },
            roleId = role,
        }));

        var mine = await MineAsync(helper, activityId);
        Assert.Contains("activity:read", mine);
        Assert.Contains("question:answer", mine);

        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/roles/{role}", new
        {
            name = await RoleNameAsync(role),
            permissions = new[] { "activity:read", "ranking:read" },
        }));

        var after = await MineAsync(helper, activityId);
        Assert.Contains("ranking:read", after);
        Assert.Contains("question:answer", after);
    }

    /// <summary>
    /// An activity's role is that activity's. Letting it be granted elsewhere
    /// would make the scope decorative: write a role where you may, hand it out
    /// where you may not.
    /// </summary>
    [Fact]
    public async Task An_activitys_role_is_refused_on_another_activity_and_at_system_scope()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var mine = await ActivityIdAsync(await NewActivityAsync(admin));
        var theirs = await ActivityIdAsync(await NewActivityAsync(admin));

        var role = await CreateRoleAsync(admin, "local-" + Suffix(), mine, ["activity:read"]);
        var (_, id) = await AccountAsync("outsider");

        var elsewhere = await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = id,
            activityId = theirs,
            permissions = Array.Empty<string>(),
            roleId = role,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, elsewhere.StatusCode);
        Assert.Equal("grant.role.scope", await Code(elsewhere));

        var globally = await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = id,
            permissions = Array.Empty<string>(),
            roleId = role,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, globally.StatusCode);
        Assert.Equal("grant.role.scope", await Code(globally));
    }

    /// <summary>
    /// <b>The excess rule, applied where it never had to be before.</b> A
    /// template could only be edited by an administrator, who is exempt from it.
    /// A role may be edited by a manager, and editing one changes what other
    /// people hold — so without this, <c>role:manage</c> in one activity would be
    /// a way of granting oneself anything.
    /// </summary>
    [Fact]
    public async Task A_manager_cannot_put_into_a_role_a_permission_they_do_not_hold()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync(await NewActivityAsync(admin));

        var (manager, managerId) = await AccountAsync("narrow-manager");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = managerId,
            activityId,
            permissions = new[] { "activity:read", "grant:update", "role:read", "role:manage" },
        }));

        var refused = await manager.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "too-much-" + Suffix(),
            activityId,
            permissions = new[] { "activity:read", "problem:delete" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("role.excess", await Code(refused));

        // What they do hold, they may write.
        var allowed = await manager.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "just-right-" + Suffix(),
            activityId,
            permissions = new[] { "activity:read" },
        });
        await Sign.Succeeded(allowed);
    }

    /// <summary>
    /// A manager runs their own activity's roles and not the installation's.
    /// This is the whole reason a role has a scope.
    /// </summary>
    [Fact]
    public async Task A_manager_may_not_edit_the_installations_roles()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync(await NewActivityAsync(admin));

        var (manager, managerId) = await AccountAsync("local-manager");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = managerId,
            activityId,
            permissions = new[] { "activity:read", "grant:update", "role:read", "role:manage" },
        }));

        var participant = await Build.RoleIdAsync(manager, "participant");
        var refused = await manager.PutAsJsonAsync($"/api/v1/roles/{participant}", new
        {
            name = "participant",
            permissions = new[] { "activity:read" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // And may not make one there either.
        var invented = await manager.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "installation-wide-" + Suffix(),
            permissions = new[] { "activity:read" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, invented.StatusCode);
    }

    /// <summary>
    /// <b>A role edit can change who is a competitor</b>, and that is the
    /// dangerous half of a live role. Adding a staff key to the role a course
    /// enrols into takes everybody holding it out of the ranking, so the flag is
    /// recomputed rather than left saying yesterday's answer.
    /// </summary>
    [Fact]
    public async Task A_role_edit_that_adds_a_staff_key_makes_its_holders_systemic()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync(await NewActivityAsync(admin));

        var role = await CreateRoleAsync(admin, "competitors-" + Suffix(), activityId,
            ["activity:read", "submission:create", "ranking:read"]);

        var (_, id) = await AccountAsync("competitor");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = id,
            activityId,
            permissions = Array.Empty<string>(),
            roleId = role,
        }));

        Assert.False((await GrantRowAsync(id, activityId)).IsSystem);

        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/roles/{role}", new
        {
            name = await RoleNameAsync(role),
            permissions = new[] { "activity:read", "submission:create", "submission:read:all" },
        }));

        Assert.True((await GrantRowAsync(id, activityId)).IsSystem);
    }

    /// <summary>
    /// The count the panel shows before saving — the only one of the four guards
    /// a person actually sees.
    /// </summary>
    [Fact]
    public async Task A_role_says_how_many_grants_an_edit_would_reach()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync(await NewActivityAsync(admin));
        var role = await CreateRoleAsync(admin, "counted-" + Suffix(), activityId, ["activity:read"]);

        Assert.Equal(0, await ReachOfAsync(admin, role, activityId));

        foreach (var who in new[] { "counted-one", "counted-two" })
        {
            var (_, id) = await AccountAsync(who);
            await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
            {
                userId = id,
                activityId,
                permissions = Array.Empty<string>(),
                roleId = role,
            }));
        }

        Assert.Equal(2, await ReachOfAsync(admin, role, activityId));
    }

    /// <summary>
    /// Deleting a role somebody holds would take rights away without anybody
    /// saying so. Refused by name here, and by the foreign key underneath.
    /// </summary>
    [Fact]
    public async Task A_role_somebody_holds_cannot_be_deleted()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync(await NewActivityAsync(admin));
        var role = await CreateRoleAsync(admin, "held-" + Suffix(), activityId, ["activity:read"]);

        var (_, id) = await AccountAsync("holder");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = id,
            activityId,
            permissions = Array.Empty<string>(),
            roleId = role,
        }));

        var refused = await admin.DeleteAsync($"/api/v1/roles/{role}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("role.inUse", await Code(refused));
    }

    /// <summary>
    /// The key is only honoured at system scope, so a role belonging to one
    /// activity that carried it would show a right every check disagrees with —
    /// the same rule an activity grant has always had.
    /// </summary>
    [Fact]
    public async Task An_activitys_role_cannot_carry_the_administrator_key()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync(await NewActivityAsync(admin));

        var refused = await admin.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "back-door-" + Suffix(),
            activityId,
            permissions = new[] { "system:administrator" },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("role.permission.scope", await Code(refused));
    }

    /// <summary>
    /// <b>Joining a course hands out a role, not a copy.</b> Whoever creates an
    /// activity is pointed at the shipped manager role, and whoever joins is
    /// pointed at whatever that activity chose — which is what makes an
    /// activity's own role reach anybody at all.
    /// </summary>
    [Fact]
    public async Task Joining_an_activity_links_the_role_it_enrols_into()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await NewActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);
        var adminId = await UserIdAsync(Seeder.DevAdminLogin);

        // Whoever made it manages it, through the shipped role.
        var creator = await GrantRowAsync(adminId, activityId);
        Assert.Equal(await Build.RoleIdAsync(admin, "manager"), creator.RoleId.ToString());

        await Sign.Succeeded(await admin.PostAsJsonAsync(
            $"/api/v1/activities/{activityId}/published", new { published = true }));

        // Nothing chosen: the shipped participant role.
        var (first, firstId) = await AccountAsync("joins-default");
        await Sign.Succeeded(await first.PostAsJsonAsync(
            $"/api/v1/activities/{slug}/enrolment", new { }));
        Assert.Equal(
            await Build.RoleIdAsync(admin, "participant"),
            (await GrantRowAsync(firstId, activityId)).RoleId.ToString());

        // The activity's own choice, once it has made one.
        var chosen = await CreateRoleAsync(admin, "our-people-" + Suffix(), activityId,
            ["activity:read", "submission:create", "ranking:read"]);
        await Sign.Succeeded(await admin.PutAsJsonAsync(
            $"/api/v1/activities/{slug}",
            await ActivityInputAsync(admin, slug, chosen)));

        var (second, secondId) = await AccountAsync("joins-chosen");
        await Sign.Succeeded(await second.PostAsJsonAsync(
            $"/api/v1/activities/{slug}/enrolment", new { }));
        Assert.Equal(chosen, (await GrantRowAsync(secondId, activityId)).RoleId.ToString());
    }


    /// <summary>
    /// <b>Pointing at a role is granting what the role holds.</b> An excess rule
    /// that read only the entries typed into the request would let anybody with
    /// <c>grant:update</c> hand out the shipped manager role by naming it — the
    /// escalation the rule exists to stop, reached by a different door.
    /// </summary>
    [Fact]
    public async Task Nobody_grants_a_role_carrying_more_than_they_hold()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync(await NewActivityAsync(admin));

        var (manager, managerId) = await AccountAsync("thin-manager");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = managerId,
            activityId,
            permissions = new[] { "activity:read", "grant:update", "role:read" },
        }));

        var (_, otherId) = await AccountAsync("would-be-manager");
        var refused = await manager.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = otherId,
            activityId,
            permissions = Array.Empty<string>(),
            roleId = await Build.RoleIdAsync(manager, "manager"),
        });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("grant.excess", await Code(refused));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<string> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString() ?? "";

    private static async Task<string> CreateRoleAsync(
        HttpClient admin, string name, string? activityId, string[] permissions)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/roles", new
        {
            name,
            activityId,
            permissions,
        });
        await Sign.Succeeded(created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
    }

    private async Task<string> RoleNameAsync(string id)
    {
        await using var context = server.NewContext();
        return (await context.PermissionRoles.AsNoTracking()
            .FirstAsync(r => r.Id == Guid.Parse(id))).Name;
    }

    private static async Task<int> ReachOfAsync(HttpClient client, string id, string activityId)
    {
        var roles = await client.GetFromJsonAsync<JsonElement>($"/api/v1/roles?activityId={activityId}");
        foreach (var role in roles.EnumerateArray())
        {
            if (role.GetProperty("id").GetString() == id) return role.GetProperty("grants").GetInt32();
        }
        throw new Xunit.Sdk.XunitException($"role {id} is not listed");
    }

    private static async Task<IReadOnlyList<string>> MineAsync(HttpClient client, string activityId)
    {
        var mine = await client.GetFromJsonAsync<string[]>(
            $"/api/v1/permissions/mine?activityId={activityId}");
        return mine ?? [];
    }

    private async Task<Grant> GrantRowAsync(string userId, string activityId)
    {
        await using var context = server.NewContext();
        return await context.Grants.AsNoTracking()
            .FirstAsync(g => g.UserId == userId && g.ActivityId == Guid.Parse(activityId));
    }

    private static async Task<object> ActivityInputAsync(
        HttpClient admin, string slug, string participantRoleId)
    {
        var activity = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/manager/activities/{slug}");
        return new
        {
            slug,
            name = activity.GetProperty("name").GetString(),
            type = activity.GetProperty("type").GetString(),
            rankingType = activity.GetProperty("rankingType").GetString(),
            timeZone = activity.GetProperty("timeZone").GetString(),
            participantRoleId,
        };
    }

    private static async Task<string> NewActivityAsync(HttpClient admin)
    {
        var slug = "ROLE-" + Suffix().ToUpperInvariant();
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/activities", new
        {
            slug,
            name = "Role test",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            joinPolicy = "open",
        }));
        return slug;
    }

    private async Task<(HttpClient Client, string Id)> AccountAsync(string prefix)
    {
        var login = prefix + "-" + Suffix();
        var client = await Sign.NewAccountAsync(server, login);
        return (client, await UserIdAsync(login));
    }

    private async Task<string> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id.ToString();
    }

    private async Task<string> UserIdAsync(string login)
    {
        await using var context = server.NewContext();
        return (await context.Users.AsNoTracking().FirstAsync(u => u.UserName == login)).Id;
    }
}

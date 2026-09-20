using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What a role belongs to, and who may write it — the scope rules the
/// end-to-end verification of 2026-09-19 found holes in.
///
/// <para>
/// Every test here is a defect that was reproduced against a running Server,
/// Keycloak and Moodle before it was closed. They are written as one test per
/// defect so a regression names itself.
/// </para>
/// </summary>
[Collection("server-3")]
public class RoleScopeTests(ServerFixture server)
{
    /// <summary>
    /// <b>Renaming an activity's role used to rewrite the installation's
    /// mapping.</b>
    ///
    /// <para>
    /// Rules named a role by name, and names are unique only within a scope. A
    /// manager of one course — every LTI instructor is one — could create a role
    /// called <c>participant</c> in their own activity and rename it to
    /// <c>manager</c>; every provider rule naming the installation's
    /// <c>participant</c> followed the rename, and at the next sign-in every
    /// directory participant held the manager role installation-wide. Renaming
    /// it to a free name instead left them holding nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Renaming_an_activitys_role_leaves_the_installations_mapping_alone()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await ActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);

        var participantId = await Build.RoleIdAsync(admin, "participant");
        var provider = await RegisterProviderAsync(admin, await Build.RuleAsync(
            admin, "students", "participant"));

        // The activity's own role, named exactly like the installation's.
        var theirs = await CreateRoleAsync(admin, "participant", activityId, ["activity:read"]);

        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/roles/{theirs}", new
        {
            name = "manager",
            permissions = new[] { "activity:read" },
        }));

        var read = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/identity/providers/{provider}");
        var rule = read.GetProperty("mappingRules").EnumerateArray().Single();
        var target = rule.GetProperty("targets").EnumerateArray().Single();

        Assert.Equal("students", rule.GetProperty("claimValue").GetString());
        Assert.Equal(participantId, target.GetProperty("roleId").GetString());
        Assert.Equal("participant", target.GetProperty("roleName").GetString());
    }

    /// <summary>
    /// <b>The installation's roles are an administrator's.</b>
    ///
    /// <para>
    /// The shipped <c>manager</c> role carried <c>role:manage</c> with both
    /// scopes, and a directory group mapped onto that role grants it at
    /// <i>system</i> scope — so anybody in the group could rewrite the
    /// installation's roles, including emptying the built-in <c>admin</c> one.
    /// Writing them needs <c>role:manage</c>, which is global and ships in the
    /// <c>admin</c> role alone.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_system_scope_manager_cannot_write_the_installations_roles()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (manager, managerId) = await AccountAsync("system-manager");

        // The manager role, at system scope, exactly as a directory group gives it.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = managerId,
            permissions = Array.Empty<string>(),
            roleIds = new[] { await Build.RoleIdAsync(admin, "manager") },
        }));

        var refused = await manager.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "invented-" + Suffix(),
            permissions = new[] { "activity:read" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var participantId = await Build.RoleIdAsync(admin, "participant");
        var edit = await manager.PutAsJsonAsync($"/api/v1/roles/{participantId}", new
        {
            name = "participant",
            permissions = new[] { "activity:read" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
    }

    /// <summary>
    /// <b>The administrator's role is fixed, and the last administrator cannot
    /// be edited away.</b> Emptying it took <c>system:administrator</c> from
    /// everybody holding it through the role, in one save, with no way back
    /// short of the database.
    /// </summary>
    [Fact]
    public async Task The_admin_role_cannot_be_emptied()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var adminRole = await Build.RoleIdAsync(admin, "admin");

        var refused = await admin.PutAsJsonAsync($"/api/v1/roles/{adminRole}", new
        {
            name = "admin",
            permissions = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("role.builtIn.fixed", await Code(refused));

        await using var context = server.NewContext();
        var stored = await context.PermissionRoles.AsNoTracking()
            .SingleAsync(r => r.BuiltInKey == DefaultRoles.Admin);
        Assert.Contains("system:administrator", stored.Permissions);
    }

    /// <summary>
    /// <b>An activity's manager writes that activity's roles.</b> The key is
    /// activity-scoped and ships in the manager role, so the reach of a manager
    /// ends where their activity does.
    /// </summary>
    [Fact]
    public async Task An_activitys_manager_writes_that_activitys_roles_and_no_others()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var mine = await ActivityIdAsync(await ActivityAsync(admin));
        var theirs = await ActivityIdAsync(await ActivityAsync(admin));

        var (manager, managerId) = await AccountAsync("runs-one-course");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = managerId,
            activityId = mine,
            permissions = Array.Empty<string>(),
            roleIds = new[] { await Build.RoleIdAsync(admin, "manager") },
        }));

        var ours = await manager.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "jury-" + Suffix(),
            activityId = mine,
            permissions = new[] { "activity:read" },
        });
        await Sign.Succeeded(ours);

        var elsewhere = await manager.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "jury-" + Suffix(),
            activityId = theirs,
            permissions = new[] { "activity:read" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, elsewhere.StatusCode);

        // And reading: another activity's roles are that activity's business.
        // Asking "anywhere" and then serving whichever activity was named let a
        // manager read the names, permissions and reach of a colleague's roles.
        var refused = await manager.GetAsync($"/api/v1/roles?activityId={theirs}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var readable = await manager.GetFromJsonAsync<JsonElement>($"/api/v1/roles?activityId={mine}");
        Assert.Contains(readable.EnumerateArray(),
            r => r.GetProperty("name").GetString() == "manager");
    }

    /// <summary>
    /// <b>A provider's default roles are the installation's.</b> Resolved by
    /// name and without a scope filter, the fallback could pick an activity's
    /// role of the same name and hand its permissions out installation-wide.
    /// </summary>
    [Fact]
    public async Task A_providers_default_role_cannot_be_an_activitys()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var activityId = await ActivityIdAsync(await ActivityAsync(admin));
        var theirs = await CreateRoleAsync(admin, "shadow-" + Suffix(), activityId, ["activity:read"]);

        var refused = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug = "defaults-" + Suffix()[..6],
            displayName = "University SSO",
            issuer = "https://auth.example.invalid/o/algojudge",
            clientId = "algojudge",
            clientSecret = "development-only",
            claimPath = "groups",
            unmappedBehavior = "defaultRole",
            defaultRoleIds = new[] { theirs },
            mappingRules = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("provider.rule.role.scope", await Code(refused));
    }

    /// <summary>
    /// <b>The reach a role edit will have counts the people a provider's rules
    /// reach.</b> Those contributions were copies and counted as nothing, so a
    /// role hundreds of people held through a directory group told an
    /// administrator that editing it reached nobody.
    /// </summary>
    [Fact]
    public async Task The_reach_of_a_role_counts_a_providers_contribution()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (_, personId) = await AccountAsync("through-a-provider");

        var role = await CreateRoleAsync(admin, "mapped-" + Suffix(), null, ["activity:read"]);
        var provider = await RegisterProviderAsync(admin, new
        {
            claimValue = "students",
            targets = new[] { new { kind = "role", roleId = role } },
        });

        // What a sign-in writes: one grant per provider, holding links.
        await using (var context = server.NewContext())
        {
            var grant = new Grant
            {
                UserId = personId,
                SourceProviderId = Guid.Parse(provider),
                Permissions = "[]",
            };
            grant.Roles.Add(new GrantRole { GrantId = grant.Id, RoleId = Guid.Parse(role) });
            context.Grants.Add(grant);
            await context.SaveChangesAsync();
        }

        var roles = await admin.GetFromJsonAsync<JsonElement>("/api/v1/roles");
        var read = roles.EnumerateArray().Single(r => r.GetProperty("id").GetString() == role);

        Assert.Equal(1, read.GetProperty("grants").GetInt32());
        Assert.Contains(read.GetProperty("mappedBy").EnumerateArray(),
            slug => slug.GetString()!.StartsWith("reach-", StringComparison.Ordinal));
    }

    // ── helpers ─────────────────────────────────────────────────────────────


    /// <summary>
    /// <b>An activity's enrollment roles survive being read back.</b>
    ///
    /// <para>
    /// They were projected from <c>activity.EnrollmentRoles</c>, a navigation
    /// nothing includes, so every read answered an empty list while the rows sat
    /// in the table: a manager chose the roles a course enrolls into, saved, and
    /// watched the pickers come back blank. The write was never the problem, and
    /// the test that covered this asserted the list was <i>empty</i> — so it
    /// passed for exactly the reason the feature was broken.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_activitys_enrollment_roles_are_read_back()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await ActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);
        var ours = await CreateRoleAsync(admin, "enrolls-" + Suffix(), activityId, ["activity:read"]);

        await Sign.Succeeded(await admin.PutAsJsonAsync($"/api/v1/activities/{slug}", new
        {
            slug,
            name = "Role scope",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            participantRoleIds = new[] { ours },
        }));

        // **A fresh read, not the answer to the write.** The write answers from
        // the entity it has just tracked, so it reported the role correctly
        // while every later read reported none.
        var reread = await Build.GetAsync(admin, $"/api/v1/manager/activities/{slug}");
        Assert.Equal(
            [ours],
            reread.GetProperty("participantRoleIds").EnumerateArray()
                .Select(value => value.GetString()!).ToArray());
        Assert.Empty(reread.GetProperty("managerRoleIds").EnumerateArray());
    }

    /// <summary>
    /// <b>Whoever may write an activity's roles may delete one.</b>
    ///
    /// <para>
    /// Deleting asked for <c>role:manage</c>, which became global and an
    /// administrator's when the key was split — so a manager could create a role
    /// in their own activity, see the panel offer the delete button, and be
    /// refused. Create, edit and delete are one power and answer to one key.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manager_may_delete_a_role_in_their_own_activity()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await ActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);
        var (manager, managerId) = await AccountAsync("runs-it");

        var managerRole = await Build.RoleIdAsync(admin, "manager");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = managerId,
            activityId,
            permissions = Array.Empty<string>(),
            roleIds = new[] { managerRole },
        }));

        var created = await manager.PostAsJsonAsync("/api/v1/roles", new
        {
            name = "theirs-" + Suffix(),
            activityId,
            permissions = new[] { "activity:read" },
        });
        await Sign.Succeeded(created);
        var role = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var removed = await manager.DeleteAsync($"/api/v1/roles/{role}");
        Assert.True(removed.IsSuccessStatusCode,
            $"{(int)removed.StatusCode} {await removed.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// <b>A write that says nothing about roles reads them all the same.</b>
    ///
    /// <para>
    /// "Absent leaves the roles alone" reached the stored links but not the union
    /// every rule beside them is computed from, which was read from the request
    /// alone. So saving a grant without naming its roles — which is what moving
    /// somebody into a group, or clearing a flag, does — recomputed the staff
    /// flag from an empty set: a jury member linking <c>manager</c> silently
    /// became a competitor and rejoined the ranking.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_grant_saved_without_naming_roles_keeps_its_staff_flag()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await ActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);
        var (_, personId) = await AccountAsync("jury");

        var managerRole = await Build.RoleIdAsync(admin, "manager");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            activityId,
            permissions = Array.Empty<string>(),
            roleIds = new[] { managerRole },
        }));

        await using (var context = server.NewContext())
        {
            Assert.True((await context.Grants.AsNoTracking()
                .FirstAsync(g => g.UserId == personId && g.ActivityId == Guid.Parse(activityId))).IsSystem);
        }

        // The same grant again, saying nothing about its roles — the shape every
        // other edit to a membership sends.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            activityId,
            permissions = Array.Empty<string>(),
        }));

        await using (var context = server.NewContext())
        {
            var grant = await context.Grants.AsNoTracking().Include(g => g.Roles)
                .FirstAsync(g => g.UserId == personId && g.ActivityId == Guid.Parse(activityId));
            Assert.Single(grant.Roles);
            Assert.True(grant.IsSystem,
                "the staff flag was recomputed from an empty union and lost");
        }
    }

    /// <summary>
    /// <b>An administrator whose key comes from a role is still an
    /// administrator to the readers that protect them.</b>
    ///
    /// <para>
    /// A grant carries its permissions in links, so a reader of the row's own
    /// entries answers "holds nothing" about the account that holds everything.
    /// The merge blocker was one of three: the holder of <c>user:merge</c> could
    /// carry an administrator's account away, taking its federated identity and
    /// blocking the original.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_administrator_by_role_still_blocks_a_merge()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (_, personId) = await AccountAsync("by-role");
        var (_, targetId) = await AccountAsync("target");
        var adminRole = await Build.RoleIdAsync(admin, "admin");

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            permissions = Array.Empty<string>(),
            roleIds = new[] { adminRole },
        }));

        // The row itself holds nothing, which is the whole shape of the defect.
        await using (var context = server.NewContext())
        {
            var grant = await context.Grants.AsNoTracking()
                .FirstAsync(g => g.UserId == personId && g.ActivityId == null);
            Assert.Equal("[]", grant.Permissions);
        }

        var preview = await admin.PostAsJsonAsync(
            $"/api/v1/users/{personId}/merge-preview", new { targetUserId = targetId });
        await Sign.Succeeded(preview);

        var blockers = (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("blockers").EnumerateArray().Select(b => b.GetString()).ToList();
        Assert.NotEmpty(blockers);
        Assert.Contains(blockers, b => b!.Contains("installation", StringComparison.OrdinalIgnoreCase));
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<string> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString() ?? "";

    private static async Task<string> RegisterProviderAsync(HttpClient admin, object rule)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug = "reach-" + Suffix()[..6],
            displayName = "University SSO",
            issuer = "https://auth.example.invalid/o/algojudge",
            clientId = "algojudge",
            clientSecret = "development-only",
            claimPath = "groups",
            mappingRules = new[] { rule },
        });
        await Sign.Succeeded(created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateRoleAsync(
        HttpClient admin, string name, string? activityId, string[] permissions)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/roles", new { name, activityId, permissions });
        await Sign.Succeeded(created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
    }

    private static async Task<string> ActivityAsync(HttpClient admin)
    {
        var slug = "SCOPE-" + Suffix().ToUpperInvariant();
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/activities", new
        {
            slug,
            name = "Role scope",
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
        await using var context = server.NewContext();
        return (client, (await context.Users.AsNoTracking().FirstAsync(u => u.UserName == login)).Id);
    }

    private async Task<string> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        return (await context.Activities.AsNoTracking().FirstAsync(a => a.Slug == slug)).Id.ToString();
    }
}

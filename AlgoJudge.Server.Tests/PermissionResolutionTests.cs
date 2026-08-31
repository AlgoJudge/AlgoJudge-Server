using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using AlgoJudge.Server.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// The resolution rule, as restated on 2026-08-09.
/// <para>
/// Four clauses, and the order between the first two is the whole of it:
/// </para>
/// <list type="number">
/// <item>an activity grant carrying the <b>override</b> is the answer inside its
/// activity, and nothing else applies there;</item>
/// <item>otherwise <c>system:administrator</c> bypasses everything;</item>
/// <item>otherwise the <b>union</b> of every system contribution and the
/// activity grant;</item>
/// <item><b>nothing subtracts, anywhere.</b></item>
/// </list>
/// <para>
/// Every test here is about a way of getting that wrong that would not show up
/// as an error — somebody keeping rights they gave up, or losing rights nobody
/// took away.
/// </para>
/// </summary>
[Collection("server-2")]
public class PermissionResolutionTests(ServerFixture server)
{
    /// <summary>
    /// <b>The key that looks like an administrator and is not one.</b>
    /// <c>IsAdministratorAsync</c> requires <c>ActivityId is null</c> before
    /// honouring it, so written into an activity grant it confers nothing —
    /// while the panel shows somebody holding it. Refused at the write now,
    /// rather than stored and silently inert.
    /// </summary>
    [Fact]
    public async Task The_administrator_key_cannot_be_written_into_an_activity_grant()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, _) = await Build.ActivityAsync(server);

        string activityId;
        await using (var context = server.NewContext())
        {
            activityId = (await context.Activities.FirstAsync(a => a.Slug == slug)).Id.ToString();
        }

        var login = "scope-" + Guid.NewGuid().ToString("N")[..8];
        await Sign.NewAccountAsync(server, login);
        var userId = await UserIdAsync(login);

        var refused = await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId,
            activityId,
            permissions = new[] { "activity:read", "system:administrator" },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("grant.permission.scope", await refused.Content.ReadAsStringAsync());

        // **Refused for everybody**, including the administrator making the
        // request: this is not "may you grant it", it is "does it mean anything
        // here", and that does not depend on who is asking.
        await using (var context = server.NewContext())
        {
            Assert.False(await context.Grants.AnyAsync(
                g => g.UserId == userId && g.ActivityId == Guid.Parse(activityId)));
        }

        // And it is still a system grant's key.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId,
            permissions = new[] { "system:administrator" },
        }));

        await using (var context = server.NewContext())
        {
            await context.Grants.Where(g => g.UserId == userId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// <b>Why the rule above names one key instead of reading the catalogue.</b>
    /// Five of the shipped <c>manager</c> template's keys are declared
    /// <c>PermissionScope.Global</c> — the <c>problem:*</c> ones — and
    /// <c>ProblemService</c> requires them with no activity, which unions system
    /// grants only. So a manager granted the template <i>on an activity</i>, the
    /// way the seeder and the panel apply it, holds five permissions that do
    /// nothing.
    /// <para>
    /// This test asserts the current behaviour rather than the desired one, so
    /// that fixing it is a decision somebody takes rather than a surprise. Until
    /// then, refusing every misplaced global key would refuse the template this
    /// product ships.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_global_key_in_an_activity_grant_does_nothing()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var (slug, _) = await Build.ActivityAsync(server);

        string activityId;
        await using (var context = server.NewContext())
        {
            activityId = (await context.Activities.FirstAsync(a => a.Slug == slug)).Id.ToString();
        }

        var login = "global-" + Guid.NewGuid().ToString("N")[..8];
        var person = await Sign.NewAccountAsync(server, login);
        var userId = await UserIdAsync(login);

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId,
            activityId,
            permissions = new[] { "activity:read", "problem:create" },
        }));

        // Inside the activity the key is there, because an activity grant unions
        // into its own activity.
        var inside = await person.GetFromJsonAsync<string[]>(
            $"/api/v1/permissions/mine?activityId={activityId}");
        Assert.Contains("problem:create", inside!);

        // And installation-wide — which is where `problem:create` is actually
        // checked — it is not.
        var everywhere = await person.GetFromJsonAsync<string[]>("/api/v1/permissions/mine");
        Assert.DoesNotContain("problem:create", everywhere!);

        await using (var context = server.NewContext())
        {
            await context.Grants.Where(g => g.UserId == userId).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// **System scope is a union now.** One contribution assigned by hand, one
    /// per linked provider, and what the person holds is all of them together.
    /// Until this milestone the database allowed exactly one row and the union
    /// had nothing to union.
    /// </summary>
    [Fact]
    public async Task System_scope_is_the_union_of_contributions()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var person = await Sign.NewAccountAsync(server, "two-sources");
        var personId = await UserIdAsync("two-sources");

        // The manual one, through the API, as an administrator would.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            permissions = new[] { "activity:create" },
        }));

        // The managed one. Written directly because nothing creates one until
        // sign-in exists; from here on it is the provider's, not anybody's.
        var providerId = await NewProviderAsync(admin, "union-provider");
        await using (var context = server.NewContext())
        {
            context.Grants.Add(new Grant
            {
                UserId = personId,
                SourceProviderId = providerId,
                Permissions = """["template:read"]""",
            });
            await context.SaveChangesAsync();
        }

        var mine = await person.GetFromJsonAsync<string[]>("/api/v1/permissions/mine");

        Assert.Contains("activity:create", mine!);
        Assert.Contains("template:read", mine!);
    }

    /// <summary>
    /// **The headline.** An administrator who steps down inside one contest is a
    /// competitor there — the override beats the bypass, which is the one place
    /// in the model where anything beats it.
    /// <para>
    /// Get the order wrong and this fails open: the administrator keeps every
    /// right in the activity they deliberately gave up, and nothing anywhere
    /// says so.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_administrator_does_not_bypass_their_own_override()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await NewActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);
        var adminId = await UserIdAsync(Seeder.DevAdminLogin);

        // Everywhere else, still an administrator.
        var everywhere = await admin.GetFromJsonAsync<string[]>("/api/v1/permissions/mine");
        Assert.Contains("system:administrator", everywhere!);

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = adminId,
            activityId,
            permissions = new[] { "activity:read", "submission:create", "result:read:own" },
            overrideSystem = true,
        }));

        var inside = await admin.GetFromJsonAsync<string[]>(
            $"/api/v1/permissions/mine?activityId={activityId}");

        Assert.Equal(
            new[] { "activity:read", "result:read:own", "submission:create" },
            inside!.OrderBy(k => k, StringComparer.Ordinal));
        Assert.DoesNotContain("system:administrator", inside!);
        Assert.DoesNotContain("submission:read:all", inside!);

        // And it really is scoped: outside that activity nothing changed.
        var outside = await admin.GetFromJsonAsync<string[]>("/api/v1/permissions/mine");
        Assert.Contains("system:administrator", outside!);

        // **They are now stranded, and that is the recorded cost.** Inside this
        // activity they hold no `grant:update`, so they cannot undo it; clearing
        // it needs another manager of the activity or another administrator.
        var stuck = await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = adminId,
            activityId,
            permissions = new[] { "activity:read" },
            overrideSystem = false,
        });
        Assert.Equal(HttpStatusCode.Forbidden, stuck.StatusCode);

        // Cleared here rather than through the API for exactly that reason. The
        // suite shares this administrator, and an override left behind would be
        // one every later test ran under.
        await using var context = server.NewContext();
        await context.Grants
            .Where(g => g.UserId == adminId && g.ActivityId == Guid.Parse(activityId))
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Without the flag, an activity grant <b>adds</b>. The old rule said "minus
    /// the union of their denies"; the denies were removed and the subtraction
    /// outlived them, so this is the test that says the subtraction is gone.
    /// </summary>
    [Fact]
    public async Task An_activity_grant_without_the_flag_takes_nothing_away()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var person = await Sign.NewAccountAsync(server, "additive-person");
        var personId = await UserIdAsync("additive-person");
        var slug = await NewActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            permissions = new[] { "submission:read:all" },
        }));
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            activityId,
            permissions = new[] { "activity:read" },
        }));

        var inside = await person.GetFromJsonAsync<string[]>(
            $"/api/v1/permissions/mine?activityId={activityId}");

        Assert.Contains("activity:read", inside!);
        // The narrower activity grant did not subtract the system one.
        Assert.Contains("submission:read:all", inside!);
    }

    /// <summary>
    /// An administrator's rights are not trimmable from below: nobody else may
    /// set the flag on their grant. Clearing it stays open to anybody who may
    /// edit grants there — it has to, because the flag suppresses the very
    /// permissions its holder would need to undo it.
    /// </summary>
    [Fact]
    public async Task Only_an_administrator_may_set_the_override_on_their_own_grant()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var slug = await NewActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);
        var adminId = await UserIdAsync(Seeder.DevAdminLogin);

        var second = await Sign.NewAccountAsync(server, "second-administrator");
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = await UserIdAsync("second-administrator"),
            permissions = new[] { "system:administrator" },
        }));

        // Another administrator, and still refused.
        var refused = await second.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = adminId,
            activityId,
            permissions = new[] { "activity:read" },
            overrideSystem = true,
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("grant.override.administrator", await Code(refused));

        // The holder may set it on themselves…
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = adminId,
            activityId,
            permissions = new[] { "activity:read" },
            overrideSystem = true,
        }));

        // …and somebody else may take it off, which is the way out of being
        // stranded by it.
        var cleared = await second.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = adminId,
            activityId,
            permissions = new[] { "activity:read" },
            overrideSystem = false,
        });
        await Sign.Succeeded(cleared);
        Assert.False((await cleared.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("overrideSystem").GetBoolean());
    }

    /// <summary>
    /// A managed contribution belongs to its provider's mapping and is rewritten
    /// at every sign-in. Editing or revoking one here would last until that
    /// person next signed in, and a change that silently reverts is worse than
    /// one that is refused.
    /// </summary>
    [Fact]
    public async Task A_managed_contribution_is_not_edited_by_hand()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.NewAccountAsync(server, "managed-person");
        var personId = await UserIdAsync("managed-person");
        var providerId = await NewProviderAsync(admin, "managing-provider");

        Guid managedId;
        await using (var context = server.NewContext())
        {
            var managed = new Grant
            {
                UserId = personId,
                SourceProviderId = providerId,
                Permissions = """["template:read"]""",
            };
            context.Grants.Add(managed);
            await context.SaveChangesAsync();
            managedId = managed.Id;
        }

        var refused = await admin.DeleteAsync($"/api/v1/grants/{managedId}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("grant.managed", await Code(refused));

        // Writing the manual contribution leaves the managed one alone — two
        // rows at system scope, which is the whole point of the source column.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            permissions = new[] { "activity:create" },
        }));

        await using (var context = server.NewContext())
        {
            var systemGrants = await context.Grants
                .Where(g => g.UserId == personId && g.ActivityId == null)
                .ToListAsync();

            Assert.Equal(2, systemGrants.Count);
            Assert.Single(systemGrants, g => g.SourceProviderId == providerId);
            Assert.Single(systemGrants, g => g.SourceProviderId == null);
        }
    }

    /// <summary>
    /// The quiet one, and the reason the override could not simply be a check at
    /// the top of the resolver.
    /// <para>
    /// "Which activities may this person read submissions in" answers <c>null</c>
    /// for "not restricted", which cannot say "everywhere except one". Somebody
    /// holding <c>submission:read:all</c> across the installation, who overrode
    /// themselves down to a competitor in one activity, would otherwise go on
    /// reading every submission in exactly the activity they are competing in.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_override_removes_its_activity_from_what_a_list_may_reach()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        var reader = await Sign.NewAccountAsync(server, "wide-reader");
        var readerId = await UserIdAsync("wide-reader");

        var slug = await NewActivityAsync(admin);
        var activityId = await ActivityIdAsync(slug);

        // Held across the installation, so the answer to "which activities?" is
        // "not restricted" — the case that cannot express an exception.
        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = readerId,
            permissions = new[] { "activity:read", "activity:update" },
        }));

        Assert.Contains(slug, await ManagedSlugsAsync(reader, slug));

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = readerId,
            activityId,
            permissions = new[] { "activity:read" },
            overrideSystem = true,
        }));

        // The one activity they stepped down in drops out, and only that one.
        Assert.DoesNotContain(slug, await ManagedSlugsAsync(reader, slug));
    }

    /// <summary>
    /// The activities this caller may update, narrowed by a search so the answer
    /// does not depend on what else the suite has created.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ManagedSlugsAsync(HttpClient client, string search)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/manager/activities?search={search}");

        return [.. page.GetProperty("items").EnumerateArray()
            .Select(a => a.GetProperty("slug").GetString()!)];
    }

    /// <summary>
    /// The same rule on the notification path.
    /// <para>
    /// An event names data, and the fan-out is supposed to reach only somebody a
    /// request for that data would have been answered for. Somebody who stepped
    /// down to compete in an activity must therefore stop being told what its
    /// staff are told — in a contest, that includes other people's submissions
    /// arriving.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_override_takes_its_holder_out_of_the_activitys_staff_audience()
    {
        var admin = await Sign.InAsync(server, Seeder.DevAdminLogin, Seeder.DevAdminPassword);
        await Sign.NewAccountAsync(server, "stepped-down");
        var personId = await UserIdAsync("stepped-down");
        var slug = await NewActivityAsync(admin);
        var activityId = Guid.Parse(await ActivityIdAsync(slug));

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            permissions = new[] { "submission:read:all" },
        }));

        var audience = server.Services.GetRequiredService<IServiceScopeFactory>();

        using (var scope = audience.CreateScope())
        {
            var readers = await scope.ServiceProvider.GetRequiredService<IEventAudience>()
                .InActivityAsync(activityId, "submission:read:all", default);
            Assert.Contains(personId, readers);
        }

        await Sign.Succeeded(await admin.PostAsJsonAsync("/api/v1/grants", new
        {
            userId = personId,
            activityId = activityId.ToString(),
            permissions = new[] { "activity:read", "submission:create" },
            overrideSystem = true,
        }));

        using (var scope = audience.CreateScope())
        {
            var readers = await scope.ServiceProvider.GetRequiredService<IEventAudience>()
                .InActivityAsync(activityId, "submission:read:all", default);
            Assert.DoesNotContain(personId, readers);
        }
    }

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task<Guid> NewProviderAsync(HttpClient admin, string slug)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/identity/providers", new
        {
            slug,
            displayName = slug,
            issuer = $"https://{slug}.example.invalid",
            clientId = "algojudge",
            clientSecret = "secret-for-the-suite",
        });
        await Sign.Succeeded(created);
        return Guid.Parse((await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!);
    }

    private static async Task<string> NewActivityAsync(HttpClient admin)
    {
        var slug = "RESOLVE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await admin.PostAsJsonAsync("/api/v1/activities", new
        {
            slug,
            name = "Resolution test",
            type = "contest@1",
            rankingType = "icpc",
            timeZone = "Europe/Warsaw",
            joinPolicy = "open",
        });
        await Sign.Succeeded(created);
        return slug;
    }

    private async Task<string> ActivityIdAsync(string slug)
    {
        await using var context = server.NewContext();
        var activity = await context.Activities.FirstAsync(a => a.Slug == slug);
        return activity.Id.ToString();
    }

    private async Task<string> UserIdAsync(string login)
    {
        await using var context = server.NewContext();
        var user = await context.Users.FirstAsync(u => u.UserName == login);
        return user.Id;
    }
}

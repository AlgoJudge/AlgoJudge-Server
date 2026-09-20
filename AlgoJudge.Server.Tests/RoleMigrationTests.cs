using AlgoJudge.Server.Database;
using AlgoJudge.Server.Database.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AlgoJudge.Server.Tests;

/// <summary>
/// What the upgrade to roles does to a database that already has grants.
///
/// <para>
/// <b>0.1.0 shipped before roles existed</b>, so every grant in an installed
/// database holds a copy of a template. The migration turns the ones that never
/// diverged into links and leaves everything else exactly as it is — and getting
/// that wrong in either direction is a permission change nobody asked for, on
/// somebody else's installation.
/// </para>
///
/// <para>
/// <b>Its own database, migrated up to the previous release and no further.</b>
/// Every other test runs against a schema that is already current, which is the
/// one state this cannot start from.
/// </para>
/// </summary>
[Collection("storage")]
public class RoleMigrationTests : IAsyncLifetime
{
    /// <summary>
    /// The released state these start from. <b>A migration id is only nameable
    /// while it exists</b>, and a release squashes its range into one — so this
    /// names the previous release's own migration, which survives, rather than
    /// whichever unreleased one happened to come last.
    /// </summary>
    private const string Previous = "20260907183332_version_0_1_0";

    private PostgreSqlContainer container = null!;

    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("algojudge")
            .WithUsername("algojudge")
            .WithPassword("test")
            .Build();
        await container.StartAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    /// <summary>
    /// An installation as 0.1.0 left it: a manager template, and one grant made
    /// from it that nobody has edited since.
    /// </summary>
    private const string PreRolesManager = """
        INSERT INTO "PermissionTemplates" ("Id", "Name", "Description", "Permissions", "IsBuiltIn", "CreatedAt")
        VALUES ('00000000-0000-0000-0000-0000000000a3', 'manager', null,
                '["activity:read","activity:update","grant:update"]', true, now());

        INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
            "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumberConfirmed",
            "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "IsTemporary", "Anonymized", "CreatedAt")
        VALUES ('runs-a-course', 'runs-a-course', 'RUNS-A-COURSE', null, null, false, null, null, null,
                false, false, true, 0, false, false, now());

        INSERT INTO "Activities" ("Id", "Slug", "Name", "Type", "RankingType", "TimeZone", "Props",
            "JoinPolicy", "Unlisted", "HideEndedSeriesProblems", "ShowGroupMembers", "HasQuestions",
            "ScoreVisibility", "MaxAttachments", "MaxUploadBytes")
        VALUES ('00000000-0000-0000-0000-0000000000d1', 'course', 'Course', 'contest@1', 'icpc',
                'Europe/Warsaw', '{}', 0, false, false, false, false, 0, 1, 1048576);

        INSERT INTO "Grants" ("Id", "UserId", "ActivityId", "SourceProviderId", "OverrideSystem",
            "Permissions", "IsSystem", "CreatedFromTemplate", "State", "CreatedAt")
        VALUES ('00000000-0000-0000-0000-0000000000c5', 'runs-a-course',
                '00000000-0000-0000-0000-0000000000d1', null, false,
                '["activity:read","activity:update","grant:update"]', true, 'manager', 1, now());
        """;

    private ApplicationDbContext Application() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(container.GetConnectionString())
            .Options);

    /// <summary>
    /// The whole upgrade, on one database holding all three kinds of grant.
    /// </summary>
    [Fact]
    public async Task An_untouched_grant_is_linked_and_every_other_kind_is_left_alone()
    {
        await using (var db = Application())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous);
        }

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        // A pre-roles database: a template, and four grants made from it.
        await ExecuteAsync(connection, """
            INSERT INTO "PermissionTemplates" ("Id", "Name", "Description", "Permissions", "IsBuiltIn", "CreatedAt")
            VALUES ('00000000-0000-0000-0000-0000000000a1', 'participant', null,
                    '["activity:read","submission:create","template:read"]', true, now());

            INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail",
                "EmailConfirmed", "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumberConfirmed",
                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "IsTemporary", "Anonymized", "CreatedAt")
            SELECT g.id, g.id, upper(g.id), null, null, false, null, null, null, false, false, true, 0, false, false, now()
            FROM (VALUES ('untouched'), ('edited'), ('reordered'), ('managed')) AS g(id);

            INSERT INTO "IdentityProviders" ("Id", "Slug", "DisplayName", "Issuer", "ClientId", "ClientSecret",
                "Scopes", "Enabled", "ClaimPath", "UnmappedBehavior", "DeletionChannelEnabled", "CreatedAt")
            VALUES ('00000000-0000-0000-0000-0000000000b1', 'sso', 'SSO', 'https://sso.example', 'aj', 'secret',
                    'openid', true, 'groups', 0, false, now());
            """);

        // Four grants: one exactly as the template made it, one edited, one
        // holding the same set in a different order, and one a provider owns.
        await ExecuteAsync(connection, """
            INSERT INTO "Grants" ("Id", "UserId", "ActivityId", "SourceProviderId", "OverrideSystem",
                "Permissions", "IsSystem", "CreatedFromTemplate", "State", "CreatedAt")
            VALUES
              ('00000000-0000-0000-0000-0000000000c1', 'untouched', null, null, false,
               '["activity:read","submission:create","template:read"]', false, 'participant', 1, now()),
              ('00000000-0000-0000-0000-0000000000c2', 'edited', null, null, false,
               '["activity:read","submission:create","template:read","submission:read:all"]', true,
               'participant', 1, now()),
              ('00000000-0000-0000-0000-0000000000c3', 'reordered', null, null, false,
               '["template:read","activity:read","submission:create"]', false, 'participant', 1, now()),
              ('00000000-0000-0000-0000-0000000000c4', 'managed', null,
               '00000000-0000-0000-0000-0000000000b1', false,
               '["activity:read","submission:create","template:read"]', false, null, 1, now());
            """);

        await using (var db = Application())
        {
            await db.Database.MigrateAsync();
        }

        await using var after = Application();

        var role = await after.PermissionRoles.AsNoTracking().SingleAsync(r => r.Name == "participant");
        Assert.Null(role.ActivityId);

        // **The two renamed keys are rewritten in place**, in the role and in
        // every grant that still holds its own set.
        Assert.Contains("role:read", role.Permissions);
        Assert.DoesNotContain("template:", role.Permissions);

        var grants = await after.Grants.AsNoTracking()
            .Include(g => g.Roles)
            .ToDictionaryAsync(g => g.UserId);

        static IReadOnlyList<Guid> Linked(Grant grant) =>
            [.. grant.Roles.Where(r => r.DismissedAt is null).Select(r => r.RoleId)];

        // Never edited: a link now, and the copy is gone from the row.
        Assert.Equal([role.Id], Linked(grants["untouched"]));
        Assert.Equal("[]", grants["untouched"].Permissions);

        // **The same set in a different order is the same set.** Comparing the
        // stored text, or a sorted list against an unsorted one, would leave this
        // grant a copy forever and nobody would know why.
        Assert.Equal([role.Id], Linked(grants["reordered"]));

        // Edited by hand: left exactly as it was, keys renamed and nothing else.
        Assert.Empty(Linked(grants["edited"]));
        Assert.Contains("submission:read:all", grants["edited"].Permissions);
        Assert.Contains("role:read", grants["edited"].Permissions);

        // A provider's contribution stays a copy until that person next signs
        // in: nothing records which claim values produced a stored set, so it
        // cannot be rewritten into links from the outside. The sign-in then
        // replaces it with links to the roles its rules name.
        Assert.Empty(Linked(grants["managed"]));
        Assert.Contains("role:read", grants["managed"].Permissions);
    }

    /// <summary>
    /// The repair the migration of 2026-09-13 could not do for itself.
    ///
    /// <para>
    /// It added <c>role:read</c> and <c>role:manage</c> to the shipped manager
    /// role and <i>then</i> compared every grant with it, so an untouched
    /// manager copy differed from the role by exactly the two keys that had just
    /// been added — and stayed a copy. Every manager an installation already had
    /// was left out of the change the release was about, and out of every
    /// correction to that role since.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manager_grant_from_before_roles_is_linked()
    {
        await using (var db = Application())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous);
        }

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();

        await ExecuteAsync(connection, PreRolesManager);

        await using (var db = Application())
        {
            await db.Database.MigrateAsync();
        }

        await using var after = Application();

        var manager = await after.PermissionRoles.AsNoTracking()
            .SingleAsync(r => r.Name == "manager");
        var grant = await after.Grants.AsNoTracking()
            .Include(g => g.Roles)
            .SingleAsync(g => g.UserId == "runs-a-course");

        Assert.Equal([manager.Id], grant.Roles.Select(r => r.RoleId));
        Assert.Equal("[]", grant.Permissions);

        // The manager role gained `role:manage:activity` where it used to carry
        // `role:manage`: writing the installation's roles is an administrator's
        // now, and this grant follows the role rather than a copy of it.
        Assert.Contains("role:manage:activity", manager.Permissions);
        Assert.DoesNotContain("\"role:manage\"", manager.Permissions);

        // The flag it carried is what its permissions imply, so it is not
        // recorded as somebody's decision — and a role edit can lower it again.
        Assert.True(grant.IsSystem);
        Assert.False(grant.StaffByHand);
    }

    /// <summary>
    /// The release adds two keys to the shipped manager role, and **this is the
    /// first upgrade that can deliver one**. Before roles, a permission added by
    /// a new version reached nobody already enrolled.
    /// </summary>
    [Fact]
    public async Task The_shipped_manager_role_gains_the_keys_the_release_adds()
    {
        await using (var db = Application())
        {
            await db.GetService<IMigrator>().MigrateAsync(Previous);
        }

        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, """
            INSERT INTO "PermissionTemplates" ("Id", "Name", "Description", "Permissions", "IsBuiltIn", "CreatedAt")
            VALUES ('00000000-0000-0000-0000-0000000000a2', 'manager', null,
                    '["activity:read","grant:update"]', true, now());
            """);

        await using (var db = Application())
        {
            await db.Database.MigrateAsync();
        }

        await using var after = Application();
        var manager = await after.PermissionRoles.AsNoTracking().SingleAsync(r => r.Name == "manager");
        Assert.Contains("role:read", manager.Permissions);
        Assert.Contains("grant:update", manager.Permissions);

        // **The writing key it gained is the activity-scoped one.** Held at
        // system scope through a directory group, `role:manage` let anybody in
        // that group rewrite the installation's roles; writing those is an
        // administrator's now, and a manager writes their own activity's.
        Assert.Contains("role:manage:activity", manager.Permissions);
        Assert.DoesNotContain("\"role:manage\"", manager.Permissions);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

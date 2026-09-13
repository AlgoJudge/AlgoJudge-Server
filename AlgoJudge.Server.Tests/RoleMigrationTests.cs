using AlgoJudge.Server.Database;
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
/// <b>Its own database, migrated up to the version before.</b> Every other test
/// runs against a schema that is already current, which is the one state this
/// cannot start from.
/// </para>
/// </summary>
[Collection("storage")]
public class RoleMigrationTests : IAsyncLifetime
{
    private const string Previous = "20260912215309_printoutClaim";

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

        var grants = await after.Grants.AsNoTracking().ToDictionaryAsync(g => g.UserId);

        // Never edited: a link now, and the copy is gone from the row.
        Assert.Equal(role.Id, grants["untouched"].RoleId);
        Assert.Equal("[]", grants["untouched"].Permissions);
        Assert.Null(grants["untouched"].CopiedFromRoleName);

        // **The same set in a different order is the same set.** Comparing the
        // stored text, or a sorted list against an unsorted one, would leave this
        // grant a copy for ever and nobody would know why.
        Assert.Equal(role.Id, grants["reordered"].RoleId);

        // Edited by hand: left exactly as it was, keys renamed and nothing else.
        Assert.Null(grants["edited"].RoleId);
        Assert.Contains("submission:read:all", grants["edited"].Permissions);
        Assert.Contains("role:read", grants["edited"].Permissions);
        Assert.Equal("participant", grants["edited"].CopiedFromRoleName);

        // A provider's contribution stays a copy by design: it is the union of
        // every rule a claim matched, which one link cannot express.
        Assert.Null(grants["managed"].RoleId);
        Assert.Contains("role:read", grants["managed"].Permissions);
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
        Assert.Contains("role:manage", manager.Permissions);
        Assert.Contains("grant:update", manager.Permissions);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

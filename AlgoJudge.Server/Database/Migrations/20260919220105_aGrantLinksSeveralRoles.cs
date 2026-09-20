using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class aGrantLinksSeveralRoles : Migration
    {
        /// <summary>
        /// A grant links several roles, and everything names a role by id.
        /// <para>
        /// The shape first, then the rows, then what the old shape held. The
        /// order is load-bearing: the data steps read columns this drops at the
        /// end, and the unique index and the two check constraints are added
        /// after the rows agree with them rather than before.
        /// </para>
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuiltInKey",
                table: "Roles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "IdentityProviders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                table: "IdentityProviderMappingRules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Target",
                table: "IdentityProviderMappingRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "StaffByHand",
                table: "Grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ActivityEnrollmentRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEnrollmentRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityEnrollmentRoles_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActivityEnrollmentRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GrantRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    DismissedAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrantRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrantRoles_Grants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "Grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GrantRoles_IdentityProviders_SourceProviderId",
                        column: x => x.SourceProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GrantRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "IdentityProviderDefaultRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviderDefaultRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityProviderDefaultRoles_IdentityProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IdentityProviderDefaultRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_BuiltInKey",
                table: "Roles",
                column: "BuiltInKey",
                unique: true,
                filter: "\"BuiltInKey\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_BuiltInIsGlobal",
                table: "Roles",
                sql: "\"BuiltInKey\" IS NULL OR \"ActivityId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_Kind",
                table: "IdentityProviders",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderMappingRules_RoleId",
                table: "IdentityProviderMappingRules",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEnrollmentRoles_ActivityId_Slot_RoleId",
                table: "ActivityEnrollmentRoles",
                columns: new[] { "ActivityId", "Slot", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEnrollmentRoles_RoleId",
                table: "ActivityEnrollmentRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_GrantRoles_GrantId_RoleId",
                table: "GrantRoles",
                columns: new[] { "GrantId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GrantRoles_RoleId",
                table: "GrantRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_GrantRoles_SourceProviderId",
                table: "GrantRoles",
                column: "SourceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderDefaultRoles_ProviderId_RoleId",
                table: "IdentityProviderDefaultRoles",
                columns: new[] { "ProviderId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderDefaultRoles_RoleId",
                table: "IdentityProviderDefaultRoles",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_IdentityProviderMappingRules_Roles_RoleId",
                table: "IdentityProviderMappingRules",
                column: "RoleId",
                principalTable: "Roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ── What the shape change means for rows already here ──────────
            //
            // In this order, because each step reads what the one before it
            // wrote. A step that only ever touches rows an installation has is
            // silent on a fresh database, which is why none of them is guarded.

            // **1. `role:manage` becomes two keys.** It meant "write the roles
            // at the scope you hold this" and now means the installation's
            // alone; an activity's are written with `role:manage:activity`.
            //
            // Three cases, and the difference matters. An activity's role and an
            // activity grant meant the activity all along, so the key is
            // replaced. The shipped `manager` role is the whole point of the
            // split — a directory group mapped onto it granted the installation's
            // roles to everybody in that group — so it is replaced there too. A
            // role an installation invented keeps `role:manage` and gains the
            // activity key beside it, because it had both reaches and must keep
            // both.
            //
            // **Every built-in carrying the key, not the one named `manager`.**
            // A shipped role can be renamed, and matching the name left a
            // renamed one holding the installation-wide key — which is the
            // reach the split exists to take away. Only the shipped manager
            // role carries `role:manage` among the three, so the flag alone
            // identifies it.
            migrationBuilder.Sql("""
                UPDATE "Roles"
                SET "Permissions" = ("Permissions" - 'role:manage') || '["role:manage:activity"]'::jsonb
                WHERE "Permissions" @> '["role:manage"]'::jsonb
                  AND ("ActivityId" IS NOT NULL OR "IsBuiltIn");

                UPDATE "Roles"
                SET "Permissions" = "Permissions" || '["role:manage:activity"]'::jsonb
                WHERE "Permissions" @> '["role:manage"]'::jsonb
                  AND NOT "Permissions" @> '["role:manage:activity"]'::jsonb;

                UPDATE "Grants"
                SET "Permissions" = ("Permissions" - 'role:manage') || '["role:manage:activity"]'::jsonb
                WHERE "ActivityId" IS NOT NULL AND "Permissions" @> '["role:manage"]'::jsonb;
                """);

            // **2. The shipped three get their key.** Every enrollment path
            // resolves them by it from here on, so a rename is a rename and
            // nothing more. Matched on the name they were seeded with, which is
            // the only evidence there is; a role an installation renamed before
            // this runs is adopted by the seeder at the next start instead.
            migrationBuilder.Sql("""
                UPDATE "Roles" SET "BuiltInKey" = "Name"
                WHERE "IsBuiltIn" AND "ActivityId" IS NULL
                  AND "Name" IN ('participant', 'manager', 'admin');
                """);

            // **3. The copies that never diverged are linked.** A grant made
            // before roles existed, or made by a launch, holds a copy of what it
            // was given. Where that copy is still exactly the role it came from,
            // it becomes a link and the copy is cleared — so a correction to the
            // role reaches that person like everybody else.
            //
            // Compared as a **set**, and against the role's keys **minus the
            // ones later migrations added to it**: `role:read` and `role:manage`
            // arrived on 2026-09-13 and `user:read:all` on 2026-09-14, so an
            // untouched manager copy differs from today's role by exactly those
            // and would otherwise stay a copy forever. That is the defect the
            // migration of 2026-09-13 left behind: it added the keys first and
            // compared afterwards, so it linked no manager grant at all.
            //
            // A hand-edited grant is left alone. That edit was somebody's
            // decision about one person, and the role was not.
            migrationBuilder.Sql("""
                WITH shipped AS (
                    SELECT r."Id", r."Name",
                           (SELECT COALESCE(jsonb_agg(k ORDER BY k), '[]'::jsonb)
                              FROM jsonb_array_elements_text(r."Permissions") AS k
                             WHERE k NOT IN ('role:read', 'role:manage',
                                             'role:manage:activity', 'user:read:all')) AS before
                      FROM "Roles" r
                     WHERE r."ActivityId" IS NULL AND r."BuiltInKey" IS NOT NULL
                )
                UPDATE "Grants" g
                SET "RoleId" = s."Id", "Permissions" = '[]'::jsonb
                FROM shipped s
                WHERE g."RoleId" IS NULL
                  AND g."CopiedFromRoleName" = s."Name"
                  AND (
                        (SELECT COALESCE(jsonb_agg(DISTINCT k), '[]'::jsonb)
                           FROM jsonb_array_elements_text(g."Permissions") AS k)
                        @> s.before
                        AND s.before @>
                        (SELECT COALESCE(jsonb_agg(DISTINCT k), '[]'::jsonb)
                           FROM jsonb_array_elements_text(g."Permissions") AS k)
                      );
                """);

            // **4. Every link a grant already had becomes a row.** The source
            // travels onto the link, because an activity grant no longer carries
            // one: one grant per person per activity, whoever wrote it, and what
            // a platform asserted marked on the roles it added.
            migrationBuilder.Sql("""
                INSERT INTO "GrantRoles" ("Id", "GrantId", "RoleId", "SourceProviderId", "AddedAt")
                SELECT gen_random_uuid(), g."Id", g."RoleId", g."SourceProviderId", g."CreatedAt"
                  FROM "Grants" g
                 WHERE g."RoleId" IS NOT NULL;
                """);

            // **5. The staff flag splits in two.** It held what the permissions
            // imply and what a person decided at once, which is why a role edit
            // could raise it and nothing could lower it again. What is not
            // implied by the permissions was somebody's decision, and that is
            // what the new column keeps.
            migrationBuilder.Sql("""
                UPDATE "Grants" g
                SET "StaffByHand" = TRUE
                WHERE g."IsSystem"
                  AND NOT EXISTS (
                      SELECT 1
                        FROM jsonb_array_elements_text(
                                 COALESCE((SELECT r."Permissions" FROM "Roles" r WHERE r."Id" = g."RoleId"),
                                          '[]'::jsonb) || g."Permissions") AS k
                       WHERE k NOT IN ('activity:read', 'submission:read:own', 'submission:create',
                                       'result:read:own', 'question:read:own', 'question:create',
                                       'ranking:read', 'printout:request', 'trial:run')
                  );
                """);

            // **6. A mapping rule names a role by id.** By name it was a
            // reference nothing enforced: names are unique only within a scope,
            // so renaming an activity's role rewrote the rules of every provider
            // that named an installation role of the same name.
            //
            // **A rule naming a role that is not there is deleted.** It grants
            // nothing today, and under `deny` it is the difference between a
            // sign-in admitted with an empty contribution and one refused. The
            // refusal is the honest answer, and it is a release-note line.
            migrationBuilder.Sql("""
                UPDATE "IdentityProviderMappingRules" m
                SET "RoleId" = r."Id"
                FROM "Roles" r
                WHERE r."ActivityId" IS NULL AND r."Name" = m."RoleName";

                DELETE FROM "IdentityProviderMappingRules" WHERE "RoleId" IS NULL;
                """);

            // **7. The default role becomes a list, by id.** Looked up by name
            // and without a scope filter, it could pick an activity's role of
            // the same name and hand its permissions out installation-wide.
            migrationBuilder.Sql("""
                INSERT INTO "IdentityProviderDefaultRoles" ("Id", "ProviderId", "RoleId")
                SELECT gen_random_uuid(), p."Id", r."Id"
                  FROM "IdentityProviders" p
                  JOIN "Roles" r ON r."ActivityId" IS NULL AND r."Name" = p."DefaultRoleName"
                 WHERE p."DefaultRoleName" IS NOT NULL;
                """);

            // **8. An activity's two roles become two sets.** An activity may
            // enroll into several now — the shipped role and one of its own
            // beside it — and a slot with nothing in it means the shipped one.
            migrationBuilder.Sql("""
                INSERT INTO "ActivityEnrollmentRoles" ("Id", "ActivityId", "Slot", "RoleId")
                SELECT gen_random_uuid(), a."Id", 0, a."ParticipantRoleId"
                  FROM "Activities" a WHERE a."ParticipantRoleId" IS NOT NULL
                UNION ALL
                SELECT gen_random_uuid(), a."Id", 1, a."ManagerRoleId"
                  FROM "Activities" a WHERE a."ManagerRoleId" IS NOT NULL;
                """);

            // **9. A source belongs to system scope.** An activity has one grant
            // per person whoever wrote it, so a source on the row claimed the
            // whole membership for one platform — and made the panel refuse to
            // edit a membership a launch had created.
            migrationBuilder.Sql("""
                UPDATE "Grants" SET "SourceProviderId" = NULL WHERE "ActivityId" IS NOT NULL;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Activities_Roles_ManagerRoleId",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_Activities_Roles_ParticipantRoleId",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_Grants_Roles_RoleId",
                table: "Grants");

            migrationBuilder.DropIndex(
                name: "IX_IdentityProviderMappingRules_ProviderId_ClaimValue",
                table: "IdentityProviderMappingRules");

            migrationBuilder.DropIndex(
                name: "IX_Grants_RoleId",
                table: "Grants");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ManagerRoleId",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ParticipantRoleId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "DefaultRoleName",
                table: "IdentityProviders");

            migrationBuilder.DropColumn(
                name: "RoleName",
                table: "IdentityProviderMappingRules");

            migrationBuilder.DropColumn(
                name: "CopiedFromRoleName",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "ManagerRoleId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ParticipantRoleId",
                table: "Activities");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderMappingRules_ProviderId_ClaimValue_Target_R~",
                table: "IdentityProviderMappingRules",
                columns: new[] { "ProviderId", "ClaimValue", "Target", "RoleId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MappingRules_RoleForRoleTarget",
                table: "IdentityProviderMappingRules",
                sql: "(\"Target\" = 0 AND \"RoleId\" IS NOT NULL) OR (\"Target\" <> 0 AND \"RoleId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Grants_SourceIsSystemScope",
                table: "Grants",
                sql: "\"ActivityId\" IS NULL OR \"SourceProviderId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultRoleName",
                table: "IdentityProviders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoleName",
                table: "IdentityProviderMappingRules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CopiedFromRoleName",
                table: "Grants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                table: "Grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ManagerRoleId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParticipantRoleId",
                table: "Activities",
                type: "uuid",
                nullable: true);

            // **A grant held one role, so the links have to collapse into one.**
            // Where there is exactly one it is put back as the link it was;
            // where there are several the union is folded into the grant's own
            // entries instead, because the old shape has nowhere else to put it.
            //
            // That is a **widening**: those keys stop tracking the roles they
            // came from, so a later edit to one of those roles no longer reaches
            // that person. Rolling back is not free, and this is what it costs.
            migrationBuilder.Sql("""
                UPDATE "Grants" g
                SET "RoleId" = (
                        SELECT gr."RoleId" FROM "GrantRoles" gr
                         WHERE gr."GrantId" = g."Id" AND gr."DismissedAt" IS NULL
                         LIMIT 1)
                WHERE (SELECT count(*) FROM "GrantRoles" gr
                        WHERE gr."GrantId" = g."Id" AND gr."DismissedAt" IS NULL) = 1;

                UPDATE "Grants" g
                SET "Permissions" = (
                        SELECT COALESCE(jsonb_agg(DISTINCT k), '[]'::jsonb)
                          FROM (
                                SELECT jsonb_array_elements_text(r."Permissions") AS k
                                  FROM "GrantRoles" gr
                                  JOIN "Roles" r ON r."Id" = gr."RoleId"
                                 WHERE gr."GrantId" = g."Id" AND gr."DismissedAt" IS NULL
                                 UNION ALL
                                SELECT jsonb_array_elements_text(g."Permissions")
                               ) AS keys)
                WHERE (SELECT count(*) FROM "GrantRoles" gr
                        WHERE gr."GrantId" = g."Id" AND gr."DismissedAt" IS NULL) > 1;

                UPDATE "IdentityProviderMappingRules" m
                SET "RoleName" = COALESCE((SELECT r."Name" FROM "Roles" r WHERE r."Id" = m."RoleId"), '');

                UPDATE "IdentityProviders" p
                SET "DefaultRoleName" = (
                        SELECT r."Name" FROM "IdentityProviderDefaultRoles" d
                          JOIN "Roles" r ON r."Id" = d."RoleId"
                         WHERE d."ProviderId" = p."Id" LIMIT 1);

                UPDATE "Activities" a
                SET "ParticipantRoleId" = (
                        SELECT e."RoleId" FROM "ActivityEnrollmentRoles" e
                         WHERE e."ActivityId" = a."Id" AND e."Slot" = 0 LIMIT 1),
                    "ManagerRoleId" = (
                        SELECT e."RoleId" FROM "ActivityEnrollmentRoles" e
                         WHERE e."ActivityId" = a."Id" AND e."Slot" = 1 LIMIT 1);

                UPDATE "Grants"
                SET "IsSystem" = TRUE
                WHERE "StaffByHand";
                """);

            migrationBuilder.DropTable(
                name: "ActivityEnrollmentRoles");

            migrationBuilder.DropTable(
                name: "GrantRoles");

            migrationBuilder.DropTable(
                name: "IdentityProviderDefaultRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_IdentityProviderMappingRules_Roles_RoleId",
                table: "IdentityProviderMappingRules");

            migrationBuilder.DropIndex(
                name: "IX_Roles_BuiltInKey",
                table: "Roles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_BuiltInIsGlobal",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_IdentityProviders_Kind",
                table: "IdentityProviders");

            migrationBuilder.DropIndex(
                name: "IX_IdentityProviderMappingRules_ProviderId_ClaimValue_Target_R~",
                table: "IdentityProviderMappingRules");

            migrationBuilder.DropIndex(
                name: "IX_IdentityProviderMappingRules_RoleId",
                table: "IdentityProviderMappingRules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MappingRules_RoleForRoleTarget",
                table: "IdentityProviderMappingRules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Grants_SourceIsSystemScope",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "BuiltInKey",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "IdentityProviders");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "IdentityProviderMappingRules");

            migrationBuilder.DropColumn(
                name: "Target",
                table: "IdentityProviderMappingRules");

            migrationBuilder.DropColumn(
                name: "StaffByHand",
                table: "Grants");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderMappingRules_ProviderId_ClaimValue",
                table: "IdentityProviderMappingRules",
                columns: new[] { "ProviderId", "ClaimValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Grants_RoleId",
                table: "Grants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ManagerRoleId",
                table: "Activities",
                column: "ManagerRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ParticipantRoleId",
                table: "Activities",
                column: "ParticipantRoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Roles_ManagerRoleId",
                table: "Activities",
                column: "ManagerRoleId",
                principalTable: "Roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Roles_ParticipantRoleId",
                table: "Activities",
                column: "ParticipantRoleId",
                principalTable: "Roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Grants_Roles_RoleId",
                table: "Grants",
                column: "RoleId",
                principalTable: "Roles",
                principalColumn: "Id");
        }
    }
}

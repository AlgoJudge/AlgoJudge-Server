using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Everything added since 0.1.0, as one migration: printouts and the claim
    /// on them, permission templates becoming roles, a manager naming a person,
    /// the American spelling of two <c>AccountMerges</c> columns, and a grant
    /// linking several roles.
    /// <para>
    /// <b>The statements are the six migrations' own, in their own order.</b>
    /// Half of them rewrite rows rather than shape &#8212; they carry an
    /// installation's permission keys across the <c>template:</c> to <c>role:</c>
    /// rename, add what the release grants the shipped <c>manager</c> role, and
    /// link the grants that never diverged from the role they were made from.
    /// A model differ produces none of that, and renders both renames as a drop
    /// and a create, so this migration is assembled rather than regenerated.
    /// </para>
    /// </summary>
    public partial class version_0_2_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 20260912185626_printouts ----
            migrationBuilder.DropCheckConstraint(
                name: "CK_FileReferences_OwnerKindMatches",
                table: "FileReferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FileReferences_SingleOwner",
                table: "FileReferences");

            migrationBuilder.AddColumn<Guid>(
                name: "PrintoutId",
                table: "FileReferences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasPrintouts",
                table: "Activities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Printouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByUserId = table.Column<string>(type: "text", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ResolvedByUserId = table.Column<string>(type: "text", nullable: true),
                    SourceDisposedAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Printouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Printouts_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Printouts_ActivityGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ActivityGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Printouts_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Printouts_AspNetUsers_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Printouts_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_PrintoutId",
                table: "FileReferences",
                column: "PrintoutId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileReferences_OwnerKindMatches",
                table: "FileReferences",
                sql: "(\"OwnerKind\" = 0 AND \"ProblemVersionId\" IS NOT NULL) OR (\"OwnerKind\" = 1 AND \"ActivityId\" IS NOT NULL) OR (\"OwnerKind\" = 2 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 3 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 4 AND \"RunnerId\" IS NOT NULL) OR (\"OwnerKind\" = 5 AND \"SubmissionId\" IS NOT NULL) OR (\"OwnerKind\" = 6 AND \"EvaluationJobId\" IS NOT NULL) OR (\"OwnerKind\" = 7 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 8 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 9 AND \"PrintoutId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileReferences_SingleOwner",
                table: "FileReferences",
                sql: "num_nonnulls(\"ProblemVersionId\", \"ActivityId\", \"SubmissionId\", \"EvaluationJobId\", \"RunnerId\", \"InstanceId\", \"PrintoutId\") = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Printouts_ActivityId_RequestedAt",
                table: "Printouts",
                columns: new[] { "ActivityId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Printouts_GroupId",
                table: "Printouts",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Printouts_RequestedByUserId",
                table: "Printouts",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Printouts_ResolvedByUserId",
                table: "Printouts",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Printouts_State_RequestedAt",
                table: "Printouts",
                columns: new[] { "State", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Printouts_SubmissionId",
                table: "Printouts",
                column: "SubmissionId");

            migrationBuilder.AddForeignKey(
                name: "FK_FileReferences_Printouts_PrintoutId",
                table: "FileReferences",
                column: "PrintoutId",
                principalTable: "Printouts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);


            // ---- 20260912215309_printoutClaim ----
            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "Printouts",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByUserId",
                table: "Printouts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Printouts_ClaimedByUserId",
                table: "Printouts",
                column: "ClaimedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Printouts_AspNetUsers_ClaimedByUserId",
                table: "Printouts",
                column: "ClaimedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);


            // ---- 20260913142136_rolesInsteadOfTemplates ----
            migrationBuilder.RenameTable(
                name: "PermissionTemplates",
                newName: "Roles");

            // Postgres renames a table without renaming what hangs off it, and
            // the model snapshot expects the new names.
            migrationBuilder.Sql(
                "ALTER TABLE \"Roles\" RENAME CONSTRAINT \"PK_PermissionTemplates\" TO \"PK_Roles\";");

            migrationBuilder.DropIndex(
                name: "IX_PermissionTemplates_Name",
                table: "Roles");

            migrationBuilder.AddColumn<Guid>(
                name: "ActivityId",
                table: "Roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.RenameColumn(
                name: "TemplateName",
                table: "IdentityProviderMappingRules",
                newName: "RoleName");

            migrationBuilder.RenameColumn(
                name: "DefaultTemplateName",
                table: "IdentityProviders",
                newName: "DefaultRoleName");

            migrationBuilder.RenameColumn(
                name: "CreatedFromTemplate",
                table: "Grants",
                newName: "CopiedFromRoleName");

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

            migrationBuilder.CreateIndex(
                name: "IX_Roles_ActivityId_Name",
                table: "Roles",
                columns: new[] { "ActivityId", "Name" },
                unique: true,
                filter: "\"ActivityId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name_Global",
                table: "Roles",
                column: "Name",
                unique: true,
                filter: "\"ActivityId\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Activities_ActivityId",
                table: "Roles",
                column: "ActivityId",
                principalTable: "Activities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

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

            // `template:read` and `template:manage` became `role:read` and
            // `role:manage`. The opening quote anchors the prefix, and those two
            // are the only keys that ever carried it.
            migrationBuilder.Sql(
                "UPDATE \"Roles\" SET \"Permissions\" = "
                + "replace(\"Permissions\"::text, '\"template:', '\"role:')::jsonb "
                + "WHERE \"Permissions\"::text LIKE '%\"template:%';");
            migrationBuilder.Sql(
                "UPDATE \"Grants\" SET \"Permissions\" = "
                + "replace(\"Permissions\"::text, '\"template:', '\"role:')::jsonb "
                + "WHERE \"Permissions\"::text LIKE '%\"template:%';");

            // The shipped `manager` role gains the two keys the release adds to
            // it. **This is the correction the whole change exists to make
            // possible** — before it, a permission added by a new version reached
            // nobody already enrolled, and an installation had to re-issue every
            // grant by hand. Written once here rather than by the seeder, which
            // must never rewrite a role an installation has since edited.
            migrationBuilder.Sql(
                "UPDATE \"Roles\" SET \"Permissions\" = \"Permissions\" || '[\"role:read\"]'::jsonb "
                + "WHERE \"ActivityId\" IS NULL AND \"IsBuiltIn\" AND \"Name\" = 'manager' "
                + "AND NOT jsonb_exists(\"Permissions\", 'role:read');");
            migrationBuilder.Sql(
                "UPDATE \"Roles\" SET \"Permissions\" = \"Permissions\" || '[\"role:manage\"]'::jsonb "
                + "WHERE \"ActivityId\" IS NULL AND \"IsBuiltIn\" AND \"Name\" = 'manager' "
                + "AND NOT jsonb_exists(\"Permissions\", 'role:manage');");

            // **Link the grants that never diverged, and only those.** A grant
            // whose set is exactly the role it was made from was never edited, so
            // pointing it at that role changes nothing today and everything the
            // next time the role is corrected. One that differs by a single key
            // is somebody's decision about one person and is left a copy —
            // together with every provider contribution, which stays a copy by
            // design because a claim may match several rules at once.
            //
            // Compared as sets: `DISTINCT` so a duplicated entry on one side is
            // not a difference, sorted so order is not one either, and
            // `IS NOT DISTINCT FROM` so two empty sets match.
            migrationBuilder.Sql(
                "UPDATE \"Grants\" g SET \"RoleId\" = r.\"Id\", \"Permissions\" = '[]'::jsonb, "
                + "\"CopiedFromRoleName\" = NULL "
                + "FROM \"Roles\" r "
                + "WHERE r.\"ActivityId\" IS NULL AND g.\"SourceProviderId\" IS NULL "
                + "AND g.\"CopiedFromRoleName\" = r.\"Name\" "
                + "AND (SELECT array_agg(DISTINCT x ORDER BY x) "
                + "FROM jsonb_array_elements_text(g.\"Permissions\") x) IS NOT DISTINCT FROM "
                + "(SELECT array_agg(DISTINCT y ORDER BY y) "
                + "FROM jsonb_array_elements_text(r.\"Permissions\") y);");


            // ---- 20260914173744_aManagerMayNameAPerson ----
            // The installation's own `manager` role, and only that one: an
            // activity's role of the same name is somebody's local decision.
            migrationBuilder.Sql(
                """
                UPDATE "Roles" SET "Permissions" = "Permissions" || '["user:read:all"]'::jsonb
                WHERE "ActivityId" IS NULL AND "IsBuiltIn" AND "Name" = 'manager'
                  AND NOT jsonb_exists("Permissions", 'user:read:all');
                """);


            // ---- 20260918195016_americanSpelling ----
            migrationBuilder.RenameColumn(
                name: "SourceAnonymisedAt",
                table: "AccountMerges",
                newName: "SourceAnonymizedAt");

            migrationBuilder.RenameColumn(
                name: "AnonymiseAfter",
                table: "AccountMerges",
                newName: "AnonymizeAfter");

            migrationBuilder.RenameIndex(
                name: "IX_AccountMerges_SourceAnonymisedAt_AnonymiseAfter",
                table: "AccountMerges",
                newName: "IX_AccountMerges_SourceAnonymizedAt_AnonymizeAfter");


            // ---- 20260919220105_aGrantLinksSeveralRoles ----
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
            // ---- 20260919220105_aGrantLinksSeveralRoles ----
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


            // ---- 20260918195016_americanSpelling ----
            migrationBuilder.RenameColumn(
                name: "SourceAnonymizedAt",
                table: "AccountMerges",
                newName: "SourceAnonymisedAt");

            migrationBuilder.RenameColumn(
                name: "AnonymizeAfter",
                table: "AccountMerges",
                newName: "AnonymiseAfter");

            migrationBuilder.RenameIndex(
                name: "IX_AccountMerges_SourceAnonymizedAt_AnonymizeAfter",
                table: "AccountMerges",
                newName: "IX_AccountMerges_SourceAnonymisedAt_AnonymiseAfter");


            // ---- 20260914173744_aManagerMayNameAPerson ----
            // Exactly what `Up` added, so a rollback leaves the role as the
            // previous version shipped it.
            //
            // **It gets one case wrong, knowingly**: an installation that had
            // already put this key on the shipped manager role by hand loses it
            // here. The column records no provenance, so the alternative is
            // leaving a right behind on every rollback, and that is the worse
            // half of the trade for a key that reads every account.
            migrationBuilder.Sql(
                """
                UPDATE "Roles" SET "Permissions" = "Permissions" - 'user:read:all'
                WHERE "ActivityId" IS NULL AND "IsBuiltIn" AND "Name" = 'manager';
                """);


            // ---- 20260913142136_rolesInsteadOfTemplates ----
            // Put back what a link was standing in for, before the link can go.
            // Anything else would be a silent demotion of everybody the upgrade
            // had tidied up.
            migrationBuilder.Sql(
                "UPDATE \"Grants\" g SET \"Permissions\" = COALESCE(("
                + "SELECT jsonb_agg(DISTINCT u.x) FROM ("
                + "SELECT jsonb_array_elements_text(r.\"Permissions\") AS x "
                + "UNION SELECT jsonb_array_elements_text(g.\"Permissions\")) u), '[]'::jsonb), "
                + "\"CopiedFromRoleName\" = r.\"Name\" "
                + "FROM \"Roles\" r WHERE g.\"RoleId\" = r.\"Id\";");

            // An activity's own role has nowhere to live in the old shape. Its
            // grants have just been given its permissions outright, so nothing is
            // lost but the ability to correct them in one place.
            migrationBuilder.Sql("DELETE FROM \"Roles\" WHERE \"ActivityId\" IS NOT NULL;");

            migrationBuilder.Sql(
                "UPDATE \"Roles\" SET \"Permissions\" = "
                + "replace(\"Permissions\"::text, '\"role:', '\"template:')::jsonb "
                + "WHERE \"Permissions\"::text LIKE '%\"role:%';");
            migrationBuilder.Sql(
                "UPDATE \"Grants\" SET \"Permissions\" = "
                + "replace(\"Permissions\"::text, '\"role:', '\"template:')::jsonb "
                + "WHERE \"Permissions\"::text LIKE '%\"role:%';");

            migrationBuilder.DropForeignKey(
                name: "FK_Activities_Roles_ManagerRoleId",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_Activities_Roles_ParticipantRoleId",
                table: "Activities");

            migrationBuilder.DropForeignKey(
                name: "FK_Grants_Roles_RoleId",
                table: "Grants");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Activities_ActivityId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Grants_RoleId",
                table: "Grants");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ManagerRoleId",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ParticipantRoleId",
                table: "Activities");

            migrationBuilder.DropIndex(
                name: "IX_Roles_ActivityId_Name",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_Name_Global",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "ManagerRoleId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ParticipantRoleId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "ActivityId",
                table: "Roles");

            migrationBuilder.RenameColumn(
                name: "RoleName",
                table: "IdentityProviderMappingRules",
                newName: "TemplateName");

            migrationBuilder.RenameColumn(
                name: "DefaultRoleName",
                table: "IdentityProviders",
                newName: "DefaultTemplateName");

            migrationBuilder.RenameColumn(
                name: "CopiedFromRoleName",
                table: "Grants",
                newName: "CreatedFromTemplate");

            migrationBuilder.RenameTable(
                name: "Roles",
                newName: "PermissionTemplates");

            migrationBuilder.Sql(
                "ALTER TABLE \"PermissionTemplates\" "
                + "RENAME CONSTRAINT \"PK_Roles\" TO \"PK_PermissionTemplates\";");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionTemplates_Name",
                table: "PermissionTemplates",
                column: "Name",
                unique: true);


            // ---- 20260912215309_printoutClaim ----
            migrationBuilder.DropForeignKey(
                name: "FK_Printouts_AspNetUsers_ClaimedByUserId",
                table: "Printouts");

            migrationBuilder.DropIndex(
                name: "IX_Printouts_ClaimedByUserId",
                table: "Printouts");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "Printouts");

            migrationBuilder.DropColumn(
                name: "ClaimedByUserId",
                table: "Printouts");


            // ---- 20260912185626_printouts ----
            migrationBuilder.DropForeignKey(
                name: "FK_FileReferences_Printouts_PrintoutId",
                table: "FileReferences");

            migrationBuilder.DropTable(
                name: "Printouts");

            migrationBuilder.DropIndex(
                name: "IX_FileReferences_PrintoutId",
                table: "FileReferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FileReferences_OwnerKindMatches",
                table: "FileReferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_FileReferences_SingleOwner",
                table: "FileReferences");

            migrationBuilder.DropColumn(
                name: "PrintoutId",
                table: "FileReferences");

            migrationBuilder.DropColumn(
                name: "HasPrintouts",
                table: "Activities");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileReferences_OwnerKindMatches",
                table: "FileReferences",
                sql: "(\"OwnerKind\" = 0 AND \"ProblemVersionId\" IS NOT NULL) OR (\"OwnerKind\" = 1 AND \"ActivityId\" IS NOT NULL) OR (\"OwnerKind\" = 2 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 3 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 4 AND \"RunnerId\" IS NOT NULL) OR (\"OwnerKind\" = 5 AND \"SubmissionId\" IS NOT NULL) OR (\"OwnerKind\" = 6 AND \"EvaluationJobId\" IS NOT NULL) OR (\"OwnerKind\" = 7 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 8 AND \"InstanceId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileReferences_SingleOwner",
                table: "FileReferences",
                sql: "num_nonnulls(\"ProblemVersionId\", \"ActivityId\", \"SubmissionId\", \"EvaluationJobId\", \"RunnerId\", \"InstanceId\") = 1");
        }
    }
}

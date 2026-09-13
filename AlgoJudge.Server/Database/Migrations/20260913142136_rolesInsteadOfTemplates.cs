using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// A grant points at a role instead of holding a copy of a template.
    /// <para>
    /// <b>The table is renamed, never dropped and recreated.</b> An installation
    /// may have edited what its managers hold, and those rows are the only record
    /// of it — scaffolding wrote a drop, which would have thrown that away on
    /// upgrade.
    /// </para>
    /// <para>
    /// Two steps here change data rather than shape, and both are deliberate: the
    /// two renamed permission keys are rewritten inside every stored set, and
    /// every grant whose permissions still match exactly the template it was made
    /// from is turned into a link. A grant that was edited by hand is left as a
    /// copy, because that edit was somebody's decision about one person.
    /// </para>
    /// </summary>
    public partial class rolesInsteadOfTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}

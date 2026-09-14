using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// The shipped <c>manager</c> role gains <c>user:read:all</c>.
    /// <para>
    /// No schema moves. The role already carried <c>activity:enroll</c> and
    /// <c>grant:update</c>, and neither could be spent: enrolling somebody by
    /// hand means naming them, the only lookup that turns a person into an id is
    /// <c>GET /users</c>, and that asks for this key at system scope — which an
    /// activity grant never reaches. Both pickers came back empty and there was
    /// nobody to choose.
    /// </para>
    /// <para>
    /// Written here rather than by the seeder, for the reason the roles release
    /// was made for: the seeder must never rewrite a role an installation has
    /// since edited, so a key added by a new version would otherwise reach
    /// nobody already enrolled.
    /// </para>
    /// </summary>
    public partial class aManagerMayNameAPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The installation's own `manager` role, and only that one: an
            // activity's role of the same name is somebody's local decision.
            migrationBuilder.Sql(
                """
                UPDATE "Roles" SET "Permissions" = "Permissions" || '["user:read:all"]'::jsonb
                WHERE "ActivityId" IS NULL AND "IsBuiltIn" AND "Name" = 'manager'
                  AND NOT jsonb_exists("Permissions", 'user:read:all');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}

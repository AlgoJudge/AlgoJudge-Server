using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class InstanceShowLocalSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // **`true`, not the `false` EF generates for a bool.** This column
            // decides whether the sign-in screen shows the password form, and
            // every installation that existed before it was added has been
            // showing one. Taking the generated default would hide the form on
            // upgrade, everywhere, without anybody asking for it — and the people
            // it hides it from include the administrator trying to work out why.
            migrationBuilder.AddColumn<bool>(
                name: "ShowLocalSignIn",
                table: "Instance",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowLocalSignIn",
                table: "Instance");
        }
    }
}

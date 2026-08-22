using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// A version says which problem it is, where the type needs telling.
    ///
    /// <para>
    /// Not the `Config` the previous migration dropped, under a new name. That
    /// was the middle of a three-layer configuration chain and the chain is
    /// still two. This carries **identity**: `uva@1` needs the archive's problem
    /// number, which is a fact about the problem rather than about one
    /// activity's use of it, and copying it onto every assignment would be one
    /// number written wherever the problem is attached.
    /// </para>
    ///
    /// <para>
    /// Nothing is back-filled. The column that was dropped held limits, which is
    /// not what this holds; a `uva@1` problem imported before today needs its
    /// number set, and the Runner says so by name when it does not find one.
    /// </para>
    /// </summary>
    public partial class ProblemVersionProps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Props",
                table: "ProblemVersions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Props",
                table: "ProblemVersions");
        }
    }
}

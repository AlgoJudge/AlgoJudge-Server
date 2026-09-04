using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class JobReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Releases",
                table: "EvaluationJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Releases",
                table: "EvaluationJobs");
        }
    }
}

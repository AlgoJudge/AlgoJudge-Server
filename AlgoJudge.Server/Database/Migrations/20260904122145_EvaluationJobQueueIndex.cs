using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationJobQueueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EvaluationJobs_State_CreatedAt",
                table: "EvaluationJobs");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_State_CreatedAt",
                table: "EvaluationJobs",
                columns: new[] { "State", "CreatedAt" },
                filter: "\"State\" < 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EvaluationJobs_State_CreatedAt",
                table: "EvaluationJobs");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_State_CreatedAt",
                table: "EvaluationJobs",
                columns: new[] { "State", "CreatedAt" });
        }
    }
}

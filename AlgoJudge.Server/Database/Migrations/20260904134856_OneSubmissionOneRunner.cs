using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class OneSubmissionOneRunner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedAt",
                table: "EvaluationJobs",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Refunds",
                table: "EvaluationJobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_SubmissionId",
                table: "EvaluationJobs",
                column: "SubmissionId",
                filter: "\"State\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EvaluationJobs_SubmissionId",
                table: "EvaluationJobs");

            migrationBuilder.DropColumn(
                name: "AcknowledgedAt",
                table: "EvaluationJobs");

            migrationBuilder.DropColumn(
                name: "Refunds",
                table: "EvaluationJobs");
        }
    }
}

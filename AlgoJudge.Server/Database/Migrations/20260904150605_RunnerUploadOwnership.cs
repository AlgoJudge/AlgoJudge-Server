using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class RunnerUploadOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UploadedByRunnerId",
                table: "Files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_UploadedByRunnerId",
                table: "Files",
                column: "UploadedByRunnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Files_Runners_UploadedByRunnerId",
                table: "Files",
                column: "UploadedByRunnerId",
                principalTable: "Runners",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Files_Runners_UploadedByRunnerId",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_UploadedByRunnerId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "UploadedByRunnerId",
                table: "Files");
        }
    }
}

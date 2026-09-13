using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class PrintoutClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}

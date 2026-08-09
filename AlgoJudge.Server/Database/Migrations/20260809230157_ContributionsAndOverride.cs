using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class ContributionsAndOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Grants_UserId",
                table: "Grants");

            migrationBuilder.AddColumn<bool>(
                name: "OverrideSystem",
                table: "Grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceProviderId",
                table: "Grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Grants_SourceProviderId",
                table: "Grants",
                column: "SourceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_UserId_Manual",
                table: "Grants",
                column: "UserId",
                unique: true,
                filter: "\"ActivityId\" IS NULL AND \"SourceProviderId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_UserId_Provider",
                table: "Grants",
                columns: new[] { "UserId", "SourceProviderId" },
                unique: true,
                filter: "\"ActivityId\" IS NULL AND \"SourceProviderId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Grants_IdentityProviders_SourceProviderId",
                table: "Grants",
                column: "SourceProviderId",
                principalTable: "IdentityProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Grants_IdentityProviders_SourceProviderId",
                table: "Grants");

            migrationBuilder.DropIndex(
                name: "IX_Grants_SourceProviderId",
                table: "Grants");

            migrationBuilder.DropIndex(
                name: "IX_Grants_UserId_Manual",
                table: "Grants");

            migrationBuilder.DropIndex(
                name: "IX_Grants_UserId_Provider",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "OverrideSystem",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "SourceProviderId",
                table: "Grants");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_UserId",
                table: "Grants",
                column: "UserId",
                unique: true,
                filter: "\"ActivityId\" IS NULL");
        }
    }
}

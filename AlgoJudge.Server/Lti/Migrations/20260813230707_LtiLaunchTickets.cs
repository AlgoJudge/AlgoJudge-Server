using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Lti.Migrations
{
    /// <inheritdoc />
    public partial class LtiLaunchTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LtiLaunchTickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ticket = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ResourceLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Locale = table.Column<string>(type: "text", nullable: true),
                    Embedded = table.Column<bool>(type: "boolean", nullable: false),
                    ReturnUrl = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiLaunchTickets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LtiLaunchTickets_ExpiresAt",
                table: "LtiLaunchTickets",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_LtiLaunchTickets_Ticket",
                table: "LtiLaunchTickets",
                column: "Ticket",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LtiLaunchTickets");
        }
    }
}

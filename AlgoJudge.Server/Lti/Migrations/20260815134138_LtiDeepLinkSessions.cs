using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Lti.Migrations
{
    /// <inheritdoc />
    public partial class LtiDeepLinkSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LtiDeepLinkSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    PlatformId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ContextId = table.Column<string>(type: "text", nullable: false),
                    ContextTitle = table.Column<string>(type: "text", nullable: true),
                    ReturnUrl = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "text", nullable: true),
                    AcceptMultiple = table.Column<bool>(type: "boolean", nullable: false),
                    Locale = table.Column<string>(type: "text", nullable: true),
                    Embedded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiDeepLinkSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LtiDeepLinkSessions_LtiPlatforms_PlatformId",
                        column: x => x.PlatformId,
                        principalTable: "LtiPlatforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LtiDeepLinkSessions_Code",
                table: "LtiDeepLinkSessions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiDeepLinkSessions_PlatformId",
                table: "LtiDeepLinkSessions",
                column: "PlatformId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LtiDeepLinkSessions");
        }
    }
}

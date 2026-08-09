using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class FederatedSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FederatedSignInAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ChangedPermissions = table.Column<bool>(type: "boolean", nullable: false),
                    Matched = table.Column<string>(type: "jsonb", nullable: false),
                    Detail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    At = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederatedSignInAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FederatedSignInAttempts_IdentityProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FederatedSignInAttempts_ProviderId_At",
                table: "FederatedSignInAttempts",
                columns: new[] { "ProviderId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_FederatedSignInAttempts_UserId_At",
                table: "FederatedSignInAttempts",
                columns: new[] { "UserId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FederatedSignInAttempts");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class IdentityProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdentityProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientSecret = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Scopes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccountUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ClaimPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UnmappedBehavior = table.Column<int>(type: "integer", nullable: false),
                    DefaultTemplateName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeletionChannelEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DeletionSecret = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityProviderMappingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TemplateName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviderMappingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityProviderMappingRules_IdentityProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    LastSignInAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserIdentities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserIdentities_IdentityProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderMappingRules_ProviderId_ClaimValue",
                table: "IdentityProviderMappingRules",
                columns: new[] { "ProviderId", "ClaimValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_Slug",
                table: "IdentityProviders",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_ProviderId_Subject",
                table: "UserIdentities",
                columns: new[] { "ProviderId", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_UserId",
                table: "UserIdentities",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityProviderMappingRules");

            migrationBuilder.DropTable(
                name: "UserIdentities");

            migrationBuilder.DropTable(
                name: "IdentityProviders");
        }
    }
}

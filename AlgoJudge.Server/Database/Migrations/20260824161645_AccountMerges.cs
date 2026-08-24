using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class AccountMerges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountMerges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    TargetUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    MergedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    MergedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    AnonymiseAfter = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    SourceAnonymisedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    UndoneAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    UndoneByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Moved = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMerges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountMerges_AspNetUsers_SourceUserId",
                        column: x => x.SourceUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountMerges_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_SourceAnonymisedAt_AnonymiseAfter",
                table: "AccountMerges",
                columns: new[] { "SourceAnonymisedAt", "AnonymiseAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_SourceUserId",
                table: "AccountMerges",
                column: "SourceUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_TargetUserId",
                table: "AccountMerges",
                column: "TargetUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountMerges");
        }
    }
}

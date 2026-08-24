using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class SeriesLockdown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Importance",
                table: "Series",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // **True, and the generator said false.** EF reads the column, not
            // the property initialiser, so every existing round would have come
            // out with its restrictions switched off — inert today, because none
            // of them carries a rule yet, and silently inert on the day somebody
            // adds one to a round that already exists.
            migrationBuilder.AddColumn<bool>(
                name: "RestrictionsEnabled",
                table: "Series",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // The same correction, and here it would have mattered at once: the
            // generated `false` is the installation-wide off switch, so the
            // whole feature would have shipped disabled on every existing
            // installation and looked like it did not work.
            migrationBuilder.AddColumn<bool>(
                name: "SeriesRestrictionsEnabled",
                table: "Instance",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "SeriesAddressRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<NpgsqlCidr>(type: "cidr", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesAddressRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesAddressRules_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Series_IsOpen_Importance",
                table: "Series",
                columns: new[] { "IsOpen", "Importance" });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesAddressRules_SeriesId",
                table: "SeriesAddressRules",
                column: "SeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeriesAddressRules");

            migrationBuilder.DropIndex(
                name: "IX_Series_IsOpen_Importance",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "Importance",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "RestrictionsEnabled",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "SeriesRestrictionsEnabled",
                table: "Instance");
        }
    }
}

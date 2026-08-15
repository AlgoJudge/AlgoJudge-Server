using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class ActivityPublishedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "Activities",
                type: "timestamptz",
                nullable: true);

            // **Everything that already exists was already visible.** Null means
            // "being prepared", and nothing had that state before this column
            // existed — so without this line every activity in a running
            // installation would go dark on upgrade, which is the exact opposite
            // of what the column is for.
            migrationBuilder.Sql(
                @"UPDATE ""Activities"" SET ""PublishedAt"" = now() WHERE ""PublishedAt"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Activities");
        }
    }
}

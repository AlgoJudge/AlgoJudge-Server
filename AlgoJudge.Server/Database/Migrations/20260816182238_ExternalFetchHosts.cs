using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class ExternalFetchHosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "ExternalFetchHosts",
                table: "Instance",
                type: "text[]",
                nullable: false,
                // Seeded rather than left empty, so an installation that
                // upgrades and one installed today start from the same list.
                // Inert while external judging is off, and an operator who
                // wants none removes it.
                defaultValue: new[] { "onlinejudge.org" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalFetchHosts",
                table: "Instance");
        }
    }
}

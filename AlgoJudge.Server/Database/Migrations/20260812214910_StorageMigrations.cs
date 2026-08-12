using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class StorageMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StorageMigrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetStoreId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FilesMoved = table.Column<int>(type: "integer", nullable: false),
                    BytesMoved = table.Column<long>(type: "bigint", nullable: false),
                    Detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageMigrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StorageMigrations_State",
                table: "StorageMigrations",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StorageMigrations");
        }
    }
}

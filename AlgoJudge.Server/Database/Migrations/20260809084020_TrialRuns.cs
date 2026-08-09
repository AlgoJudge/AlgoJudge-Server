using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class TrialRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Trials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PackageFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProblemType = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RunnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    Deliveries = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    Measurement = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Trials_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Trials_Runners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "Runners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Trials_ActivityId",
                table: "Trials",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_LeaseExpiresAt",
                table: "Trials",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_LeaseToken",
                table: "Trials",
                column: "LeaseToken",
                unique: true,
                filter: "\"LeaseToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_RunnerId",
                table: "Trials",
                column: "RunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_State_CreatedAt",
                table: "Trials",
                columns: new[] { "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Trials");
        }
    }
}

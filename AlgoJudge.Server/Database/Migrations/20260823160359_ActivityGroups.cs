using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// Several people competing as one.
    ///
    /// <para>
    /// One table and three columns, all empty: nothing existed to be grouped, so
    /// every row correctly comes out ungrouped and every activity comes out with
    /// its roster hidden.
    /// </para>
    /// <para>
    /// <b>The two foreign keys point the opposite ways on purpose.</b>
    /// <c>Grants.GroupId</c> is <c>SetNull</c> — deleting a group must not delete
    /// the people who were in it, because a grant is somebody's place in the
    /// activity and the group is one field on it. <c>Submissions.GroupId</c> is
    /// <c>Restrict</c> — that stamp is the record of what competed, and a group
    /// with submissions cannot go without making every one of them say it was
    /// sent by nobody.
    /// </para>
    /// </summary>
    public partial class ActivityGroups : Migration
    {
        /// <summary>Adds the table and the three columns, all empty.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowGroupMembers",
                table: "Activities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ActivityGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityGroups_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_GroupId",
                table: "Submissions",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_GroupId",
                table: "Grants",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityGroups_ActivityId_Name",
                table: "ActivityGroups",
                columns: new[] { "ActivityId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Grants_ActivityGroups_GroupId",
                table: "Grants",
                column: "GroupId",
                principalTable: "ActivityGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_ActivityGroups_GroupId",
                table: "Submissions",
                column: "GroupId",
                principalTable: "ActivityGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <summary>
        /// Drops them. <b>It loses which group sent what</b>, which nothing else
        /// records — going back is not a round trip here.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Grants_ActivityGroups_GroupId",
                table: "Grants");

            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_ActivityGroups_GroupId",
                table: "Submissions");

            migrationBuilder.DropTable(
                name: "ActivityGroups");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_GroupId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Grants_GroupId",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "ShowGroupMembers",
                table: "Activities");
        }
    }
}

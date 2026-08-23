using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// Where a submission arrived from.
    ///
    /// <para>
    /// Three nullable columns and nothing carried: no submission before this
    /// recorded any of it, so every existing row correctly has none.
    /// </para>
    /// <para>
    /// <b>The foreign key is <c>SetNull</c>, and that is the line to check if
    /// this is ever regenerated.</b> The default for an optional key is
    /// <c>ClientSetNull</c>, which leaves the database itself doing nothing — a
    /// session deleted by anything other than EF with the submissions loaded
    /// would take somebody's work with it. Nothing deletes sessions today; this
    /// is what stops the sweep somebody writes later from being a data loss.
    /// </para>
    /// </summary>
    public partial class SubmissionOrigin : Migration
    {
        /// <summary>Adds them, empty.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeviceId",
                table: "Submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<IPAddress>(
                name: "IpAddress",
                table: "Submissions",
                type: "inet",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "Submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_SessionId",
                table: "Submissions",
                column: "SessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_UserSessions_SessionId",
                table: "Submissions",
                column: "SessionId",
                principalTable: "UserSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <summary>Drops them. Nothing derives from them.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_UserSessions_SessionId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_SessionId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "Submissions");
        }
    }
}

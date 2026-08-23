using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// The name a browser gives itself, on the session.
    ///
    /// <para>
    /// One nullable column and nothing carried: no browser has ever sent the
    /// header, so every existing row correctly has none. It fills in from the
    /// first request each browser makes after the Client that sends it ships.
    /// </para>
    /// </summary>
    public partial class SessionDeviceId : Migration
    {
        /// <summary>Adds it, empty.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeviceId",
                table: "UserSessions",
                type: "uuid",
                nullable: true);
        }

        /// <summary>Drops it. Nothing derives from it.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "UserSessions");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// Remembers how long a lease is, so a heartbeat can renew by it.
    ///
    /// <para>
    /// One nullable column and nothing carried: the duration it records cannot
    /// be recovered for a job already running, because every renewal has moved
    /// <c>LeaseExpiresAt</c> and nothing kept what it was moved by. Null is read
    /// as the Server's default, which is exactly what those jobs had.
    /// </para>
    /// </summary>
    public partial class LeaseSecondsOnJob : Migration
    {
        /// <summary>Adds it, empty.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LeaseSeconds",
                table: "EvaluationJobs",
                type: "integer",
                nullable: true);
        }

        /// <summary>Drops it. Nothing else read it.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseSeconds",
                table: "EvaluationJobs");
        }
    }
}

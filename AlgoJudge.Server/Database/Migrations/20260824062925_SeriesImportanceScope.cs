using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class SeriesImportanceScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // **The generated default is the wanted one here, unlike
            // `SeriesLockdown`'s two.** `0` is `Activity`, which is the decision
            // for rows already stored as well as for new ones: a rank reaching
            // out of its own activity is opted into. Said out loud because the
            // previous migration needed both of its defaults corrected by hand,
            // and a reader arriving from it will look for the same correction.
            migrationBuilder.AddColumn<int>(
                name: "ImportanceScope",
                table: "Series",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImportanceScope",
                table: "Series");
        }
    }
}

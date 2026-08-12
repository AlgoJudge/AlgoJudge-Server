using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// The contract half of expand–contract: the old column goes.
    /// <para>
    /// <b>The top-up backfill is the whole reason this is hand-written.</b>
    /// <c>FileStorageExpand</c> copied every row that existed <i>when it ran</i>.
    /// A deployment that sat on that migration for a day kept writing bytes the
    /// old way — into <c>Files.Content</c>, with no <c>FileContents</c> row —
    /// because the code that writes through a store arrives with this step, not
    /// with that one. Dropping the column without sweeping those up would delete
    /// a day of uploads, silently, with no error and nothing left to recover from.
    /// </para>
    /// <para>
    /// <c>WHERE NOT EXISTS</c> rather than an upsert: rows written by the new
    /// path already have their bytes in place, and their <c>Content</c> is an
    /// empty array. Copying that over a good blob is the one way this migration
    /// could destroy data all by itself.
    /// </para>
    /// <para>
    /// <c>Down</c> re-creates the column empty. It cannot do better, and saying so
    /// here is more use than a comment claiming it is reversible: the bytes are in
    /// <c>FileContents</c>, and going back means running the reverse copy by hand.
    /// </para>
    /// </summary>
    public partial class FileContentDropped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO ""FileContents"" (""FileId"", ""Content"")
                SELECT f.""Id"", f.""Content""
                FROM ""Files"" f
                WHERE NOT EXISTS (
                    SELECT 1 FROM ""FileContents"" c WHERE c.""FileId"" = f.""Id""
                );
            ");

            migrationBuilder.DropColumn(
                name: "Content",
                table: "Files");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Content",
                table: "Files",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}

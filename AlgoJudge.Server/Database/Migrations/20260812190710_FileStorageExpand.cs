using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// Makes room for the bytes to live somewhere else — without moving them yet.
    /// <para>
    /// <b>Expand, then contract.</b> This adds <c>FileContents</c>, copies every
    /// byte into it and leaves <c>Files.Content</c> exactly where it is;
    /// <c>FileContentDropped</c> removes the old column once nothing reads it.
    /// Two migrations rather than one because a deployment may sit between them,
    /// and because a schema change that removes a mapped property cannot compile
    /// until its replacement exists.
    /// </para>
    /// <para>
    /// Hand-edited after scaffolding, in two places that matter:
    /// <list type="bullet">
    /// <item><description>
    /// <b><c>StorageId</c> backfills to <c>pg</c>, not to the empty string.</b>
    /// EF scaffolds <c>defaultValue: ""</c> for a non-nullable string, which
    /// would leave every existing row naming a store that does not exist — and
    /// the startup check would then refuse to bring the Server up on a database
    /// that was perfectly fine the day before.
    /// </description></item>
    /// <item><description>
    /// <b><c>FileContents</c> is raw SQL, not <c>CreateTable</c>.</b> It is not
    /// an entity and never will be: EF cannot stream a <c>byte[]</c>, and change
    /// tracking would snapshot every file it touched. Writing it as SQL says
    /// that out loud. The model snapshot does not know the table exists, which
    /// is correct and harmless — the migration differ compares the model against
    /// the snapshot, and neither has ever heard of it.
    /// </description></item>
    /// </list>
    /// </para>
    /// </summary>
    public partial class FileStorageExpand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PreviousCopyDeleteAfter",
                table: "Files",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousStorageId",
                table: "Files",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // The default is what backfills the existing rows, and it is dropped
            // immediately afterwards so that a later insert has to say where it
            // put the bytes rather than inheriting an answer.
            migrationBuilder.AddColumn<string>(
                name: "StorageId",
                table: "Files",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "pg");

            migrationBuilder.Sql(@"ALTER TABLE ""Files"" ALTER COLUMN ""StorageId"" DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PendingCopySweep",
                table: "Files",
                column: "PreviousCopyDeleteAfter",
                filter: "\"PreviousCopyDeleteAfter\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Files_StorageId",
                table: "Files",
                column: "StorageId");

            // STORAGE EXTERNAL: out of line and **uncompressed**. Uncompressed is
            // the load-bearing half — a ranged read is `substring(Content from X
            // for Y)`, and PostgreSQL can only seek into a TOASTed value that was
            // not compressed. With the default EXTENDED, serving `Range:` on a
            // 128 MiB package would decompress from the beginning every time.
            //
            // **No foreign key back to Files, deliberately.** §6 of the spec
            // draws one with ON DELETE CASCADE, and §7 step 6 requires the bytes
            // to be written *before* the row that names them. For this backend
            // the two cannot both hold: the write would violate a key pointing at
            // a row that does not exist yet.
            //
            // The ordering is what wins, because it is the one invariant that is
            // the same on all three backends — bytes before row, so the only
            // reachable inconsistency is bytes nobody points at, and the
            // collector already handles those (§2, invariant 3). Losing the
            // cascade costs nothing uniform either: filesystem and S3 never had
            // one, so deletion has to go through IBlobStore.DeleteAsync for them
            // regardless, and postgres now takes the same path as its siblings
            // rather than a private one.
            //
            // What the database no longer defends, §6.1 already said it could
            // not: "StorageId names postgres iff a FileContents row exists" is
            // listed there as an invariant no constraint can express, to be
            // asserted by tests and checked by the health endpoint.
            migrationBuilder.Sql(@"
                CREATE TABLE ""FileContents"" (
                    ""FileId""  uuid  PRIMARY KEY,
                    ""Content"" bytea NOT NULL
                );
                ALTER TABLE ""FileContents"" ALTER COLUMN ""Content"" SET STORAGE EXTERNAL;
                INSERT INTO ""FileContents"" (""FileId"", ""Content"")
                    SELECT ""Id"", ""Content"" FROM ""Files"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""FileContents"";");

            migrationBuilder.DropIndex(
                name: "IX_Files_PendingCopySweep",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_StorageId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PreviousCopyDeleteAfter",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PreviousStorageId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "StorageId",
                table: "Files");
        }
    }
}

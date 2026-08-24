using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// Which Runners judge which work.
    /// <para>
    /// <b>A no-op until somebody types a tag.</b> Every activity arrives with an
    /// empty list and every round inherits it, which the matcher reads as the
    /// default pool — the same pool every untagged Runner is in. Nothing moves
    /// on the day this is applied.
    /// </para>
    /// </summary>
    public partial class RunnerTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Null inherits the activity's. There is no empty override: a round
            // that wants the general Runners while its activity is pinned writes
            // `default` out, so one meaning keeps one spelling.
            migrationBuilder.AddColumn<List<string>>(
                name: "RunnerTags",
                table: "Series",
                type: "text[]",
                nullable: true);

            // **The default matters.** Without it PostgreSQL refuses a NOT NULL
            // column on a table that already holds rows, and an installation with
            // one activity in it fails to start.
            migrationBuilder.AddColumn<List<string>>(
                name: "RunnerTags",
                table: "Activities",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            // The Runner tags that already exist were decoration — nothing read
            // them. From here they are matched by equality, so they are brought
            // into the one spelling now, rather than leaving `Lab-A` looking as
            // though it pairs with `lab-a` and never doing it.
            migrationBuilder.Sql("""
                UPDATE "Runners"
                SET "Tags" = ARRAY(SELECT DISTINCT lower(trim(tag))
                                   FROM unnest("Tags") AS tag
                                   WHERE trim(tag) <> '');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The lowercasing is not undone. It cannot be — the original spelling
            // is gone — and it changed nothing anything was reading.
            migrationBuilder.DropColumn(
                name: "RunnerTags",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "RunnerTags",
                table: "Activities");
        }
    }
}

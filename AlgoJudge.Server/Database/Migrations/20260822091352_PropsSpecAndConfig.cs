using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// One name for the extra fields, and one layer fewer in the chain.
    ///
    /// <para>
    /// Four columns arrive and three go. <b>Nothing is dropped before it has been
    /// carried</b>, except the one thing that is being dropped on purpose — see
    /// below. EF scaffolded the drops alone and warned about data loss; the
    /// copies here are what makes the warning untrue.
    /// </para>
    /// </summary>
    public partial class PropsSpecAndConfig : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── the new columns, before anything is read into them ───────────

            migrationBuilder.AddColumn<string>(
                name: "Props", table: "Submissions", type: "jsonb", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Props", table: "SeriesProblems", type: "jsonb", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Spec", table: "SeriesProblems", type: "jsonb", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Props", table: "Results", type: "jsonb", nullable: true);

            // ── what a participant declared, kept ────────────────────────────
            //
            // A submission's language is a fact about somebody's work and about
            // what it was judged as. It becomes one member of the document that
            // replaces the column, under the problem type it was sent to — which
            // is why this joins out to `Problems`: the envelope names the type,
            // and a row cannot state its own.
            //
            // `cpp` and `python` carry over unchanged and still resolve: the
            // Runner's catalogue keeps them as aliases of `cpp20-gcc` and
            // `python3` for exactly this reason.
            migrationBuilder.Sql("""
                UPDATE "Submissions" s
                SET "Props" = jsonb_build_object('type', p."Type", 'language', s."Language")
                FROM "SeriesProblems" sp
                JOIN "Problems" p ON p."Id" = sp."ProblemId"
                WHERE sp."Id" = s."SeriesProblemId"
                  AND s."Language" IS NOT NULL
                  AND s."Language" <> '';
                """);

            // ── the middle layer of the chain, folded into the one below ─────
            //
            // A version's configuration applied to every assignment that pinned
            // it and stated none of its own, so copying it there preserves
            // exactly what those assignments were judged under.
            //
            // **Where an assignment states its own, this leaves it alone**, and
            // that is not a full reproduction of what the Server used to do: it
            // deep-merged the two, so an assignment naming `limits.timeMs` alone
            // still got the version's `limits.memoryBytes`. A deep merge in SQL
            // is a recursive function this migration is not going to carry.
            //
            // No database in existence has such a row — nothing but the seeder
            // ever wrote a version config, and the seeder leaves assignments
            // null. Before running this anywhere that matters, check:
            //
            //   SELECT count(*) FROM "SeriesProblems" sp
            //   JOIN "ProblemVersions" pv ON pv."Id" = sp."PinnedProblemVersionId"
            //   WHERE sp."Config" IS NOT NULL AND pv."Config" IS NOT NULL;
            //
            // Anything above zero wants merging by hand first.
            migrationBuilder.Sql("""
                UPDATE "SeriesProblems" sp
                SET "Config" = pv."Config"
                FROM "ProblemVersions" pv
                WHERE pv."Id" = sp."PinnedProblemVersionId"
                  AND sp."Config" IS NULL
                  AND pv."Config" IS NOT NULL;
                """);

            // ── and now the columns they came from ───────────────────────────

            migrationBuilder.DropColumn(name: "Language", table: "Submissions");
            migrationBuilder.DropColumn(name: "Config", table: "ProblemVersions");

            // **Not carried anywhere, deliberately.** The allowed language set
            // lives on the assignment now, in three documents with three readers
            // — `config` for the Runner, `spec` for the form, `props` for a
            // header — and a list on the activity would be a fourth copy that
            // nothing reads and nothing enforces. An installation that had one
            // sets it per assignment.
            migrationBuilder.DropColumn(name: "Languages", table: "Activities");
        }

        /// <summary>
        /// Reverses the shape. <b>It does not reverse the copies</b>: going back
        /// leaves a submission's language in `Props`, where the old column cannot
        /// read it, and restores `Activities.Languages` empty.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language", table: "Submissions", type: "text", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Config", table: "ProblemVersions", type: "jsonb", nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "Languages", table: "Activities", type: "text[]", nullable: false,
                defaultValue: new List<string>());

            // The one thing worth putting back, because it is a fact rather than
            // a setting: what a participant said they wrote it in.
            migrationBuilder.Sql("""
                UPDATE "Submissions"
                SET "Language" = "Props" ->> 'language'
                WHERE "Props" ? 'language';
                """);

            migrationBuilder.DropColumn(name: "Props", table: "Submissions");
            migrationBuilder.DropColumn(name: "Props", table: "SeriesProblems");
            migrationBuilder.DropColumn(name: "Spec", table: "SeriesProblems");
            migrationBuilder.DropColumn(name: "Props", table: "Results");
        }
    }
}

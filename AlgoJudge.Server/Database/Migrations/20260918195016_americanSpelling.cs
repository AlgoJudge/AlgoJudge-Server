using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Two columns of <c>AccountMerges</c> and the index over them take the
    /// American spelling the product uses from 0.2: <c>AnonymiseAfter</c> and
    /// <c>SourceAnonymisedAt</c>.
    /// <para>
    /// A rename, not a copy: the values stay where they are, and the index keeps
    /// the rows it covers.
    /// </para>
    /// </summary>
    public partial class americanSpelling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SourceAnonymisedAt",
                table: "AccountMerges",
                newName: "SourceAnonymizedAt");

            migrationBuilder.RenameColumn(
                name: "AnonymiseAfter",
                table: "AccountMerges",
                newName: "AnonymizeAfter");

            migrationBuilder.RenameIndex(
                name: "IX_AccountMerges_SourceAnonymisedAt_AnonymiseAfter",
                table: "AccountMerges",
                newName: "IX_AccountMerges_SourceAnonymizedAt_AnonymizeAfter");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SourceAnonymizedAt",
                table: "AccountMerges",
                newName: "SourceAnonymisedAt");

            migrationBuilder.RenameColumn(
                name: "AnonymizeAfter",
                table: "AccountMerges",
                newName: "AnonymiseAfter");

            migrationBuilder.RenameIndex(
                name: "IX_AccountMerges_SourceAnonymizedAt_AnonymizeAfter",
                table: "AccountMerges",
                newName: "IX_AccountMerges_SourceAnonymisedAt_AnonymiseAfter");
        }
    }
}

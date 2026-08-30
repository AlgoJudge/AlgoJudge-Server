using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class InstanceThemeFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FileReferences_OwnerKindMatches",
                table: "FileReferences");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileReferences_OwnerKindMatches",
                table: "FileReferences",
                sql: "(\"OwnerKind\" = 0 AND \"ProblemVersionId\" IS NOT NULL) OR (\"OwnerKind\" = 1 AND \"ActivityId\" IS NOT NULL) OR (\"OwnerKind\" = 2 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 3 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 4 AND \"RunnerId\" IS NOT NULL) OR (\"OwnerKind\" = 5 AND \"SubmissionId\" IS NOT NULL) OR (\"OwnerKind\" = 6 AND \"EvaluationJobId\" IS NOT NULL) OR (\"OwnerKind\" = 7 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 8 AND \"InstanceId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_FileReferences_OwnerKindMatches",
                table: "FileReferences");

            migrationBuilder.AddCheckConstraint(
                name: "CK_FileReferences_OwnerKindMatches",
                table: "FileReferences",
                sql: "(\"OwnerKind\" = 0 AND \"ProblemVersionId\" IS NOT NULL) OR (\"OwnerKind\" = 1 AND \"ActivityId\" IS NOT NULL) OR (\"OwnerKind\" = 2 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 3 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 4 AND \"RunnerId\" IS NOT NULL) OR (\"OwnerKind\" = 5 AND \"SubmissionId\" IS NOT NULL) OR (\"OwnerKind\" = 6 AND \"EvaluationJobId\" IS NOT NULL)");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// Optimistic-concurrency tokens on five more tables, and a rename of the
    /// four that already had one.
    /// <para>
    /// <b>This migration runs no SQL at all.</b> `xmin` is a PostgreSQL system
    /// column that exists on every table already, so Npgsql's generator drops
    /// the `AddColumn` operations below and writes only the history row —
    /// verified with `dotnet ef migrations script` before it was applied
    /// anywhere. They are left as EF generated them rather than deleted by hand:
    /// they are what the model asks for, and emptying the file would hide that.
    /// </para>
    /// <para>
    /// The rename contributes <b>nothing</b> here, which is the point of it: the
    /// column was always `xmin` and only the property name changed, so the
    /// differ produced no operation.
    /// </para>
    /// </summary>
    public partial class RowVersionTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "StorageMigrations",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Runners",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Instance",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "AccountMerges",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "AccountDeletionRequests",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "StorageMigrations");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Runners");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Instance");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "AccountMerges");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "AccountDeletionRequests");
        }
    }
}

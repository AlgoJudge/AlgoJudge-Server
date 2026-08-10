using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class AccountDeletionRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // **`true`, changed by hand from what the scaffolder wrote.** A new
            // column's default is what backfills every row that already exists,
            // and the C# initialiser (`= true`) applies only to objects this
            // process constructs — so the generated `false` would have silently
            // closed self-service account removal on every installation that
            // migrated. It is a data-protection right before it is a feature: an
            // operator who wants it off should have to choose that.
            //
            // Deliberately not `HasDefaultValue(true)` in the model: EF treats a
            // non-nullable bool equal to its CLR default as "not set", so a store
            // default of `true` would make saving `false` impossible.
            migrationBuilder.AddColumn<bool>(
                name: "AccountDeletionEnabled",
                table: "Instance",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AccountDeletionRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    RequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ExecuteAfter = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    HaltedByUserId = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountDeletionRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountDeletionRequests_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountDeletionRequests_IdentityProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountDeletionRequests_ProviderId_RequestId",
                table: "AccountDeletionRequests",
                columns: new[] { "ProviderId", "RequestId" },
                unique: true,
                filter: "\"RequestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountDeletionRequests_State_ExecuteAfter",
                table: "AccountDeletionRequests",
                columns: new[] { "State", "ExecuteAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountDeletionRequests_UserId",
                table: "AccountDeletionRequests",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountDeletionRequests");

            migrationBuilder.DropColumn(
                name: "AccountDeletionEnabled",
                table: "Instance");
        }
    }
}

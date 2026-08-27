using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// The keys that encrypt a session cookie.
    /// <para>
    /// <b>Additive, and empty on the day it is applied.</b> Data Protection
    /// writes the first key when it first needs one, so an instance running the
    /// previous version alongside this schema neither reads nor writes here —
    /// which is what a rolling deploy needs (<c>SERVER_SCALING.md</c> §6).
    /// </para>
    /// <para>
    /// <b>Applying it signs everybody out once</b>, and only once: the ring in
    /// memory is discarded, so cookies minted before it cannot be read after.
    /// That is the last time a deploy does this, which is the point of the
    /// table.
    /// </para>
    /// <para>
    /// The <c>int</c> key is the framework's, not this product's. See
    /// <c>Authorization/KeyRing.cs</c>.
    /// </para>
    /// </summary>
    public partial class DataProtectionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");
        }
    }
}

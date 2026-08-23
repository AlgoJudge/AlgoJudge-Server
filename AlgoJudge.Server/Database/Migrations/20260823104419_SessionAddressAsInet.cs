using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// A session's address becomes an address rather than a string.
    ///
    /// <para>
    /// <b>Written by hand, because the scaffolded <c>AlterColumn</c> would carry
    /// the defect across.</b> Everything in the column today is an IPv4-mapped
    /// IPv6 address — measured on the development installation on 2026-08-23,
    /// <b>85 rows of 85</b>, because Kestrel on a dual-stack socket renders
    /// <c>172.20.0.1</c> as <c>::ffff:172.20.0.1</c>. PostgreSQL calls that
    /// family 6, so <c>'::ffff:10.0.5.17'::inet &lt;&lt;= '10.0.5.0/24'</c> is
    /// <b>false</b> — silently, never an error. Converting the type without
    /// un-mapping would produce a column of the right type holding values that
    /// answer every question wrongly.
    /// </para>
    /// <para>
    /// The dotted form is the only one .NET writes, so matching <c>::ffff:%.%</c>
    /// is exact rather than approximate: the hexadecimal spelling
    /// (<c>::ffff:ac14:1</c>) never reached this column and is deliberately left
    /// alone if it somehow did.
    /// </para>
    /// </summary>
    public partial class SessionAddressAsInet : Migration
    {
        /// <summary>Converts, un-mapping as it goes.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "UserSessions"
                    ALTER COLUMN "IpAddress" TYPE inet
                    USING (CASE
                        WHEN "IpAddress" IS NULL THEN NULL
                        WHEN "IpAddress" LIKE '::ffff:%.%' THEN substring("IpAddress" from 8)
                        ELSE "IpAddress"
                    END)::inet;
                """);
        }

        /// <summary>
        /// Back to text. <b>It does not restore the mapped spelling</b>, which is
        /// the point of going forward, and nothing reads that column expecting
        /// one form over another.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "UserSessions"
                    ALTER COLUMN "IpAddress" TYPE character varying(64)
                    USING host("IpAddress");
                """);
        }
    }
}

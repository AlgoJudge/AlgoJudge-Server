using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoJudge.Server.Lti.Migrations
{
    /// <inheritdoc />
    public partial class LtiInitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LtiLaunchStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Nonce = table.Column<string>(type: "text", nullable: false),
                    PlatformId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetLinkUri = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiLaunchStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LtiLaunchTickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Ticket = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ResourceLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    Locale = table.Column<string>(type: "text", nullable: true),
                    Embedded = table.Column<bool>(type: "boolean", nullable: false),
                    ReturnUrl = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiLaunchTickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LtiPlatforms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Issuer = table.Column<string>(type: "text", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    DeploymentId = table.Column<string>(type: "text", nullable: false),
                    KeySetUrl = table.Column<string>(type: "text", nullable: false),
                    AuthTokenUrl = table.Column<string>(type: "text", nullable: false),
                    AuthLoginUrl = table.Column<string>(type: "text", nullable: false),
                    IsIdentityAuthority = table.Column<bool>(type: "boolean", nullable: false),
                    IdentityNamespace = table.Column<string>(type: "text", nullable: true),
                    UsernameClaim = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiPlatforms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LtiRegistrationInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PlatformId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiRegistrationInvitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LtiSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountCreationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LtiToolKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kid = table.Column<string>(type: "text", nullable: false),
                    PublicPem = table.Column<string>(type: "text", nullable: false),
                    PrivatePem = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiToolKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LtiDeepLinkSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    PlatformId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ContextId = table.Column<string>(type: "text", nullable: false),
                    ContextTitle = table.Column<string>(type: "text", nullable: true),
                    ReturnUrl = table.Column<string>(type: "text", nullable: false),
                    Data = table.Column<string>(type: "text", nullable: true),
                    AcceptMultiple = table.Column<bool>(type: "boolean", nullable: false),
                    Locale = table.Column<string>(type: "text", nullable: true),
                    Embedded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiDeepLinkSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LtiDeepLinkSessions_LtiPlatforms_PlatformId",
                        column: x => x.PlatformId,
                        principalTable: "LtiPlatforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LtiExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Strength = table.Column<int>(type: "integer", nullable: false),
                    AssertedUsername = table.Column<string>(type: "text", nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastLaunchAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiExternalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LtiExternalIdentities_LtiPlatforms_PlatformId",
                        column: x => x.PlatformId,
                        principalTable: "LtiPlatforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LtiResourceLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformResourceLinkId = table.Column<string>(type: "text", nullable: false),
                    ContextId = table.Column<string>(type: "text", nullable: false),
                    ContextTitle = table.Column<string>(type: "text", nullable: true),
                    ContextHistory = table.Column<string>(type: "text", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgsLineItemsUrl = table.Column<string>(type: "text", nullable: true),
                    NrpsMembershipsUrl = table.Column<string>(type: "text", nullable: true),
                    Aggregation = table.Column<int>(type: "integer", nullable: false),
                    SharingAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiResourceLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LtiResourceLinks_LtiPlatforms_PlatformId",
                        column: x => x.PlatformId,
                        principalTable: "LtiPlatforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LtiLineItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesProblemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformUrl = table.Column<string>(type: "text", nullable: false),
                    ScoreMaximum = table.Column<double>(type: "double precision", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiLineItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LtiLineItems_LtiResourceLinks_ResourceLinkId",
                        column: x => x.ResourceLinkId,
                        principalTable: "LtiResourceLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LtiGradeSyncStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    SourceResultId = table.Column<Guid>(type: "uuid", nullable: true),
                    DesiredScore = table.Column<double>(type: "double precision", nullable: false),
                    PostedScore = table.Column<double>(type: "double precision", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTimestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LtiGradeSyncStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LtiGradeSyncStates_LtiLineItems_LineItemId",
                        column: x => x.LineItemId,
                        principalTable: "LtiLineItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LtiDeepLinkSessions_Code",
                table: "LtiDeepLinkSessions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiDeepLinkSessions_PlatformId",
                table: "LtiDeepLinkSessions",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_LtiExternalIdentities_PlatformId_Subject",
                table: "LtiExternalIdentities",
                columns: new[] { "PlatformId", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiExternalIdentities_PlatformId_UserId",
                table: "LtiExternalIdentities",
                columns: new[] { "PlatformId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiGradeSyncStates_LineItemId_UserId",
                table: "LtiGradeSyncStates",
                columns: new[] { "LineItemId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiGradeSyncStates_State_NextAttemptAt",
                table: "LtiGradeSyncStates",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LtiLaunchStates_ExpiresAt",
                table: "LtiLaunchStates",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_LtiLaunchStates_State",
                table: "LtiLaunchStates",
                column: "State",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiLaunchTickets_ExpiresAt",
                table: "LtiLaunchTickets",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_LtiLaunchTickets_Ticket",
                table: "LtiLaunchTickets",
                column: "Ticket",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiLineItems_ResourceLinkId_SeriesProblemId",
                table: "LtiLineItems",
                columns: new[] { "ResourceLinkId", "SeriesProblemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiPlatforms_Issuer_ClientId_DeploymentId",
                table: "LtiPlatforms",
                columns: new[] { "Issuer", "ClientId", "DeploymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiPlatforms_ProviderId",
                table: "LtiPlatforms",
                column: "ProviderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiRegistrationInvitations_Code",
                table: "LtiRegistrationInvitations",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiResourceLinks_ActivityId",
                table: "LtiResourceLinks",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_LtiResourceLinks_PlatformId_PlatformResourceLinkId",
                table: "LtiResourceLinks",
                columns: new[] { "PlatformId", "PlatformResourceLinkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LtiToolKeys_Kid",
                table: "LtiToolKeys",
                column: "Kid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LtiDeepLinkSessions");

            migrationBuilder.DropTable(
                name: "LtiExternalIdentities");

            migrationBuilder.DropTable(
                name: "LtiGradeSyncStates");

            migrationBuilder.DropTable(
                name: "LtiLaunchStates");

            migrationBuilder.DropTable(
                name: "LtiLaunchTickets");

            migrationBuilder.DropTable(
                name: "LtiRegistrationInvitations");

            migrationBuilder.DropTable(
                name: "LtiSettings");

            migrationBuilder.DropTable(
                name: "LtiToolKeys");

            migrationBuilder.DropTable(
                name: "LtiLineItems");

            migrationBuilder.DropTable(
                name: "LtiResourceLinks");

            migrationBuilder.DropTable(
                name: "LtiPlatforms");
        }
    }
}

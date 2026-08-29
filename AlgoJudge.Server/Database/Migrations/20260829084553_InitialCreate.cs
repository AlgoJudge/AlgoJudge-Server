using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoJudge.Server.Database.Migrations
{
    /// <summary>
    /// The whole schema, in one migration.
    /// <para>
    /// <b>Squashed on 2026-08-28</b>, before 0.1.0 and therefore before any
    /// installation had a database to carry forward: thirty-one migrations for
    /// this context and seven for the LTI one became one each. What the old
    /// chain carried and this does not is its backfills — every one of them
    /// rewrote rows an empty database does not have.
    /// </para>
    /// <para>
    /// <b>One thing here is not generated from the model</b>, at the end of
    /// <c>Up</c>. Everything else in this file is, so regenerating it is a
    /// mechanical act; that block is not, and would be lost by one.
    /// </para>
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessKeys",
                columns: table => new
                {
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessKeys", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RankingType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    EndDate = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    HasQuestions = table.Column<bool>(type: "boolean", nullable: false),
                    ScoreVisibility = table.Column<int>(type: "integer", nullable: false),
                    ShowGroupMembers = table.Column<bool>(type: "boolean", nullable: false),
                    JoinPolicy = table.Column<int>(type: "integer", nullable: false),
                    JoinPassword = table.Column<string>(type: "text", nullable: true),
                    Unlisted = table.Column<bool>(type: "boolean", nullable: false),
                    HideEndedSeriesProblems = table.Column<bool>(type: "boolean", nullable: false),
                    Props = table.Column<string>(type: "jsonb", nullable: true),
                    MaxUploadBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxAttachments = table.Column<int>(type: "integer", nullable: false),
                    MaxSubmissionsPerProblem = table.Column<int>(type: "integer", nullable: true),
                    RunnerTags = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    ArchivedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    FirstName = table.Column<string>(type: "text", nullable: true),
                    LastName = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<string>(type: "text", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    IsTemporary = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    BlockedReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    Anonymized = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "IdentityProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClientSecret = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Scopes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccountUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DeletionUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ClaimPath = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UnmappedBehavior = table.Column<int>(type: "integer", nullable: false),
                    DefaultTemplateName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeletionChannelEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DeletionSecret = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    LocalRegistrationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RequireEmail = table.Column<bool>(type: "boolean", nullable: false),
                    RequireConfirmedEmail = table.Column<bool>(type: "boolean", nullable: false),
                    ShowLogo = table.Column<bool>(type: "boolean", nullable: false),
                    ExternalJudgingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExternalFetchHosts = table.Column<List<string>>(type: "text[]", nullable: false),
                    SeriesRestrictionsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ShowLocalSignIn = table.Column<bool>(type: "boolean", nullable: false),
                    AccountDeletionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instance", x => x.Id);
                    table.CheckConstraint("CK_Instance_Singleton", "\"Id\" = '00000000-0000-7000-8000-000000000001'");
                });

            migrationBuilder.CreateTable(
                name: "Maintenance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Maintenance", x => x.Id);
                    table.CheckConstraint("CK_Maintenance_Singleton", "\"Id\" = '00000000-0000-7000-8000-000000000002'");
                });

            migrationBuilder.CreateTable(
                name: "PermissionTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Permissions = table.Column<string>(type: "jsonb", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Product = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PublicKey = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ProblemTypes = table.Column<List<string>>(type: "text[]", nullable: false),
                    External = table.Column<bool>(type: "boolean", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    Address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Machine = table.Column<string>(type: "jsonb", nullable: true),
                    RegisteredAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "text", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    RevokedReason = table.Column<string>(type: "text", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    CompletedJobs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runners", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StorageMigrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetStoreId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FilesMoved = table.Column<int>(type: "integer", nullable: false),
                    BytesMoved = table.Column<long>(type: "bigint", nullable: false),
                    Detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageMigrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActivityGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityGroups_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttachmentRules",
                columns: table => new
                {
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentRules", x => new { x.ActivityId, x.Name });
                    table.ForeignKey(
                        name: "FK_AttachmentRules_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Series",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    EndDate = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    PausedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    HideProblemsWhilePaused = table.Column<bool>(type: "boolean", nullable: false),
                    RevealProblemCount = table.Column<bool>(type: "boolean", nullable: false),
                    RankingFreezeAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    RankingRevealAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    RankingVisibleFrom = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    RankingVisibleTo = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    StartAnnouncedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    EndAnnouncedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    WindowAnnouncedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    UnfrozenAnnouncedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    Importance = table.Column<int>(type: "integer", nullable: false),
                    ImportanceScope = table.Column<int>(type: "integer", nullable: false),
                    RestrictionsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RunnerTags = table.Column<List<string>>(type: "text[]", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Series_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountMerges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    TargetUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    MergedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    MergedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    AnonymiseAfter = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    SourceAnonymisedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    UndoneAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    UndoneByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Moved = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMerges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountMerges_AspNetUsers_SourceUserId",
                        column: x => x.SourceUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccountMerges_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PreviousStorageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PreviousCopyDeleteAfter = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UploadedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Files_AspNetUsers_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Problems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    External = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    OwnerUserId = table.Column<string>(type: "text", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Problems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Problems_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    LastRequestAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    LastRequestPath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IpAddress = table.Column<IPAddress>(type: "inet", nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                    Detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "FederatedSignInAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ChangedPermissions = table.Column<bool>(type: "boolean", nullable: false),
                    Matched = table.Column<string>(type: "jsonb", nullable: false),
                    Detail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    At = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FederatedSignInAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FederatedSignInAttempts_IdentityProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityProviderMappingRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TemplateName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviderMappingRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityProviderMappingRules_IdentityProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    LastSignInAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserIdentities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserIdentities_IdentityProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Trials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PackageFileId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProblemType = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RunnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    Deliveries = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    Measurement = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Trials_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Trials_Runners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "Runners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceProviderId = table.Column<Guid>(type: "uuid", nullable: true),
                    OverrideSystem = table.Column<bool>(type: "boolean", nullable: false),
                    Permissions = table.Column<string>(type: "jsonb", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedFromTemplate = table.Column<string>(type: "text", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    GrantedByUserId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Grants_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Grants_ActivityGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ActivityGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Grants_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Grants_IdentityProviders_SourceProviderId",
                        column: x => x.SourceProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SeriesAddressRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<IPNetwork>(type: "cidr", nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesAddressRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesAddressRules_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProblemShares",
                columns: table => new
                {
                    ProblemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProblemShares", x => new { x.ProblemId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ProblemShares_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProblemShares_Problems_ProblemId",
                        column: x => x.ProblemId,
                        principalTable: "Problems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProblemVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProblemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    Props = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProblemVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProblemVersions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProblemVersions_Problems_ProblemId",
                        column: x => x.ProblemId,
                        principalTable: "Problems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeriesProblems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProblemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PinnedProblemVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Slug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    MaxPoints = table.Column<int>(type: "integer", nullable: true),
                    Config = table.Column<string>(type: "jsonb", nullable: true),
                    Spec = table.Column<string>(type: "jsonb", nullable: true),
                    Props = table.Column<string>(type: "jsonb", nullable: true),
                    MaxUploadBytes = table.Column<long>(type: "bigint", nullable: true),
                    MaxAttachments = table.Column<int>(type: "integer", nullable: true),
                    MaxSubmissions = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesProblems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesProblems_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeriesProblems_ProblemVersions_PinnedProblemVersionId",
                        column: x => x.PinnedProblemVersionId,
                        principalTable: "ProblemVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeriesProblems_Problems_ProblemId",
                        column: x => x.ProblemId,
                        principalTable: "Problems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SeriesProblems_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeriesId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeriesProblemId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Topic = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    AuthorUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    AnswerBody = table.Column<string>(type: "text", nullable: true),
                    AnswerAuthorUserId = table.Column<string>(type: "text", nullable: true),
                    AnsweredAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Questions_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Questions_AspNetUsers_AuthorUserId",
                        column: x => x.AuthorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Questions_SeriesProblems_SeriesProblemId",
                        column: x => x.SeriesProblemId,
                        principalTable: "SeriesProblems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Questions_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Submissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeriesProblemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Props = table.Column<string>(type: "jsonb", nullable: true),
                    IpAddress = table.Column<IPAddress>(type: "inet", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExcludedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ExcludedByUserId = table.Column<string>(type: "text", nullable: true),
                    ExclusionReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Submissions_ActivityGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ActivityGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Submissions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Submissions_SeriesProblems_SeriesProblemId",
                        column: x => x.SeriesProblemId,
                        principalTable: "SeriesProblems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Submissions_UserSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "UserSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "QuestionReads",
                columns: table => new
                {
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuestionReads", x => new { x.QuestionId, x.UserId });
                    table.ForeignKey(
                        name: "FK_QuestionReads_Questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "Questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EvaluationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    ProblemVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    Deliveries = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    LeaseSeconds = table.Column<int>(type: "integer", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvaluationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvaluationJobs_ProblemVersions_ProblemVersionId",
                        column: x => x.ProblemVersionId,
                        principalTable: "ProblemVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvaluationJobs_Runners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "Runners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_EvaluationJobs_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileReferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKind = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    SupersededAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ProblemVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvaluationJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    RunnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileReferences", x => x.Id);
                    table.CheckConstraint("CK_FileReferences_OwnerKindMatches", "(\"OwnerKind\" = 0 AND \"ProblemVersionId\" IS NOT NULL) OR (\"OwnerKind\" = 1 AND \"ActivityId\" IS NOT NULL) OR (\"OwnerKind\" = 2 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 3 AND \"InstanceId\" IS NOT NULL) OR (\"OwnerKind\" = 4 AND \"RunnerId\" IS NOT NULL) OR (\"OwnerKind\" = 5 AND \"SubmissionId\" IS NOT NULL) OR (\"OwnerKind\" = 6 AND \"EvaluationJobId\" IS NOT NULL)");
                    table.CheckConstraint("CK_FileReferences_SingleOwner", "num_nonnulls(\"ProblemVersionId\", \"ActivityId\", \"SubmissionId\", \"EvaluationJobId\", \"RunnerId\", \"InstanceId\") = 1");
                    table.ForeignKey(
                        name: "FK_FileReferences_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileReferences_EvaluationJobs_EvaluationJobId",
                        column: x => x.EvaluationJobId,
                        principalTable: "EvaluationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileReferences_Files_FileId",
                        column: x => x.FileId,
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FileReferences_Instance_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileReferences_ProblemVersions_ProblemVersionId",
                        column: x => x.ProblemVersionId,
                        principalTable: "ProblemVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileReferences_Runners_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "Runners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FileReferences_Submissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalTable: "Submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EvaluationJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProblemVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: true),
                    MaxScore = table.Column<double>(type: "double precision", nullable: true),
                    Verdict = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Extra = table.Column<string>(type: "jsonb", nullable: true),
                    Props = table.Column<string>(type: "jsonb", nullable: true),
                    RunnerVersion = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Results_EvaluationJobs_EvaluationJobId",
                        column: x => x.EvaluationJobId,
                        principalTable: "EvaluationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_SourceAnonymisedAt_AnonymiseAfter",
                table: "AccountMerges",
                columns: new[] { "SourceAnonymisedAt", "AnonymiseAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_SourceUserId",
                table: "AccountMerges",
                column: "SourceUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountMerges_TargetUserId",
                table: "AccountMerges",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Slug",
                table: "Activities",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activities_Unlisted_ArchivedAt",
                table: "Activities",
                columns: new[] { "Unlisted", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityGroups_ActivityId_Name",
                table: "ActivityGroups",
                columns: new[] { "ActivityId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_LeaseExpiresAt",
                table: "EvaluationJobs",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_LeaseToken",
                table: "EvaluationJobs",
                column: "LeaseToken",
                unique: true,
                filter: "\"LeaseToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_ProblemVersionId",
                table: "EvaluationJobs",
                column: "ProblemVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_RunnerId",
                table: "EvaluationJobs",
                column: "RunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_State_CreatedAt",
                table: "EvaluationJobs",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EvaluationJobs_SubmissionId_Attempt",
                table: "EvaluationJobs",
                columns: new[] { "SubmissionId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FederatedSignInAttempts_ProviderId_At",
                table: "FederatedSignInAttempts",
                columns: new[] { "ProviderId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_FederatedSignInAttempts_UserId_At",
                table: "FederatedSignInAttempts",
                columns: new[] { "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_ActivityId",
                table: "FileReferences",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_EvaluationJobId",
                table: "FileReferences",
                column: "EvaluationJobId");

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_FileId",
                table: "FileReferences",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_InstanceId",
                table: "FileReferences",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_OwnerKind_SupersededAt",
                table: "FileReferences",
                columns: new[] { "OwnerKind", "SupersededAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_ProblemVersionId",
                table: "FileReferences",
                column: "ProblemVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_RunnerId",
                table: "FileReferences",
                column: "RunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_FileReferences_SubmissionId",
                table: "FileReferences",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_CreatedAt",
                table: "Files",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PendingCopySweep",
                table: "Files",
                column: "PreviousCopyDeleteAfter",
                filter: "\"PreviousCopyDeleteAfter\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Files_Sha256",
                table: "Files",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Files_StorageId",
                table: "Files",
                column: "StorageId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_UploadedByUserId",
                table: "Files",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_ActivityId_State_IsSystem",
                table: "Grants",
                columns: new[] { "ActivityId", "State", "IsSystem" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_GroupId",
                table: "Grants",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_SourceProviderId",
                table: "Grants",
                column: "SourceProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_UserId_ActivityId",
                table: "Grants",
                columns: new[] { "UserId", "ActivityId" },
                unique: true,
                filter: "\"ActivityId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_UserId_Manual",
                table: "Grants",
                column: "UserId",
                unique: true,
                filter: "\"ActivityId\" IS NULL AND \"SourceProviderId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_UserId_Provider",
                table: "Grants",
                columns: new[] { "UserId", "SourceProviderId" },
                unique: true,
                filter: "\"ActivityId\" IS NULL AND \"SourceProviderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderMappingRules_ProviderId_ClaimValue",
                table: "IdentityProviderMappingRules",
                columns: new[] { "ProviderId", "ClaimValue" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_Slug",
                table: "IdentityProviders",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionTemplates_Name",
                table: "PermissionTemplates",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProblemShares_UserId",
                table: "ProblemShares",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProblemVersions_CreatedByUserId",
                table: "ProblemVersions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProblemVersions_ProblemId_Version",
                table: "ProblemVersions",
                columns: new[] { "ProblemId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Problems_OwnerUserId_Visibility",
                table: "Problems",
                columns: new[] { "OwnerUserId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_Problems_Slug",
                table: "Problems",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Questions_ActivityId_CreatedAt",
                table: "Questions",
                columns: new[] { "ActivityId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Questions_AuthorUserId",
                table: "Questions",
                column: "AuthorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_SeriesId",
                table: "Questions",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_Questions_SeriesProblemId",
                table: "Questions",
                column: "SeriesProblemId");

            migrationBuilder.CreateIndex(
                name: "IX_Results_EvaluationJobId",
                table: "Results",
                column: "EvaluationJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Runners_Fingerprint",
                table: "Runners",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Runners_State",
                table: "Runners",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Series_ActivityId_Slug",
                table: "Series",
                columns: new[] { "ActivityId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Series_EndDate_EndAnnouncedAt",
                table: "Series",
                columns: new[] { "EndDate", "EndAnnouncedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_IsOpen_Importance",
                table: "Series",
                columns: new[] { "IsOpen", "Importance" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_RankingRevealAt_UnfrozenAnnouncedAt",
                table: "Series",
                columns: new[] { "RankingRevealAt", "UnfrozenAnnouncedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_RankingVisibleFrom_WindowAnnouncedAt",
                table: "Series",
                columns: new[] { "RankingVisibleFrom", "WindowAnnouncedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Series_StartDate_StartAnnouncedAt",
                table: "Series",
                columns: new[] { "StartDate", "StartAnnouncedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesAddressRules_SeriesId",
                table: "SeriesAddressRules",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesProblems_ActivityId_Slug",
                table: "SeriesProblems",
                columns: new[] { "ActivityId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SeriesProblems_PinnedProblemVersionId",
                table: "SeriesProblems",
                column: "PinnedProblemVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesProblems_ProblemId",
                table: "SeriesProblems",
                column: "ProblemId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesProblems_SeriesId",
                table: "SeriesProblems",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageMigrations_State",
                table: "StorageMigrations",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_GroupId",
                table: "Submissions",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_SeriesProblemId_UserId_CreatedDate",
                table: "Submissions",
                columns: new[] { "SeriesProblemId", "UserId", "CreatedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_SessionId",
                table: "Submissions",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_UserId",
                table: "Submissions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_ActivityId",
                table: "Trials",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_LeaseExpiresAt",
                table: "Trials",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_LeaseToken",
                table: "Trials",
                column: "LeaseToken",
                unique: true,
                filter: "\"LeaseToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_RunnerId",
                table: "Trials",
                column: "RunnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Trials_State_CreatedAt",
                table: "Trials",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_ProviderId_Subject",
                table: "UserIdentities",
                columns: new[] { "ProviderId", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentities_UserId",
                table: "UserIdentities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ExpiresAt",
                table: "UserSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId_EndedAt",
                table: "UserSessions",
                columns: new[] { "UserId", "EndedAt" });

            // **Not an EF entity, so the model cannot generate it.** The
            // postgres blob store reads and writes these bytes with raw SQL
            // (`Storage/PostgresBlobStore.cs`), and there is deliberately no
            // foreign key back to `Files`: the bytes are written before the row
            // that names them, so a key would refuse the write it exists to
            // protect. `FILE_STORAGE.md` §6.1 lists what no constraint can say
            // here.
            //
            // STORAGE EXTERNAL is out of line and **uncompressed**, and the
            // uncompressed half is the load-bearing one: a ranged read is
            // `substring("Content" from X for Y)`, and PostgreSQL can only seek
            // into a TOASTed value that was not compressed. Under the default
            // EXTENDED, serving `Range:` on a large package would decompress
            // from the beginning every time.
            migrationBuilder.Sql("""
                CREATE TABLE "FileContents" (
                    "FileId"  uuid  PRIMARY KEY,
                    "Content" bytea NOT NULL
                );
                ALTER TABLE "FileContents" ALTER COLUMN "Content" SET STORAGE EXTERNAL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "FileContents";""");

            migrationBuilder.DropTable(
                name: "AccessKeys");

            migrationBuilder.DropTable(
                name: "AccountDeletionRequests");

            migrationBuilder.DropTable(
                name: "AccountMerges");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AttachmentRules");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "FederatedSignInAttempts");

            migrationBuilder.DropTable(
                name: "FileReferences");

            migrationBuilder.DropTable(
                name: "Grants");

            migrationBuilder.DropTable(
                name: "IdentityProviderMappingRules");

            migrationBuilder.DropTable(
                name: "Maintenance");

            migrationBuilder.DropTable(
                name: "PermissionTemplates");

            migrationBuilder.DropTable(
                name: "ProblemShares");

            migrationBuilder.DropTable(
                name: "QuestionReads");

            migrationBuilder.DropTable(
                name: "Results");

            migrationBuilder.DropTable(
                name: "SeriesAddressRules");

            migrationBuilder.DropTable(
                name: "StorageMigrations");

            migrationBuilder.DropTable(
                name: "Trials");

            migrationBuilder.DropTable(
                name: "UserIdentities");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "Instance");

            migrationBuilder.DropTable(
                name: "Questions");

            migrationBuilder.DropTable(
                name: "EvaluationJobs");

            migrationBuilder.DropTable(
                name: "IdentityProviders");

            migrationBuilder.DropTable(
                name: "Runners");

            migrationBuilder.DropTable(
                name: "Submissions");

            migrationBuilder.DropTable(
                name: "ActivityGroups");

            migrationBuilder.DropTable(
                name: "SeriesProblems");

            migrationBuilder.DropTable(
                name: "UserSessions");

            migrationBuilder.DropTable(
                name: "ProblemVersions");

            migrationBuilder.DropTable(
                name: "Series");

            migrationBuilder.DropTable(
                name: "Problems");

            migrationBuilder.DropTable(
                name: "Activities");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}

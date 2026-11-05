using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TriggerCheck.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClusterLeases",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LeasedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClusterLeases", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "EmailOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    To = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LockedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                    AlertChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MinIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxMonitors = table.Column<int>(type: "INTEGER", nullable: false),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    MonthlyPrice = table.Column<double>(type: "REAL", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowsSmsVoice = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowsSelfSignup = table.Column<bool>(type: "INTEGER", nullable: false),
                    IyzicoProductReferenceCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IyzicoPricingPlanReferenceCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AiFeatures = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AiMonthlyCallLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    AiMonthlyTokenLimit = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxRegions = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    MaxStatusPages = table.Column<int>(type: "INTEGER", nullable: true),
                    AllowsStatusPageCustomDomain = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Regions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CredentialRotatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Regions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
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
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContactEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    AlertContactEmailWhenNoChannel = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BillingStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    TrialEndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrialReminderSentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrialKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SignupPending = table.Column<bool>(type: "INTEGER", nullable: false),
                    AiEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AiLanguage = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    AlertGroupingEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    MonthlyReportEmail = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReportTimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    Feature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubjectType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubjectId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    InputHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Effort = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    OutputJson = table.Column<string>(type: "TEXT", nullable: true),
                    MaxConfidence = table.Column<int>(type: "INTEGER", nullable: true),
                    AutonomyLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CostEstimate = table.Column<double>(type: "REAL", nullable: false),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelCalls = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserFeedback = table.Column<int>(type: "INTEGER", nullable: true),
                    FeedbackByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    FeedbackAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiDecisions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Secret = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertChannels_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUsers_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EscalationPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EnabledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EscalationPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EscalationPolicies_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    PrimaryIncidentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    Signals = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    ExplanationQueuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentGroups_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRecurring = table.Column<bool>(type: "INTEGER", nullable: false),
                    RecurrenceDays = table.Column<int>(type: "INTEGER", nullable: false),
                    RecurrenceStartMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliesToAllMonitors = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceWindows_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Monitors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    Keyword = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedStatusCodes = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    HttpMethod = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureThreshold = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SlaTarget = table.Column<double>(type: "REAL", nullable: true),
                    DnsRecordType = table.Column<int>(type: "INTEGER", nullable: true),
                    JsonPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AssertionOperator = table.Column<int>(type: "INTEGER", nullable: true),
                    ExpectedValue = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    GracePeriodSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    HeartbeatTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HeartbeatTokenPrefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastHeartbeatFailed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastHeartbeatMessage = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CertWarnDays = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextCheckAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResponseTimeMs = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    StatusChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DownWhenPaused = table.Column<bool>(type: "INTEGER", nullable: false),
                    CertExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CertSubject = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CertIssuer = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CertError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CertCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CertWarningSentForUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CertWarningSentDays = table.Column<int>(type: "INTEGER", nullable: true),
                    PendingConfirmationSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PendingConfirmationVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    LastProbeFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RegionsExpeditedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeasedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SettingsChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Monitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Monitors_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyReportDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyReportDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonthlyReportDeliveries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportNarratives",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodKey = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DecisionId = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportNarratives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportNarratives_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SmsVoiceUsage",
                columns: table => new
                {
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    DayUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmsVoiceUsage", x => new { x.CustomerId, x.DayUtc });
                    table.ForeignKey(
                        name: "FK_SmsVoiceUsage_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Announcement = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Visibility = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CustomDomain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    CustomDomainToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CustomDomainVerifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CustomDomainCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CustomDomainFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusPages_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ProviderCustomerReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CheckoutToken = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CheckoutUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CurrentPeriodEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelAtPeriodEnd = table.Column<bool>(type: "INTEGER", nullable: false),
                    PastDueSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScheduledPlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastEventAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefundNeededAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefundHandledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RefundHandledBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActivatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AiFeedback",
                columns: table => new
                {
                    DecisionId = table.Column<long>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiFeedback", x => new { x.DecisionId, x.UserId });
                    table.ForeignKey(
                        name: "FK_AiFeedback_AiDecisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "AiDecisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
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
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
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
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    RoleId = table.Column<string>(type: "TEXT", nullable: false)
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
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
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
                name: "EscalationSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EscalationPolicyId = table.Column<int>(type: "INTEGER", nullable: false),
                    DelayMinutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EscalationSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EscalationSteps_EscalationPolicies_EscalationPolicyId",
                        column: x => x.EscalationPolicyId,
                        principalTable: "EscalationPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnomalySignals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Metric = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnomalySignals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnomalySignals_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CheckResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsUp = table.Column<bool>(type: "INTEGER", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    ResponseTimeMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsConfirmation = table.Column<bool>(type: "INTEGER", nullable: false),
                    InMaintenance = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegionId = table.Column<int>(type: "INTEGER", nullable: true),
                    VotedUp = table.Column<bool>(type: "INTEGER", nullable: true),
                    DnsMs = table.Column<int>(type: "INTEGER", nullable: true),
                    ConnectMs = table.Column<int>(type: "INTEGER", nullable: true),
                    TlsMs = table.Column<int>(type: "INTEGER", nullable: true),
                    TtfbMs = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckResults_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CheckResults_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Cause = table.Column<string>(type: "TEXT", nullable: true),
                    FailedRegions = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ResolvedDuringMaintenance = table.Column<bool>(type: "INTEGER", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    AcknowledgeNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastEscalationStep = table.Column<int>(type: "INTEGER", nullable: false),
                    LastEscalationDelayMinutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Incidents_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindowMonitors",
                columns: table => new
                {
                    MaintenanceWindowId = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitorsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindowMonitors", x => new { x.MaintenanceWindowId, x.MonitorsId });
                    table.ForeignKey(
                        name: "FK_MaintenanceWindowMonitors_MaintenanceWindows_MaintenanceWindowId",
                        column: x => x.MaintenanceWindowId,
                        principalTable: "MaintenanceWindows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaintenanceWindowMonitors_Monitors_MonitorsId",
                        column: x => x.MonitorsId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitorBaselines",
                columns: table => new
                {
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Bucket = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    MedianMs = table.Column<double>(type: "REAL", nullable: false),
                    MadMs = table.Column<double>(type: "REAL", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorBaselines", x => new { x.MonitorId, x.Bucket });
                    table.ForeignKey(
                        name: "FK_MonitorBaselines_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitorRegionStates",
                columns: table => new
                {
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                    RegionId = table.Column<int>(type: "INTEGER", nullable: false),
                    NextCheckAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastIsUp = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastResponseTimeMs = table.Column<int>(type: "INTEGER", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    FailingSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastInMaintenance = table.Column<bool>(type: "INTEGER", nullable: false),
                    LeaseItemId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LeasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeasedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastResultItemId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastResultFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorRegionStates", x => new { x.MonitorId, x.RegionId });
                    table.ForeignKey(
                        name: "FK_MonitorRegionStates_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MonitorRegionStates_Regions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "Regions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SettingsSuggestions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentValue = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    SuggestedValue = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EvidenceUpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettingsSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettingsSuggestions_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusPageEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusPageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: true),
                    Mailed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPageEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusPageEvents_StatusPages_StatusPageId",
                        column: x => x.StatusPageId,
                        principalTable: "StatusPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusPageMonitors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusPageId = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitorId = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPageMonitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusPageMonitors_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StatusPageMonitors_StatusPages_StatusPageId",
                        column: x => x.StatusPageId,
                        principalTable: "StatusPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusPageSubscribers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusPageId = table.Column<int>(type: "INTEGER", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfirmationSentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPageSubscribers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusPageSubscribers_StatusPages_StatusPageId",
                        column: x => x.StatusPageId,
                        principalTable: "StatusPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    SubscriptionId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProviderEventId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    Ignored = table.Column<bool>(type: "INTEGER", nullable: false),
                    IgnoredReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubscriptionReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CustomerReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OrderReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PlanName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Amount = table.Column<double>(type: "REAL", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "EscalationStepChannels",
                columns: table => new
                {
                    ChannelsId = table.Column<int>(type: "INTEGER", nullable: false),
                    EscalationStepId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EscalationStepChannels", x => new { x.ChannelsId, x.EscalationStepId });
                    table.ForeignKey(
                        name: "FK_EscalationStepChannels_AlertChannels_ChannelsId",
                        column: x => x.ChannelsId,
                        principalTable: "AlertChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EscalationStepChannels_EscalationSteps_EscalationStepId",
                        column: x => x.EscalationStepId,
                        principalTable: "EscalationSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentGroupMembers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    IncidentId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Included = table.Column<bool>(type: "INTEGER", nullable: false),
                    FoldedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentGroupMembers_IncidentGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "IncidentGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IncidentGroupMembers_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostmortemDrafts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    IncidentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Language = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    DecisionId = table.Column<long>(type: "INTEGER", nullable: true),
                    CauseCategory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LikelyCause = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CustomerMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedByUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    PublishedStatusPageIds = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostmortemDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostmortemDrafts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PostmortemDrafts_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusPageNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusPageId = table.Column<int>(type: "INTEGER", nullable: false),
                    IncidentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPageNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusPageNotes_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StatusPageNotes_StatusPages_StatusPageId",
                        column: x => x.StatusPageId,
                        principalTable: "StatusPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiDecisions_CustomerId_CreatedAtUtc",
                table: "AiDecisions",
                columns: new[] { "CustomerId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiDecisions_Feature_SubjectType_SubjectId_InputHash",
                table: "AiDecisions",
                columns: new[] { "Feature", "SubjectType", "SubjectId", "InputHash" });

            migrationBuilder.CreateIndex(
                name: "IX_AiFeedback_CustomerId",
                table: "AiFeedback",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertChannels_CustomerId",
                table: "AlertChannels",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_AnomalySignals_CustomerId_StartedAtUtc",
                table: "AnomalySignals",
                columns: new[] { "CustomerId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AnomalySignals_MonitorId_EndedAtUtc",
                table: "AnomalySignals",
                columns: new[] { "MonitorId", "EndedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_CustomerId",
                table: "ApiKeys",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Prefix",
                table: "ApiKeys",
                column: "Prefix",
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
                name: "IX_AspNetUsers_CustomerId",
                table: "AspNetUsers",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CheckResults_MonitorId_CheckedAtUtc",
                table: "CheckResults",
                columns: new[] { "MonitorId", "CheckedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CheckResults_RegionId",
                table: "CheckResults",
                column: "RegionId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PlanId",
                table: "Customers",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TrialKey",
                table: "Customers",
                column: "TrialKey");

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutbox_SentAtUtc_FailedAtUtc_NextAttemptAtUtc",
                table: "EmailOutbox",
                columns: new[] { "SentAtUtc", "FailedAtUtc", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EscalationPolicies_CustomerId",
                table: "EscalationPolicies",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EscalationStepChannels_EscalationStepId",
                table: "EscalationStepChannels",
                column: "EscalationStepId");

            migrationBuilder.CreateIndex(
                name: "IX_EscalationSteps_EscalationPolicyId",
                table: "EscalationSteps",
                column: "EscalationPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentGroupMembers_CustomerId",
                table: "IncidentGroupMembers",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentGroupMembers_FoldedAtUtc_NotifiedAtUtc",
                table: "IncidentGroupMembers",
                columns: new[] { "FoldedAtUtc", "NotifiedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentGroupMembers_GroupId",
                table: "IncidentGroupMembers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentGroupMembers_IncidentId",
                table: "IncidentGroupMembers",
                column: "IncidentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentGroups_CustomerId_Status_CreatedAtUtc",
                table: "IncidentGroups",
                columns: new[] { "CustomerId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_MonitorId_StartedAtUtc",
                table: "Incidents",
                columns: new[] { "MonitorId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ResolvedAtUtc",
                table: "Incidents",
                column: "ResolvedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindowMonitors_MonitorsId",
                table: "MaintenanceWindowMonitors",
                column: "MonitorsId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceWindows_CustomerId",
                table: "MaintenanceWindows",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitorBaselines_CustomerId",
                table: "MonitorBaselines",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitorRegionStates_LeaseItemId",
                table: "MonitorRegionStates",
                column: "LeaseItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitorRegionStates_RegionId_LastResultItemId",
                table: "MonitorRegionStates",
                columns: new[] { "RegionId", "LastResultItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitorRegionStates_RegionId_NextCheckAtUtc",
                table: "MonitorRegionStates",
                columns: new[] { "RegionId", "NextCheckAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Monitors_CustomerId",
                table: "Monitors",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Monitors_HeartbeatTokenHash",
                table: "Monitors",
                column: "HeartbeatTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Monitors_IsEnabled_NextCheckAtUtc",
                table: "Monitors",
                columns: new[] { "IsEnabled", "NextCheckAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyReportDeliveries_CustomerId_Month",
                table: "MonthlyReportDeliveries",
                columns: new[] { "CustomerId", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_MonitorId_SentAtUtc",
                table: "Notifications",
                columns: new[] { "MonitorId", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_CustomerId_OccurredAtUtc",
                table: "PaymentEvents",
                columns: new[] { "CustomerId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_Ignored_IgnoredReason",
                table: "PaymentEvents",
                columns: new[] { "Ignored", "IgnoredReason" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_ProviderEventId",
                table: "PaymentEvents",
                column: "ProviderEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_SubscriptionId",
                table: "PaymentEvents",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Name",
                table: "Plans",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PostmortemDrafts_CustomerId",
                table: "PostmortemDrafts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PostmortemDrafts_IncidentId",
                table: "PostmortemDrafts",
                column: "IncidentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Regions_Code",
                table: "Regions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Regions_Prefix",
                table: "Regions",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportNarratives_CustomerId_Kind_PeriodKey",
                table: "ReportNarratives",
                columns: new[] { "CustomerId", "Kind", "PeriodKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportNarratives_Status",
                table: "ReportNarratives",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SettingsSuggestions_CustomerId_Status",
                table: "SettingsSuggestions",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SettingsSuggestions_MonitorId_Kind_Status",
                table: "SettingsSuggestions",
                columns: new[] { "MonitorId", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageEvents_CreatedAtUtc",
                table: "StatusPageEvents",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageEvents_StatusPageId_Key",
                table: "StatusPageEvents",
                columns: new[] { "StatusPageId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageEvents_StatusPageId_MonitorId_CreatedAtUtc",
                table: "StatusPageEvents",
                columns: new[] { "StatusPageId", "MonitorId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageMonitors_MonitorId",
                table: "StatusPageMonitors",
                column: "MonitorId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageMonitors_StatusPageId_MonitorId",
                table: "StatusPageMonitors",
                columns: new[] { "StatusPageId", "MonitorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageNotes_IncidentId",
                table: "StatusPageNotes",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageNotes_StatusPageId_IncidentId",
                table: "StatusPageNotes",
                columns: new[] { "StatusPageId", "IncidentId" });

            migrationBuilder.CreateIndex(
                name: "IX_StatusPages_CustomDomain",
                table: "StatusPages",
                column: "CustomDomain");

            migrationBuilder.CreateIndex(
                name: "IX_StatusPages_CustomerId",
                table: "StatusPages",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusPages_Slug",
                table: "StatusPages",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageSubscribers_ConfirmedAtUtc_CreatedAtUtc",
                table: "StatusPageSubscribers",
                columns: new[] { "ConfirmedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageSubscribers_StatusPageId_Email",
                table: "StatusPageSubscribers",
                columns: new[] { "StatusPageId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_CheckoutToken",
                table: "Subscriptions",
                column: "CheckoutToken");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_CustomerId_Status",
                table: "Subscriptions",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_PlanId",
                table: "Subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_ProviderCustomerReference",
                table: "Subscriptions",
                column: "ProviderCustomerReference");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_ProviderReference",
                table: "Subscriptions",
                column: "ProviderReference");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_RefundNeededAtUtc_RefundHandledAtUtc",
                table: "Subscriptions",
                columns: new[] { "RefundNeededAtUtc", "RefundHandledAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiFeedback");

            migrationBuilder.DropTable(
                name: "AnomalySignals");

            migrationBuilder.DropTable(
                name: "ApiKeys");

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
                name: "CheckResults");

            migrationBuilder.DropTable(
                name: "ClusterLeases");

            migrationBuilder.DropTable(
                name: "EmailOutbox");

            migrationBuilder.DropTable(
                name: "EscalationStepChannels");

            migrationBuilder.DropTable(
                name: "IncidentGroupMembers");

            migrationBuilder.DropTable(
                name: "MaintenanceWindowMonitors");

            migrationBuilder.DropTable(
                name: "MonitorBaselines");

            migrationBuilder.DropTable(
                name: "MonitorRegionStates");

            migrationBuilder.DropTable(
                name: "MonthlyReportDeliveries");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "PaymentEvents");

            migrationBuilder.DropTable(
                name: "PostmortemDrafts");

            migrationBuilder.DropTable(
                name: "ReportNarratives");

            migrationBuilder.DropTable(
                name: "SettingsSuggestions");

            migrationBuilder.DropTable(
                name: "SmsVoiceUsage");

            migrationBuilder.DropTable(
                name: "StatusPageEvents");

            migrationBuilder.DropTable(
                name: "StatusPageMonitors");

            migrationBuilder.DropTable(
                name: "StatusPageNotes");

            migrationBuilder.DropTable(
                name: "StatusPageSubscribers");

            migrationBuilder.DropTable(
                name: "AiDecisions");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "AlertChannels");

            migrationBuilder.DropTable(
                name: "EscalationSteps");

            migrationBuilder.DropTable(
                name: "IncidentGroups");

            migrationBuilder.DropTable(
                name: "MaintenanceWindows");

            migrationBuilder.DropTable(
                name: "Regions");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropTable(
                name: "StatusPages");

            migrationBuilder.DropTable(
                name: "EscalationPolicies");

            migrationBuilder.DropTable(
                name: "Monitors");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Plans");
        }
    }
}

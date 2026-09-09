using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFlagGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "FeatureFlags",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonalKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SummaryJson = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "FlagRolloutSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FlagId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatorUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonalKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExecuteAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RolloutPercentage = table.Column<double>(type: "REAL", nullable: false),
                    ExpectedRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AppliedRevision = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagRolloutSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlagVersions",
                columns: table => new
                {
                    FlagId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: false),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PersonalKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RestoredFromRevision = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagVersions", x => new { x.FlagId, x.Revision });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ProjectId_Sequence",
                table: "AuditEntries",
                columns: new[] { "ProjectId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_FlagRolloutSchedules_FlagId",
                table: "FlagRolloutSchedules",
                column: "FlagId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagRolloutSchedules_Status_ExecuteAt",
                table: "FlagRolloutSchedules",
                columns: new[] { "Status", "ExecuteAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FlagVersions_ProjectId_FlagId_Revision",
                table: "FlagVersions",
                columns: new[] { "ProjectId", "FlagId", "Revision" });

            // Preserve raw legacy configuration as JSON strings, including malformed
            // old payloads. Restoration validates support; migration must not erase data.
            // SQLite's maxLength metadata is not a CHECK, so oversized legacy snapshots
            // survive adoption while new writes enforce the 64 KiB application budget.
            migrationBuilder.Sql("""
                INSERT INTO FlagVersions (FlagId, Revision, ProjectId, ConfigJson, RecordedAt, ActorUserId, PersonalKeyId, Origin, RestoredFromRevision)
                SELECT Id, Revision, ProjectId,
                    json_object('name', Name, 'type', lower(Type), 'active', json(CASE WHEN Active = 1 THEN 'true' ELSE 'false' END),
                        'rolloutPercentage', RolloutPercentage, 'filtersJson', FiltersJson, 'variantsJson', VariantsJson),
                    CAST(strftime('%s', 'now') AS INTEGER) * 10000000 + 621355968000000000,
                    NULL, NULL, 'baseline', NULL
                FROM FeatureFlags;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "FlagRolloutSchedules");

            migrationBuilder.DropTable(
                name: "FlagVersions");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "FeatureFlags");
        }
    }
}

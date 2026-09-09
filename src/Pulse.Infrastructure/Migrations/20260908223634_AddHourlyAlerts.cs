using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHourlyAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertEvaluations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowStart = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservedCount = table.Column<long>(type: "INTEGER", nullable: false),
                    Threshold = table.Column<int>(type: "INTEGER", nullable: false),
                    Triggered = table.Column<bool>(type: "INTEGER", nullable: false),
                    EvaluatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertEvaluations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EventName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Threshold = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    NextWindowStart = table.Column<long>(type: "INTEGER", nullable: false),
                    SkippedThrough = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationReads",
                columns: table => new
                {
                    NotificationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationReads", x => new { x.NotificationId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "ProjectNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EvaluationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    RuleName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EventName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    WindowStart = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservedCount = table.Column<long>(type: "INTEGER", nullable: false),
                    Threshold = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvaluations_ProjectId_EvaluatedAt",
                table: "AlertEvaluations",
                columns: new[] { "ProjectId", "EvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvaluations_RuleId_RuleRevision_WindowEnd",
                table: "AlertEvaluations",
                columns: new[] { "RuleId", "RuleRevision", "WindowEnd" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_Enabled_IsDeleted_NextWindowStart",
                table: "AlertRules",
                columns: new[] { "Enabled", "IsDeleted", "NextWindowStart" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_ProjectId_IsDeleted_CreatedAt",
                table: "AlertRules",
                columns: new[] { "ProjectId", "IsDeleted", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationReads_UserId_ReadAt",
                table: "NotificationReads",
                columns: new[] { "UserId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotifications_EvaluationId",
                table: "ProjectNotifications",
                column: "EvaluationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotifications_ProjectId_CreatedAt",
                table: "ProjectNotifications",
                columns: new[] { "ProjectId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertEvaluations");

            migrationBuilder.DropTable(
                name: "AlertRules");

            migrationBuilder.DropTable(
                name: "NotificationReads");

            migrationBuilder.DropTable(
                name: "ProjectNotifications");
        }
    }
}

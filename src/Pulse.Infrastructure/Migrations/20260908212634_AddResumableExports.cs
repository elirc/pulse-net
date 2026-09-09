using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResumableExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AttemptGeneration",
                table: "ExportJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Consistency",
                table: "ExportJobs",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "live");

            migrationBuilder.AddColumn<long>(
                name: "LastHeartbeatAt",
                table: "ExportJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseExpiresAt",
                table: "ExportJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Owner",
                table: "ExportJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SnapshotCapturedAt",
                table: "ExportJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SnapshotReady",
                table: "ExportJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ExportSnapshotRows",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RowJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportSnapshotRows", x => new { x.JobId, x.Ordinal });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExportJobs_Status_LeaseExpiresAt",
                table: "ExportJobs",
                columns: new[] { "Status", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ExportSnapshotRows_ProjectId_JobId",
                table: "ExportSnapshotRows",
                columns: new[] { "ProjectId", "JobId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportSnapshotRows");

            migrationBuilder.DropIndex(
                name: "IX_ExportJobs_Status_LeaseExpiresAt",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "AttemptGeneration",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "Consistency",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "SnapshotCapturedAt",
                table: "ExportJobs");

            migrationBuilder.DropColumn(
                name: "SnapshotReady",
                table: "ExportJobs");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonErasure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ErasureJobId",
                table: "ProjectIngestionStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MaintenanceGeneration",
                table: "ProjectIngestionStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "InvalidatedByErasureJobId",
                table: "ExportJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ErasureIdentities",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeyVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErasureIdentities", x => new { x.JobId, x.KeyVersion, x.Fingerprint });
                });

            migrationBuilder.CreateTable(
                name: "ErasureJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    MaintenanceGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    QueueCursor = table.Column<long>(type: "INTEGER", nullable: false),
                    EventCursor = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeadLetterCursor = table.Column<Guid>(type: "TEXT", nullable: true),
                    RemovedEvents = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedQueueItems = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedDeadLetters = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedAliases = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedCohortLinks = table.Column<long>(type: "INTEGER", nullable: false),
                    InvalidatedExports = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErasureJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ErasureUnreadableItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QueueSequence = table.Column<long>(type: "INTEGER", nullable: true),
                    DeadLetterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscardedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    DiscardedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ErasureUnreadableItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentitySuppressions",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeyVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentitySuppressions", x => new { x.ProjectId, x.KeyVersion, x.Fingerprint });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ErasureJobs_ProjectId",
                table: "ErasureJobs",
                column: "ProjectId",
                unique: true,
                filter: "\"Status\" <> 'Completed'");

            migrationBuilder.CreateIndex(
                name: "IX_ErasureJobs_Status_UpdatedAt",
                table: "ErasureJobs",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ErasureUnreadableItems_JobId_DeadLetterId",
                table: "ErasureUnreadableItems",
                columns: new[] { "JobId", "DeadLetterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ErasureUnreadableItems_JobId_QueueSequence",
                table: "ErasureUnreadableItems",
                columns: new[] { "JobId", "QueueSequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ErasureIdentities");

            migrationBuilder.DropTable(
                name: "ErasureJobs");

            migrationBuilder.DropTable(
                name: "ErasureUnreadableItems");

            migrationBuilder.DropTable(
                name: "IdentitySuppressions");

            migrationBuilder.DropColumn(
                name: "ErasureJobId",
                table: "ProjectIngestionStates");

            migrationBuilder.DropColumn(
                name: "MaintenanceGeneration",
                table: "ProjectIngestionStates");

            migrationBuilder.DropColumn(
                name: "InvalidatedByErasureJobId",
                table: "ExportJobs");
        }
    }
}

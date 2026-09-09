using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AdmissionId",
                table: "QueuedEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorCode",
                table: "QueuedEvents",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAt",
                table: "QueuedEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AdmissionId",
                table: "DeadLetterEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaptureAdmissionKeys",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdmissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FingerprintVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureAdmissionKeys", x => new { x.ProjectId, x.ClientEventId });
                });

            migrationBuilder.CreateTable(
                name: "CaptureItemTransitions",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdmissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReplayGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeadLetterId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureItemTransitions", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "CaptureProcessingItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ChangedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ReplayGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeadLetterId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RetiredReason = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureProcessingItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CaptureReceiptItems",
                columns: table => new
                {
                    ReceiptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdmissionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureReceiptItems", x => new { x.ReceiptId, x.Ordinal });
                });

            migrationBuilder.CreateTable(
                name: "CaptureReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaptureReceipts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectIngestionLeases",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Owner = table.Column<Guid>(type: "TEXT", nullable: true),
                    Generation = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastServedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectIngestionLeases", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "ProjectIngestionStates",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GateVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxPending = table.Column<int>(type: "INTEGER", nullable: true),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectIngestionStates", x => x.ProjectId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueuedEvents_AdmissionId",
                table: "QueuedEvents",
                column: "AdmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_QueuedEvents_ProjectId_NextAttemptAt_Seq",
                table: "QueuedEvents",
                columns: new[] { "ProjectId", "NextAttemptAt", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetterEvents_AdmissionId",
                table: "DeadLetterEvents",
                column: "AdmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureAdmissionKeys_AdmissionId",
                table: "CaptureAdmissionKeys",
                column: "AdmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureAdmissionKeys_ExpiresAt",
                table: "CaptureAdmissionKeys",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureItemTransitions_AdmissionId_Sequence",
                table: "CaptureItemTransitions",
                columns: new[] { "AdmissionId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureProcessingItems_ProjectId_State_ChangedAt",
                table: "CaptureProcessingItems",
                columns: new[] { "ProjectId", "State", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CaptureReceiptItems_AdmissionId",
                table: "CaptureReceiptItems",
                column: "AdmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureReceipts_ProjectId_CreatedAt",
                table: "CaptureReceipts",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectIngestionLeases_ExpiresAt",
                table: "ProjectIngestionLeases",
                column: "ExpiresAt");

            // Existing work has nullable admission/retry links: immediately due,
            // with no invented receipt or processing history. Provision projects
            // with disabled capacity limits and unowned generation-zero leases.
            migrationBuilder.Sql("""
                INSERT INTO ProjectIngestionStates (ProjectId, GateVersion, MaxPending, Paused)
                    SELECT Id, 0, NULL, 0 FROM Projects;
                INSERT INTO ProjectIngestionLeases (ProjectId, Owner, Generation, ExpiresAt, LastServedAt)
                    SELECT Id, NULL, 0, 0, 0 FROM Projects;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaptureAdmissionKeys");

            migrationBuilder.DropTable(
                name: "CaptureItemTransitions");

            migrationBuilder.DropTable(
                name: "CaptureProcessingItems");

            migrationBuilder.DropTable(
                name: "CaptureReceiptItems");

            migrationBuilder.DropTable(
                name: "CaptureReceipts");

            migrationBuilder.DropTable(
                name: "ProjectIngestionLeases");

            migrationBuilder.DropTable(
                name: "ProjectIngestionStates");

            migrationBuilder.DropIndex(
                name: "IX_QueuedEvents_AdmissionId",
                table: "QueuedEvents");

            migrationBuilder.DropIndex(
                name: "IX_QueuedEvents_ProjectId_NextAttemptAt_Seq",
                table: "QueuedEvents");

            migrationBuilder.DropIndex(
                name: "IX_DeadLetterEvents_AdmissionId",
                table: "DeadLetterEvents");

            migrationBuilder.DropColumn(
                name: "AdmissionId",
                table: "QueuedEvents");

            migrationBuilder.DropColumn(
                name: "LastErrorCode",
                table: "QueuedEvents");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "QueuedEvents");

            migrationBuilder.DropColumn(
                name: "AdmissionId",
                table: "DeadLetterEvents");
        }
    }
}

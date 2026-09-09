using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEventRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectRetentionPolicies",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectRetentionPolicies", x => x.ProjectId);
                });

            migrationBuilder.CreateTable(
                name: "RetentionRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Cutoff = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RemovedCount = table.Column<long>(type: "INTEGER", nullable: false),
                    Batches = table.Column<int>(type: "INTEGER", nullable: false),
                    LastBatchAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_ProjectId_Timestamp",
                table: "Events",
                columns: new[] { "ProjectId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionRuns_ProjectId",
                table: "RetentionRuns",
                column: "ProjectId",
                unique: true,
                filter: "\"Status\" = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_RetentionRuns_ProjectId_StartedAt",
                table: "RetentionRuns",
                columns: new[] { "ProjectId", "StartedAt" });
            // Deploy disabled: migration never deletes existing analytics data.
            migrationBuilder.Sql("INSERT INTO ProjectRetentionPolicies (ProjectId, Enabled, Days, Revision) SELECT Id, 0, 365, 1 FROM Projects;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectRetentionPolicies");

            migrationBuilder.DropTable(
                name: "RetentionRuns");

            migrationBuilder.DropIndex(
                name: "IX_Events_ProjectId_Timestamp",
                table: "Events");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestionTracing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalTraceParent",
                table: "QueuedEvents",
                type: "TEXT",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                table: "QueuedEvents",
                type: "TEXT",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalTraceParent",
                table: "DeadLetterEvents",
                type: "TEXT",
                maxLength: 55,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceParent",
                table: "DeadLetterEvents",
                type: "TEXT",
                maxLength: 55,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalTraceParent",
                table: "QueuedEvents");

            migrationBuilder.DropColumn(
                name: "TraceParent",
                table: "QueuedEvents");

            migrationBuilder.DropColumn(
                name: "OriginalTraceParent",
                table: "DeadLetterEvents");

            migrationBuilder.DropColumn(
                name: "TraceParent",
                table: "DeadLetterEvents");
        }
    }
}

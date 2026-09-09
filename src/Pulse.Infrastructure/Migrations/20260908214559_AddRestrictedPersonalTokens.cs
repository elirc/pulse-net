using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRestrictedPersonalTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ExpiresAt",
                table: "PersonalApiKeys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "PersonalApiKeys",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "LegacyUnrestricted");

            migrationBuilder.CreateTable(
                name: "PersonalKeyProjects",
                columns: table => new
                {
                    KeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalKeyProjects", x => new { x.KeyId, x.ProjectId });
                });

            migrationBuilder.CreateTable(
                name: "PersonalKeyScopes",
                columns: table => new
                {
                    KeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalKeyScopes", x => new { x.KeyId, x.Scope });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersonalKeyProjects");

            migrationBuilder.DropTable(
                name: "PersonalKeyScopes");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "PersonalApiKeys");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "PersonalApiKeys");
        }
    }
}

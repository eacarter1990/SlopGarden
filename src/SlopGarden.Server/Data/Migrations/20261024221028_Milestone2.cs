using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlopGarden.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Milestone2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_update",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "notified_update_version",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_error",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "update_status",
                table: "devices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image",
                table: "client_releases",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "growth_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    conditions = table.Column<string>(type: "jsonb", nullable: false),
                    predictions = table.Column<string>(type: "jsonb", nullable: false),
                    image_ids = table.Column<string>(type: "jsonb", nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_growth_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_growth_reports_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_growth_reports_garden_id_created_at",
                table: "growth_reports",
                columns: new[] { "garden_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "growth_reports");

            migrationBuilder.DropColumn(
                name: "auto_update",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "notified_update_version",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "update_error",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "update_status",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "image",
                table: "client_releases");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlopGarden.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class HydroponicMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_care_targets_scope_type_bed_id_plant_id_metric",
                table: "care_targets");

            migrationBuilder.AddColumn<Guid>(
                name: "reservoir_id",
                table: "sensors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reservoir_id",
                table: "journal_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "default_growing_medium",
                table: "gardens",
                type: "text",
                nullable: false,
                defaultValue: "soil");

            migrationBuilder.AddColumn<Guid>(
                name: "reservoir_id",
                table: "care_targets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reservoir_id",
                table: "cameras",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "growing_medium",
                table: "beds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hydro_system",
                table: "beds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reservoir_id",
                table: "beds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "allow_standing",
                table: "ai_proposals",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "continuous_run",
                table: "actuators",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "flow_channel_id",
                table: "actuators",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "flow_grace_sec",
                table: "actuators",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "flow_max_when_off",
                table: "actuators",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "flow_min_when_on",
                table: "actuators",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reservoir_id",
                table: "actuators",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "actuators",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "stop_on_stall",
                table: "actuators",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "reservoirs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    volume_liters = table.Column<double>(type: "double precision", nullable: true),
                    last_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    change_interval_days = table.Column<int>(type: "integer", nullable: true),
                    topped_up_liters_since_change = table.Column<double>(type: "double precision", nullable: false),
                    ai_context = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservoirs", x => x.id);
                    table.ForeignKey(
                        name: "fk_reservoirs_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sensors_reservoir_id",
                table: "sensors",
                column: "reservoir_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_targets_reservoir_id",
                table: "care_targets",
                column: "reservoir_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_targets_scope_type_bed_id_plant_id_reservoir_id_metric",
                table: "care_targets",
                columns: new[] { "scope_type", "bed_id", "plant_id", "reservoir_id", "metric" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_cameras_reservoir_id",
                table: "cameras",
                column: "reservoir_id");

            migrationBuilder.CreateIndex(
                name: "ix_beds_reservoir_id",
                table: "beds",
                column: "reservoir_id");

            migrationBuilder.CreateIndex(
                name: "ix_actuators_reservoir_id",
                table: "actuators",
                column: "reservoir_id");

            migrationBuilder.CreateIndex(
                name: "ix_reservoirs_garden_id",
                table: "reservoirs",
                column: "garden_id");

            migrationBuilder.AddForeignKey(
                name: "fk_actuators_reservoirs_reservoir_id",
                table: "actuators",
                column: "reservoir_id",
                principalTable: "reservoirs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_beds_reservoirs_reservoir_id",
                table: "beds",
                column: "reservoir_id",
                principalTable: "reservoirs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_cameras_reservoirs_reservoir_id",
                table: "cameras",
                column: "reservoir_id",
                principalTable: "reservoirs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_care_targets_reservoirs_reservoir_id",
                table: "care_targets",
                column: "reservoir_id",
                principalTable: "reservoirs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_sensors_reservoirs_reservoir_id",
                table: "sensors",
                column: "reservoir_id",
                principalTable: "reservoirs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_actuators_reservoirs_reservoir_id",
                table: "actuators");

            migrationBuilder.DropForeignKey(
                name: "fk_beds_reservoirs_reservoir_id",
                table: "beds");

            migrationBuilder.DropForeignKey(
                name: "fk_cameras_reservoirs_reservoir_id",
                table: "cameras");

            migrationBuilder.DropForeignKey(
                name: "fk_care_targets_reservoirs_reservoir_id",
                table: "care_targets");

            migrationBuilder.DropForeignKey(
                name: "fk_sensors_reservoirs_reservoir_id",
                table: "sensors");

            migrationBuilder.DropTable(
                name: "reservoirs");

            migrationBuilder.DropIndex(
                name: "ix_sensors_reservoir_id",
                table: "sensors");

            migrationBuilder.DropIndex(
                name: "ix_care_targets_reservoir_id",
                table: "care_targets");

            migrationBuilder.DropIndex(
                name: "ix_care_targets_scope_type_bed_id_plant_id_reservoir_id_metric",
                table: "care_targets");

            migrationBuilder.DropIndex(
                name: "ix_cameras_reservoir_id",
                table: "cameras");

            migrationBuilder.DropIndex(
                name: "ix_beds_reservoir_id",
                table: "beds");

            migrationBuilder.DropIndex(
                name: "ix_actuators_reservoir_id",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "reservoir_id",
                table: "sensors");

            migrationBuilder.DropColumn(
                name: "reservoir_id",
                table: "journal_entries");

            migrationBuilder.DropColumn(
                name: "default_growing_medium",
                table: "gardens");

            migrationBuilder.DropColumn(
                name: "reservoir_id",
                table: "care_targets");

            migrationBuilder.DropColumn(
                name: "reservoir_id",
                table: "cameras");

            migrationBuilder.DropColumn(
                name: "growing_medium",
                table: "beds");

            migrationBuilder.DropColumn(
                name: "hydro_system",
                table: "beds");

            migrationBuilder.DropColumn(
                name: "reservoir_id",
                table: "beds");

            migrationBuilder.DropColumn(
                name: "allow_standing",
                table: "ai_proposals");

            migrationBuilder.DropColumn(
                name: "continuous_run",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "flow_channel_id",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "flow_grace_sec",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "flow_max_when_off",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "flow_min_when_on",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "reservoir_id",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "role",
                table: "actuators");

            migrationBuilder.DropColumn(
                name: "stop_on_stall",
                table: "actuators");

            migrationBuilder.CreateIndex(
                name: "ix_care_targets_scope_type_bed_id_plant_id_metric",
                table: "care_targets",
                columns: new[] { "scope_type", "bed_id", "plant_id", "metric" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}

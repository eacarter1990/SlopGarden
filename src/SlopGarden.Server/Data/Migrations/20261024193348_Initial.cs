using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SlopGarden.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_activities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_proposals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin = table.Column<string>(type: "text", nullable: false),
                    requested_by_type = table.Column<string>(type: "text", nullable: false),
                    requested_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tool_name = table.Column<string>(type: "text", nullable: false),
                    arguments = table.Column<string>(type: "jsonb", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    rationale = table.Column<string>(type: "text", nullable: false),
                    changes = table.Column<string>(type: "jsonb", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: true),
                    risk = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_proposals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_standing_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool_name = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    constraints = table.Column<string>(type: "jsonb", nullable: false),
                    created_from_proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_standing_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_label = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    rid = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_releases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device_updates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_updates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "emergency_stops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "text", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    activated_by_type = table.Column<string>(type: "text", nullable: false),
                    activated_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_emergency_stops", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gardens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    environment = table.Column<string>(type: "text", nullable: false),
                    location_source = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    elevation_m = table.Column<double>(type: "double precision", nullable: true),
                    location_label = table.Column<string>(type: "text", nullable: true),
                    timezone_id = table.Column<string>(type: "text", nullable: false),
                    timezone_source = table.Column<string>(type: "text", nullable: false),
                    hardiness_zone = table.Column<string>(type: "text", nullable: true),
                    climate_notes = table.Column<string>(type: "text", nullable: true),
                    ai_context = table.Column<string>(type: "text", nullable: false),
                    ai_autonomy = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gardens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hardware_catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    manufacturer = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    metrics = table.Column<List<string>>(type: "text[]", nullable: false),
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    recommended = table.Column<bool>(type: "boolean", nullable: false),
                    tested_status = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    datasheet_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hardware_catalog", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    camera_id = table.Column<Guid>(type: "uuid", nullable: true),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: true),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    min_severity = table.Column<string>(type: "text", nullable: false),
                    categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_mutes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "text", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "text", nullable: true),
                    muted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_mutes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dedupe_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plant_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    common_name = table.Column<string>(type: "text", nullable: false),
                    scientific_name = table.Column<string>(type: "text", nullable: true),
                    family = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    water_need = table.Column<string>(type: "text", nullable: false),
                    soil_moisture_min_pct = table.Column<double>(type: "double precision", nullable: true),
                    soil_moisture_max_pct = table.Column<double>(type: "double precision", nullable: true),
                    light_hours_min = table.Column<double>(type: "double precision", nullable: true),
                    light_hours_max = table.Column<double>(type: "double precision", nullable: true),
                    dli_min = table.Column<double>(type: "double precision", nullable: true),
                    air_temp_min_c = table.Column<double>(type: "double precision", nullable: true),
                    air_temp_max_c = table.Column<double>(type: "double precision", nullable: true),
                    soil_temp_min_c = table.Column<double>(type: "double precision", nullable: true),
                    humidity_min_pct = table.Column<double>(type: "double precision", nullable: true),
                    humidity_max_pct = table.Column<double>(type: "double precision", nullable: true),
                    ph_min = table.Column<double>(type: "double precision", nullable: true),
                    ph_max = table.Column<double>(type: "double precision", nullable: true),
                    ec_min = table.Column<double>(type: "double precision", nullable: true),
                    ec_max = table.Column<double>(type: "double precision", nullable: true),
                    fertilizer_notes = table.Column<string>(type: "text", nullable: true),
                    days_to_germination = table.Column<int>(type: "integer", nullable: true),
                    days_to_maturity_min = table.Column<int>(type: "integer", nullable: true),
                    days_to_maturity_max = table.Column<int>(type: "integer", nullable: true),
                    spacing_cm = table.Column<double>(type: "double precision", nullable: true),
                    frost_tolerance = table.Column<string>(type: "text", nullable: false),
                    sources = table.Column<string>(type: "jsonb", nullable: false),
                    is_builtin = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_type = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plant_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plugin_signing_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: false),
                    algorithm = table.Column<string>(type: "text", nullable: false),
                    public_key_spki = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plugin_signing_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    ciphertext = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_secrets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "server_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    is_disabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "beds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    environment = table.Column<string>(type: "text", nullable: true),
                    width_cm = table.Column<double>(type: "double precision", nullable: true),
                    length_cm = table.Column<double>(type: "double precision", nullable: true),
                    depth_cm = table.Column<double>(type: "double precision", nullable: true),
                    soil_type = table.Column<string>(type: "text", nullable: true),
                    soil_notes = table.Column<string>(type: "text", nullable: true),
                    irrigation_type = table.Column<string>(type: "text", nullable: false),
                    sun_exposure = table.Column<string>(type: "text", nullable: false),
                    layout_x = table.Column<double>(type: "double precision", nullable: true),
                    layout_y = table.Column<double>(type: "double precision", nullable: true),
                    ai_context = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beds", x => x.id);
                    table.ForeignKey(
                        name: "fk_beds_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    hardware_id = table.Column<string>(type: "text", nullable: false),
                    hostname = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    discovered_via = table.Column<string>(type: "text", nullable: false),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    local_endpoint = table.Column<string>(type: "text", nullable: true),
                    os = table.Column<string>(type: "text", nullable: false),
                    architecture = table.Column<string>(type: "text", nullable: false),
                    client_version = table.Column<string>(type: "text", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: true),
                    registration_secret_hash = table.Column<string>(type: "text", nullable: false),
                    credential_hash = table.Column<string>(type: "text", nullable: true),
                    pending_credential_ciphertext = table.Column<string>(type: "text", nullable: true),
                    credential_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reported_timezone_id = table.Column<string>(type: "text", nullable: true),
                    reported_utc_offset_min = table.Column<double>(type: "double precision", nullable: true),
                    reported_latitude = table.Column<double>(type: "double precision", nullable: true),
                    reported_longitude = table.Column<double>(type: "double precision", nullable: true),
                    reported_location_label = table.Column<string>(type: "text", nullable: true),
                    desired_config_version = table.Column<int>(type: "integer", nullable: false),
                    applied_config_version = table.Column<int>(type: "integer", nullable: true),
                    config_errors = table.Column<string>(type: "jsonb", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_online = table.Column<bool>(type: "boolean", nullable: false),
                    buffered_readings = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_devices_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "journal_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    quantity = table.Column<double>(type: "double precision", nullable: true),
                    unit = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_type = table.Column<string>(type: "text", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journal_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_journal_entries_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_deliveries_notifications_notification_id",
                        column: x => x.notification_id,
                        principalTable: "notifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: false),
                    kinds = table.Column<List<string>>(type: "text[]", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    signature = table.Column<string>(type: "text", nullable: false),
                    signing_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    homepage_url = table.Column<string>(type: "text", nullable: true),
                    settings_schema = table.Column<string>(type: "jsonb", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plugin_catalog", x => x.id);
                    table.ForeignKey(
                        name: "fk_plugin_catalog_plugin_signing_keys_signing_key_id",
                        column: x => x.signing_key_id,
                        principalTable: "plugin_signing_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: false),
                    api_key_secret_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_model = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_providers", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_providers_secrets_api_key_secret_id",
                        column: x => x.api_key_secret_id,
                        principalTable: "secrets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "chat_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_page_context = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_conversations", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_conversations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "personal_access_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    token_prefix = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personal_access_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_personal_access_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plant_profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    variety = table.Column<string>(type: "text", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    layout_x = table.Column<double>(type: "double precision", nullable: true),
                    layout_y = table.Column<double>(type: "double precision", nullable: true),
                    stage = table.Column<string>(type: "text", nullable: false),
                    started_from = table.Column<string>(type: "text", nullable: false),
                    planted_at = table.Column<DateOnly>(type: "date", nullable: true),
                    expected_harvest_start = table.Column<DateOnly>(type: "date", nullable: true),
                    expected_harvest_end = table.Column<DateOnly>(type: "date", nullable: true),
                    harvest_prediction_notes = table.Column<string>(type: "text", nullable: true),
                    ai_context = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plants", x => x.id);
                    table.ForeignKey(
                        name: "fk_plants_beds_bed_id",
                        column: x => x.bed_id,
                        principalTable: "beds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_plants_plant_profiles_plant_profile_id",
                        column: x => x.plant_profile_id,
                        principalTable: "plant_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "device_config_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    document = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: true),
                    errors = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_config_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_config_revisions_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    category = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    sensor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actuator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    camera_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_events_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_plugins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    kinds = table.Column<List<string>>(type: "text[]", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    trust = table.Column<string>(type: "text", nullable: false),
                    publisher = table.Column<string>(type: "text", nullable: true),
                    signature_key_id = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    catalog_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_plugins", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_plugins_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discovered_hardware",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    suggested_name = table.Column<string>(type: "text", nullable: false),
                    suggested_settings = table.Column<string>(type: "jsonb", nullable: false),
                    channels = table.Column<string>(type: "jsonb", nullable: false),
                    actuator_kind = table.Column<string>(type: "text", nullable: true),
                    adopted_component_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discovered_hardware", x => x.id);
                    table.ForeignKey(
                        name: "fk_discovered_hardware_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_model_assignments",
                columns: table => new
                {
                    job = table.Column<string>(type: "text", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    temperature = table.Column<double>(type: "double precision", nullable: true),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: true),
                    context_message_limit = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_model_assignments", x => x.job);
                    table.ForeignKey(
                        name: "fk_ai_model_assignments_ai_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "ai_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    tool_calls = table.Column<string>(type: "jsonb", nullable: true),
                    tool_call_id = table.Column<string>(type: "text", nullable: true),
                    tool_name = table.Column<string>(type: "text", nullable: true),
                    page_context = table.Column<string>(type: "jsonb", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_messages_chat_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "chat_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "actuators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    failsafe_state = table.Column<string>(type: "text", nullable: false),
                    flow_rate_ml_per_min = table.Column<double>(type: "double precision", nullable: true),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_on = table.Column<bool>(type: "boolean", nullable: false),
                    level = table.Column<double>(type: "double precision", nullable: true),
                    state_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actuators", x => x.id);
                    table.ForeignKey(
                        name: "fk_actuators_beds_bed_id",
                        column: x => x.bed_id,
                        principalTable: "beds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_actuators_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_actuators_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_actuators_plants_plant_id",
                        column: x => x.plant_id,
                        principalTable: "plants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "cameras",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    capture_width = table.Column<int>(type: "integer", nullable: false),
                    capture_height = table.Column<int>(type: "integer", nullable: false),
                    frame_interval_sec = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_frame_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cameras", x => x.id);
                    table.ForeignKey(
                        name: "fk_cameras_beds_bed_id",
                        column: x => x.bed_id,
                        principalTable: "beds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_cameras_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cameras_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_cameras_plants_plant_id",
                        column: x => x.plant_id,
                        principalTable: "plants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "care_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "text", nullable: false),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metric = table.Column<string>(type: "text", nullable: false),
                    min_value = table.Column<double>(type: "double precision", nullable: true),
                    max_value = table.Column<double>(type: "double precision", nullable: true),
                    ideal_value = table.Column<double>(type: "double precision", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    rationale = table.Column<string>(type: "text", nullable: true),
                    updated_by_type = table.Column<string>(type: "text", nullable: false),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_care_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_care_targets_beds_bed_id",
                        column: x => x.bed_id,
                        principalTable: "beds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_care_targets_plants_plant_id",
                        column: x => x.plant_id,
                        principalTable: "plants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sensors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    plugin_id = table.Column<string>(type: "text", nullable: false),
                    hardware_catalog_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    read_interval_sec = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensors", x => x.id);
                    table.ForeignKey(
                        name: "fk_sensors_beds_bed_id",
                        column: x => x.bed_id,
                        principalTable: "beds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_sensors_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sensors_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sensors_plants_plant_id",
                        column: x => x.plant_id,
                        principalTable: "plants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "actuator_commands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actuator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    duration_sec = table.Column<double>(type: "double precision", nullable: true),
                    amount_ml = table.Column<double>(type: "double precision", nullable: true),
                    level = table.Column<double>(type: "double precision", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    requested_by_type = table.Column<string>(type: "text", nullable: false),
                    requested_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    blocked_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actuator_commands", x => x.id);
                    table.ForeignKey(
                        name: "fk_actuator_commands_actuators_actuator_id",
                        column: x => x.actuator_id,
                        principalTable: "actuators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "actuator_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actuator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_sec = table.Column<double>(type: "double precision", nullable: false),
                    amount_ml = table.Column<double>(type: "double precision", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    stop_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actuator_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_actuator_runs_actuators_actuator_id",
                        column: x => x.actuator_id,
                        principalTable: "actuators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    garden_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    cron = table.Column<string>(type: "text", nullable: false),
                    timezone_id = table.Column<string>(type: "text", nullable: false),
                    actuator_id = table.Column<Guid>(type: "uuid", nullable: true),
                    duration_sec = table.Column<int>(type: "integer", nullable: true),
                    amount_ml = table.Column<double>(type: "double precision", nullable: true),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    bed_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_type = table.Column<string>(type: "text", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedules", x => x.id);
                    table.ForeignKey(
                        name: "fk_schedules_actuators_actuator_id",
                        column: x => x.actuator_id,
                        principalTable: "actuators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_schedules_gardens_garden_id",
                        column: x => x.garden_id,
                        principalTable: "gardens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sensor_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sensor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    metric = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    calibration = table.Column<string>(type: "jsonb", nullable: true),
                    calibration_status = table.Column<string>(type: "text", nullable: false),
                    calibrated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    min_valid = table.Column<double>(type: "double precision", nullable: true),
                    max_valid = table.Column<double>(type: "double precision", nullable: true),
                    alert_low = table.Column<double>(type: "double precision", nullable: true),
                    alert_high = table.Column<double>(type: "double precision", nullable: true),
                    last_value = table.Column<double>(type: "double precision", nullable: true),
                    last_raw_value = table.Column<double>(type: "double precision", nullable: true),
                    last_quality = table.Column<short>(type: "smallint", nullable: true),
                    last_value_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensor_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_sensor_channels_sensors_sensor_id",
                        column: x => x.sensor_id,
                        principalTable: "sensors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schedule_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedule_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_schedule_runs_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "readings",
                columns: table => new
                {
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    raw_value = table.Column<double>(type: "double precision", nullable: true),
                    quality = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_readings", x => new { x.channel_id, x.time });
                    table.ForeignKey(
                        name: "fk_readings_sensor_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "sensor_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "safety_limits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "text", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    limit_type = table.Column<string>(type: "text", nullable: false),
                    actuator_kind = table.Column<string>(type: "text", nullable: true),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    @operator = table.Column<string>(name: "operator", type: "text", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: false),
                    created_by_type = table.Column<string>(type: "text", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_safety_limits", x => x.id);
                    table.ForeignKey(
                        name: "fk_safety_limits_sensor_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "sensor_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_actuator_commands_actuator_id_created_at",
                table: "actuator_commands",
                columns: new[] { "actuator_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_actuator_runs_actuator_id_started_at",
                table: "actuator_runs",
                columns: new[] { "actuator_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_actuators_bed_id",
                table: "actuators",
                column: "bed_id");

            migrationBuilder.CreateIndex(
                name: "ix_actuators_device_id",
                table: "actuators",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_actuators_garden_id",
                table: "actuators",
                column: "garden_id");

            migrationBuilder.CreateIndex(
                name: "ix_actuators_plant_id",
                table: "actuators",
                column: "plant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_activities_status_started_at",
                table: "ai_activities",
                columns: new[] { "status", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_model_assignments_provider_id",
                table: "ai_model_assignments",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_proposals_status_created_at",
                table: "ai_proposals",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_providers_api_key_secret_id",
                table: "ai_providers",
                column: "api_key_secret_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_at",
                table: "audit_log",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "ix_beds_garden_id",
                table: "beds",
                column: "garden_id");

            migrationBuilder.CreateIndex(
                name: "ix_cameras_bed_id",
                table: "cameras",
                column: "bed_id");

            migrationBuilder.CreateIndex(
                name: "ix_cameras_device_id",
                table: "cameras",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_cameras_garden_id",
                table: "cameras",
                column: "garden_id");

            migrationBuilder.CreateIndex(
                name: "ix_cameras_plant_id",
                table: "cameras",
                column: "plant_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_targets_bed_id",
                table: "care_targets",
                column: "bed_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_targets_plant_id",
                table: "care_targets",
                column: "plant_id");

            migrationBuilder.CreateIndex(
                name: "ix_care_targets_scope_type_bed_id_plant_id_metric",
                table: "care_targets",
                columns: new[] { "scope_type", "bed_id", "plant_id", "metric" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_chat_conversations_user_id_updated_at",
                table: "chat_conversations",
                columns: new[] { "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_conversation_id_seq",
                table: "chat_messages",
                columns: new[] { "conversation_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_config_revisions_device_id_version",
                table: "device_config_revisions",
                columns: new[] { "device_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_events_device_id_time",
                table: "device_events",
                columns: new[] { "device_id", "time" });

            migrationBuilder.CreateIndex(
                name: "ix_device_plugins_device_id_plugin_id",
                table: "device_plugins",
                columns: new[] { "device_id", "plugin_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_garden_id",
                table: "devices",
                column: "garden_id");

            migrationBuilder.CreateIndex(
                name: "ix_devices_hardware_id",
                table: "devices",
                column: "hardware_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_discovered_hardware_device_id_plugin_id_address",
                table: "discovered_hardware",
                columns: new[] { "device_id", "plugin_id", "address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_emergency_stops_active",
                table: "emergency_stops",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_journal_entries_garden_id_occurred_at",
                table: "journal_entries",
                columns: new[] { "garden_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_media_camera_id_captured_at",
                table: "media",
                columns: new[] { "camera_id", "captured_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_notification_id",
                table: "notification_deliveries",
                column: "notification_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_mutes_scope_type_scope_id",
                table: "notification_mutes",
                columns: new[] { "scope_type", "scope_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_created_at",
                table: "notifications",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_personal_access_tokens_token_hash",
                table: "personal_access_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_personal_access_tokens_user_id",
                table: "personal_access_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_plant_profiles_slug",
                table: "plant_profiles",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plants_bed_id",
                table: "plants",
                column: "bed_id");

            migrationBuilder.CreateIndex(
                name: "ix_plants_plant_profile_id",
                table: "plants",
                column: "plant_profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_plugin_catalog_plugin_id_version_sha256",
                table: "plugin_catalog",
                columns: new[] { "plugin_id", "version", "sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plugin_catalog_signing_key_id",
                table: "plugin_catalog",
                column: "signing_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_plugin_signing_keys_key_id",
                table: "plugin_signing_keys",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_safety_limits_channel_id",
                table: "safety_limits",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_safety_limits_scope_type_scope_id",
                table: "safety_limits",
                columns: new[] { "scope_type", "scope_id" });

            migrationBuilder.CreateIndex(
                name: "ix_schedule_runs_schedule_id",
                table: "schedule_runs",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedules_actuator_id",
                table: "schedules",
                column: "actuator_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedules_garden_id",
                table: "schedules",
                column: "garden_id");

            migrationBuilder.CreateIndex(
                name: "ix_secrets_name",
                table: "secrets",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sensor_channels_sensor_id_key",
                table: "sensor_channels",
                columns: new[] { "sensor_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sensors_bed_id",
                table: "sensors",
                column: "bed_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensors_device_id",
                table: "sensors",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensors_garden_id",
                table: "sensors",
                column: "garden_id");

            migrationBuilder.CreateIndex(
                name: "ix_sensors_plant_id",
                table: "sensors",
                column: "plant_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actuator_commands");

            migrationBuilder.DropTable(
                name: "actuator_runs");

            migrationBuilder.DropTable(
                name: "ai_activities");

            migrationBuilder.DropTable(
                name: "ai_model_assignments");

            migrationBuilder.DropTable(
                name: "ai_proposals");

            migrationBuilder.DropTable(
                name: "ai_standing_approvals");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "cameras");

            migrationBuilder.DropTable(
                name: "care_targets");

            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "client_releases");

            migrationBuilder.DropTable(
                name: "device_config_revisions");

            migrationBuilder.DropTable(
                name: "device_events");

            migrationBuilder.DropTable(
                name: "device_plugins");

            migrationBuilder.DropTable(
                name: "device_updates");

            migrationBuilder.DropTable(
                name: "discovered_hardware");

            migrationBuilder.DropTable(
                name: "emergency_stops");

            migrationBuilder.DropTable(
                name: "hardware_catalog");

            migrationBuilder.DropTable(
                name: "journal_entries");

            migrationBuilder.DropTable(
                name: "media");

            migrationBuilder.DropTable(
                name: "notification_channels");

            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_mutes");

            migrationBuilder.DropTable(
                name: "personal_access_tokens");

            migrationBuilder.DropTable(
                name: "plugin_catalog");

            migrationBuilder.DropTable(
                name: "readings");

            migrationBuilder.DropTable(
                name: "safety_limits");

            migrationBuilder.DropTable(
                name: "schedule_runs");

            migrationBuilder.DropTable(
                name: "server_settings");

            migrationBuilder.DropTable(
                name: "ai_providers");

            migrationBuilder.DropTable(
                name: "chat_conversations");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "plugin_signing_keys");

            migrationBuilder.DropTable(
                name: "sensor_channels");

            migrationBuilder.DropTable(
                name: "schedules");

            migrationBuilder.DropTable(
                name: "secrets");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "sensors");

            migrationBuilder.DropTable(
                name: "actuators");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "plants");

            migrationBuilder.DropTable(
                name: "beds");

            migrationBuilder.DropTable(
                name: "plant_profiles");

            migrationBuilder.DropTable(
                name: "gardens");
        }
    }
}

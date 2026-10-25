using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlopGarden.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceCanSelfUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "can_self_update",
                table: "devices",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "can_self_update",
                table: "devices");
        }
    }
}

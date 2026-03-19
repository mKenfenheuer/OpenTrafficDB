using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyWaySpeedSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "P50Speed",
                table: "WaySpeedSummaries");

            migrationBuilder.DropColumn(
                name: "P95Speed",
                table: "WaySpeedSummaries");

            migrationBuilder.DropColumn(
                name: "TotalSamples",
                table: "WaySpeedSummaries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "P50Speed",
                table: "WaySpeedSummaries",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "P95Speed",
                table: "WaySpeedSummaries",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "TotalSamples",
                table: "WaySpeedSummaries",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}

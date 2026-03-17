using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeedLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SpeedLimitKmh",
                table: "WaySpeedAggregates",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SpeedLimitKmh",
                table: "SpeedObservations",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpeedLimitKmh",
                table: "WaySpeedAggregates");

            migrationBuilder.DropColumn(
                name: "SpeedLimitKmh",
                table: "SpeedObservations");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDBMigrations
{
    /// <inheritdoc />
    public partial class AddWaySpeedSummaryRoadClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "RoadClass",
                table: "WaySpeedSummaries",
                type: "smallint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoadClass",
                table: "WaySpeedSummaries");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDBMigrations
{
    /// <inheritdoc />
    public partial class DropWaySpeedSummaryLastUpdatedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaySpeedSummaries_LastUpdated",
                table: "WaySpeedSummaries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WaySpeedSummaries_LastUpdated",
                table: "WaySpeedSummaries",
                column: "LastUpdated");
        }
    }
}

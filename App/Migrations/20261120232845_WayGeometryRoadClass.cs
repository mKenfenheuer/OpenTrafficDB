using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDBMigrations
{
    /// <inheritdoc />
    public partial class WayGeometryRoadClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "RoadClass",
                table: "WayGeometries",
                type: "smallint",
                nullable: false,
                defaultValue: (short)5);

            // Existing rows: take the class from the matched speed data (same OSM highway codes).
            // The next OSM import sets it for every way.
            migrationBuilder.Sql("""
                UPDATE "WayGeometries" wg
                SET "RoadClass" = wss."RoadClass"
                FROM "WaySpeedSummaries" wss
                WHERE wss."OsmWayId" = wg."OsmWayId"
                  AND wss."RoadClass" IS NOT NULL
                  AND wss."RoadClass" < 5;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_BBox_Class1",
                table: "WayGeometries",
                column: "BBox",
                filter: "\"RoadClass\" <= 1")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_BBox_Class2",
                table: "WayGeometries",
                column: "BBox",
                filter: "\"RoadClass\" <= 2")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_BBox_Class3",
                table: "WayGeometries",
                column: "BBox",
                filter: "\"RoadClass\" <= 3")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_BBox_Class4",
                table: "WayGeometries",
                column: "BBox",
                filter: "\"RoadClass\" <= 4")
                .Annotation("Npgsql:IndexMethod", "GIST");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WayGeometries_BBox_Class1",
                table: "WayGeometries");

            migrationBuilder.DropIndex(
                name: "IX_WayGeometries_BBox_Class2",
                table: "WayGeometries");

            migrationBuilder.DropIndex(
                name: "IX_WayGeometries_BBox_Class3",
                table: "WayGeometries");

            migrationBuilder.DropIndex(
                name: "IX_WayGeometries_BBox_Class4",
                table: "WayGeometries");

            migrationBuilder.DropColumn(
                name: "RoadClass",
                table: "WayGeometries");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class UpdatePlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WayGeometries_MinLat_MaxLat",
                table: "WayGeometries");

            migrationBuilder.DropIndex(
                name: "IX_WayGeometries_MinLon_MaxLon",
                table: "WayGeometries");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.AddColumn<Geometry>(
                name: "BBox",
                table: "WayGeometries",
                type: "geometry(Polygon,4326)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_BBox",
                table: "WayGeometries",
                column: "BBox")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_Timestamp_Latitude_Longitude",
                table: "SpeedObservations",
                columns: new[] { "Timestamp", "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_Timestamp_OsmWayId",
                table: "SpeedObservations",
                columns: new[] { "Timestamp", "OsmWayId" },
                filter: "\"OsmWayId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WayGeometries_BBox",
                table: "WayGeometries");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_Timestamp_Latitude_Longitude",
                table: "SpeedObservations");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_Timestamp_OsmWayId",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "BBox",
                table: "WayGeometries");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_MinLat_MaxLat",
                table: "WayGeometries",
                columns: new[] { "MinLat", "MaxLat" });

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_MinLon_MaxLon",
                table: "WayGeometries",
                columns: new[] { "MinLon", "MaxLon" });
        }
    }
}

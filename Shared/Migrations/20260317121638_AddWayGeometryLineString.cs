using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class AddWayGeometryLineString : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncodedPolyline",
                table: "WayGeometries");

            migrationBuilder.AddColumn<Geometry>(
                name: "Geometry",
                table: "WayGeometries",
                type: "geometry(LineString,4326)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Geometry",
                table: "WayGeometries");

            migrationBuilder.AddColumn<string>(
                name: "EncodedPolyline",
                table: "WayGeometries",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}

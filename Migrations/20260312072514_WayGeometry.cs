using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class WayGeometry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WayGeometries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    MinLat = table.Column<double>(type: "double precision", nullable: false),
                    MinLon = table.Column<double>(type: "double precision", nullable: false),
                    MaxLat = table.Column<double>(type: "double precision", nullable: false),
                    MaxLon = table.Column<double>(type: "double precision", nullable: false),
                    EncodedPolyline = table.Column<string>(type: "text", nullable: false),
                    MaxSpeedKmh = table.Column<double>(type: "double precision", nullable: true),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WayGeometries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_MinLat_MaxLat",
                table: "WayGeometries",
                columns: new[] { "MinLat", "MaxLat" });

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_MinLon_MaxLon",
                table: "WayGeometries",
                columns: new[] { "MinLon", "MaxLon" });

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_OsmWayId",
                table: "WayGeometries",
                column: "OsmWayId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WayGeometries");
        }
    }
}

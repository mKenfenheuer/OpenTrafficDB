using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDBMigrations
{
    /// <inheritdoc />
    public partial class WayClosures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WayClosures",
                columns: table => new
                {
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    FirstReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WayClosures", x => x.OsmWayId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WayClosures_ExpiresAt",
                table: "WayClosures",
                column: "ExpiresAt");

            // Closures were point hazards snapped one by one (often onto a parallel carriageway);
            // they now arrive as trace-matched lines. Drop the old points.
            migrationBuilder.Sql("""
                DELETE FROM "HazardAggregates" WHERE "HazardType" = 'RoadClosure';
                DELETE FROM "HazardObservations" WHERE "HazardType" = 'RoadClosure';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WayClosures");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class WaySpeedSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WaySpeedSummaries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    FreeflowSpeed = table.Column<double>(type: "double precision", nullable: false),
                    ConstrainedSpeed = table.Column<double>(type: "double precision", nullable: true),
                    P50Speed = table.Column<double>(type: "double precision", nullable: false),
                    P95Speed = table.Column<double>(type: "double precision", nullable: false),
                    SpeedLimitKmh = table.Column<double>(type: "double precision", nullable: true),
                    TotalSamples = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaySpeedSummaries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaySpeedSummaries_OsmWayId",
                table: "WaySpeedSummaries",
                column: "OsmWayId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaySpeedSummaries");
        }
    }
}

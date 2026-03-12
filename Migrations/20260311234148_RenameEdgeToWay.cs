using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class RenameEdgeToWay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EdgeSpeedAggregates");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_OsmWayId_OsmStartNodeId_OsmEndNodeId",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "OsmEndNodeId",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "OsmStartNodeId",
                table: "SpeedObservations");

            migrationBuilder.CreateTable(
                name: "WaySpeedAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    FiveMinuteBucket = table.Column<int>(type: "integer", nullable: false),
                    SumSpeedKmh = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaySpeedAggregates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_OsmWayId",
                table: "SpeedObservations",
                column: "OsmWayId",
                filter: "\"OsmWayId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WaySpeedAggregates_OsmWayId_DayOfWeek_FiveMinuteBucket",
                table: "WaySpeedAggregates",
                columns: new[] { "OsmWayId", "DayOfWeek", "FiveMinuteBucket" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaySpeedAggregates");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_OsmWayId",
                table: "SpeedObservations");

            migrationBuilder.AddColumn<long>(
                name: "OsmEndNodeId",
                table: "SpeedObservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OsmStartNodeId",
                table: "SpeedObservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EdgeSpeedAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    FiveMinuteBucket = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OsmEndNodeId = table.Column<long>(type: "bigint", nullable: false),
                    OsmStartNodeId = table.Column<long>(type: "bigint", nullable: false),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    SumSpeedKmh = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EdgeSpeedAggregates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_OsmWayId_OsmStartNodeId_OsmEndNodeId",
                table: "SpeedObservations",
                columns: new[] { "OsmWayId", "OsmStartNodeId", "OsmEndNodeId" },
                filter: "\"OsmWayId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EdgeSpeedAggregates_OsmWayId_OsmStartNodeId_OsmEndNodeId_Da~",
                table: "EdgeSpeedAggregates",
                columns: new[] { "OsmWayId", "OsmStartNodeId", "OsmEndNodeId", "DayOfWeek", "FiveMinuteBucket" },
                unique: true);
        }
    }
}

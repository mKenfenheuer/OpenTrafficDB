using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class HybridAggregation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoadSpeedAggregates");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_ValhallaEdgeId",
                table: "SpeedObservations");

            migrationBuilder.RenameColumn(
                name: "ValhallaEdgeId",
                table: "SpeedObservations",
                newName: "OsmWayId");

            migrationBuilder.AddColumn<long>(
                name: "H3CellIndex",
                table: "SpeedObservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsForward",
                table: "SpeedObservations",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EdgeSpeedAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    IsForward = table.Column<bool>(type: "boolean", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    FiveMinuteBucket = table.Column<int>(type: "integer", nullable: false),
                    SumSpeedKmh = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ValhallaEdgeId = table.Column<long>(type: "bigint", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EdgeSpeedAggregates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "H3SpeedAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    H3CellIndex = table.Column<long>(type: "bigint", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    FiveMinuteBucket = table.Column<int>(type: "integer", nullable: false),
                    SumSpeedKmh = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_H3SpeedAggregates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_H3CellIndex",
                table: "SpeedObservations",
                column: "H3CellIndex",
                filter: "\"H3CellIndex\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_OsmWayId",
                table: "SpeedObservations",
                column: "OsmWayId",
                filter: "\"OsmWayId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EdgeSpeedAggregates_OsmWayId_IsForward_DayOfWeek_FiveMinute~",
                table: "EdgeSpeedAggregates",
                columns: new[] { "OsmWayId", "IsForward", "DayOfWeek", "FiveMinuteBucket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EdgeSpeedAggregates_ValhallaEdgeId",
                table: "EdgeSpeedAggregates",
                column: "ValhallaEdgeId",
                filter: "\"ValhallaEdgeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_H3SpeedAggregates_H3CellIndex",
                table: "H3SpeedAggregates",
                column: "H3CellIndex");

            migrationBuilder.CreateIndex(
                name: "IX_H3SpeedAggregates_H3CellIndex_DayOfWeek_FiveMinuteBucket",
                table: "H3SpeedAggregates",
                columns: new[] { "H3CellIndex", "DayOfWeek", "FiveMinuteBucket" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EdgeSpeedAggregates");

            migrationBuilder.DropTable(
                name: "H3SpeedAggregates");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_H3CellIndex",
                table: "SpeedObservations");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_OsmWayId",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "H3CellIndex",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "IsForward",
                table: "SpeedObservations");

            migrationBuilder.RenameColumn(
                name: "OsmWayId",
                table: "SpeedObservations",
                newName: "ValhallaEdgeId");

            migrationBuilder.CreateTable(
                name: "RoadSpeedAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AvgSpeedKmh = table.Column<double>(type: "double precision", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    FiveMinuteBucket = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    ValhallaEdgeId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadSpeedAggregates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_ValhallaEdgeId",
                table: "SpeedObservations",
                column: "ValhallaEdgeId",
                filter: "\"ValhallaEdgeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RoadSpeedAggregates_ValhallaEdgeId",
                table: "RoadSpeedAggregates",
                column: "ValhallaEdgeId");

            migrationBuilder.CreateIndex(
                name: "IX_RoadSpeedAggregates_ValhallaEdgeId_DayOfWeek_FiveMinuteBuck~",
                table: "RoadSpeedAggregates",
                columns: new[] { "ValhallaEdgeId", "DayOfWeek", "FiveMinuteBucket" },
                unique: true);
        }
    }
}

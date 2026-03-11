using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class OsmNodeIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_OsmWayId",
                table: "SpeedObservations");

            migrationBuilder.DropIndex(
                name: "IX_EdgeSpeedAggregates_OsmWayId_IsForward_DayOfWeek_FiveMinute~",
                table: "EdgeSpeedAggregates");

            migrationBuilder.DropIndex(
                name: "IX_EdgeSpeedAggregates_ValhallaEdgeId",
                table: "EdgeSpeedAggregates");

            migrationBuilder.DropColumn(
                name: "IsForward",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "IsForward",
                table: "EdgeSpeedAggregates");

            migrationBuilder.DropColumn(
                name: "ValhallaEdgeId",
                table: "EdgeSpeedAggregates");

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

            migrationBuilder.AddColumn<long>(
                name: "OsmEndNodeId",
                table: "EdgeSpeedAggregates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OsmStartNodeId",
                table: "EdgeSpeedAggregates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_OsmWayId_OsmStartNodeId_OsmEndNodeId",
                table: "SpeedObservations");

            migrationBuilder.DropIndex(
                name: "IX_EdgeSpeedAggregates_OsmWayId_OsmStartNodeId_OsmEndNodeId_Da~",
                table: "EdgeSpeedAggregates");

            migrationBuilder.DropColumn(
                name: "OsmEndNodeId",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "OsmStartNodeId",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "OsmEndNodeId",
                table: "EdgeSpeedAggregates");

            migrationBuilder.DropColumn(
                name: "OsmStartNodeId",
                table: "EdgeSpeedAggregates");

            migrationBuilder.AddColumn<bool>(
                name: "IsForward",
                table: "SpeedObservations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsForward",
                table: "EdgeSpeedAggregates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ValhallaEdgeId",
                table: "EdgeSpeedAggregates",
                type: "bigint",
                nullable: true);

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
        }
    }
}

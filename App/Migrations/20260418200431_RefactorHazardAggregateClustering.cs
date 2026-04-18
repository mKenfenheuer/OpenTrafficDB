using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Shared.Migrations
{
    /// <inheritdoc />
    public partial class RefactorHazardAggregateClustering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing aggregates are incompatible with the new clustering key
            // (position-grid + heading-bucket + type). They'll regenerate from
            // raw HazardObservations on the next aggregation cycle, so truncate now.
            migrationBuilder.Sql("""TRUNCATE TABLE "HazardAggregates";""");
            migrationBuilder.Sql("""UPDATE "HazardObservations" SET "IsProcessed" = FALSE, "OsmWayId" = NULL;""");

            migrationBuilder.DropIndex(
                name: "IX_HazardAggregates_OsmWayId_HazardType",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "HeadingSampleCount",
                table: "HazardAggregates");

            migrationBuilder.AlterColumn<float>(
                name: "Heading",
                table: "HazardObservations",
                type: "real",
                nullable: false,
                defaultValue: 0f,
                oldClrType: typeof(float),
                oldType: "real",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "OsmWayId",
                table: "HazardAggregates",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<float>(
                name: "Heading",
                table: "HazardAggregates",
                type: "real",
                nullable: false,
                defaultValue: 0f,
                oldClrType: typeof(float),
                oldType: "real",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClusterGridX",
                table: "HazardAggregates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ClusterGridY",
                table: "HazardAggregates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HeadingBucket",
                table: "HazardAggregates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastMatchedAt",
                table: "HazardAggregates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastMatchedCentroidLat",
                table: "HazardAggregates",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastMatchedCentroidLon",
                table: "HazardAggregates",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MatchedLatitude",
                table: "HazardAggregates",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MatchedLongitude",
                table: "HazardAggregates",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_ClusterGridX_ClusterGridY_HeadingBucket_Ha~",
                table: "HazardAggregates",
                columns: new[] { "ClusterGridX", "ClusterGridY", "HeadingBucket", "HazardType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_LastMatchedAt",
                table: "HazardAggregates",
                column: "LastMatchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_OsmWayId",
                table: "HazardAggregates",
                column: "OsmWayId",
                filter: "\"OsmWayId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""TRUNCATE TABLE "HazardAggregates";""");

            migrationBuilder.DropIndex(
                name: "IX_HazardAggregates_ClusterGridX_ClusterGridY_HeadingBucket_Ha~",
                table: "HazardAggregates");

            migrationBuilder.DropIndex(
                name: "IX_HazardAggregates_LastMatchedAt",
                table: "HazardAggregates");

            migrationBuilder.DropIndex(
                name: "IX_HazardAggregates_OsmWayId",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "ClusterGridX",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "ClusterGridY",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "HeadingBucket",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "LastMatchedAt",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "LastMatchedCentroidLat",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "LastMatchedCentroidLon",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "MatchedLatitude",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "MatchedLongitude",
                table: "HazardAggregates");

            migrationBuilder.AddColumn<int>(
                name: "HeadingSampleCount",
                table: "HazardAggregates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<float>(
                name: "Heading",
                table: "HazardObservations",
                type: "real",
                nullable: true,
                oldClrType: typeof(float),
                oldType: "real");

            migrationBuilder.AlterColumn<long>(
                name: "OsmWayId",
                table: "HazardAggregates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<float>(
                name: "Heading",
                table: "HazardAggregates",
                type: "real",
                nullable: true,
                oldClrType: typeof(float),
                oldType: "real");

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_OsmWayId_HazardType",
                table: "HazardAggregates",
                columns: new[] { "OsmWayId", "HazardType" },
                unique: true);
        }
    }
}

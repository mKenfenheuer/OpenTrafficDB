using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class DropH3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "H3SpeedAggregates");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_H3CellIndex",
                table: "SpeedObservations");

            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_TripId",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "H3CellIndex",
                table: "SpeedObservations");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_TripId_Unaggregated",
                table: "SpeedObservations",
                column: "TripId",
                filter: "\"OsmWayId\" IS NOT NULL AND \"IsAggregated\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_TripId_Unaggregated",
                table: "SpeedObservations");

            migrationBuilder.AddColumn<long>(
                name: "H3CellIndex",
                table: "SpeedObservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "H3SpeedAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FiveMinuteBucket = table.Column<int>(type: "integer", nullable: false),
                    H3CellIndex = table.Column<long>(type: "bigint", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    SumSpeedKmh = table.Column<double>(type: "double precision", nullable: false)
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
                name: "IX_SpeedObservations_TripId",
                table: "SpeedObservations",
                column: "TripId");

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
    }
}

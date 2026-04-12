using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddHazardTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HazardObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApiKeyId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    GpsAccuracyMeters = table.Column<float>(type: "real", nullable: false),
                    Heading = table.Column<float>(type: "real", nullable: true),
                    HazardType = table.Column<string>(type: "text", nullable: false),
                    Metadata = table.Column<string>(type: "text", nullable: true),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: true),
                    IsProcessed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HazardObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HazardObservations_ApiKeys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HazardAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    HazardType = table.Column<string>(type: "text", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    Metadata = table.Column<string>(type: "text", nullable: true),
                    ConfirmationCount = table.Column<int>(type: "integer", nullable: false),
                    FirstReportedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HazardAggregates", x => x.Id);
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 80);

            migrationBuilder.CreateIndex(
                name: "IX_HazardObservations_ApiKeyId",
                table: "HazardObservations",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_HazardObservations_IsProcessed_CreatedAt",
                table: "HazardObservations",
                columns: new[] { "IsProcessed", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HazardObservations_Latitude_Longitude",
                table: "HazardObservations",
                columns: new[] { "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_HazardObservations_OsmWayId",
                table: "HazardObservations",
                column: "OsmWayId",
                filter: "\"OsmWayId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_OsmWayId_HazardType",
                table: "HazardAggregates",
                columns: new[] { "OsmWayId", "HazardType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_ExpiresAt",
                table: "HazardAggregates",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_Latitude_Longitude",
                table: "HazardAggregates",
                columns: new[] { "Latitude", "Longitude" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HazardObservations");
            migrationBuilder.DropTable(name: "HazardAggregates");
        }
    }
}

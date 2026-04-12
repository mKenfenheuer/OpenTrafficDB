using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddHazardsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HazardObservation_ApiKeys_ApiKeyId",
                table: "HazardObservation");

            migrationBuilder.RenameTable(
                name: "HazardObservation",
                newName: "HazardObservations");

            migrationBuilder.AlterColumn<int>(
                name: "ApiKeyId",
                table: "HazardObservations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Id",
                table: "HazardObservations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "HazardObservations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<float>(
                name: "GpsAccuracyMeters",
                table: "HazardObservations",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<string>(
                name: "HazardType",
                table: "HazardObservations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<float>(
                name: "Heading",
                table: "HazardObservations",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsProcessed",
                table: "HazardObservations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "HazardObservations",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "HazardObservations",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "HazardObservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OsmWayId",
                table: "HazardObservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "Timestamp",
                table: "HazardObservations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddPrimaryKey(
                name: "PK_HazardObservations",
                table: "HazardObservations",
                column: "Id");

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
                name: "IX_HazardAggregates_ExpiresAt",
                table: "HazardAggregates",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_Latitude_Longitude",
                table: "HazardAggregates",
                columns: new[] { "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_HazardAggregates_OsmWayId_HazardType",
                table: "HazardAggregates",
                columns: new[] { "OsmWayId", "HazardType" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_HazardObservations_ApiKeys_ApiKeyId",
                table: "HazardObservations",
                column: "ApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HazardObservations_ApiKeys_ApiKeyId",
                table: "HazardObservations");

            migrationBuilder.DropTable(
                name: "HazardAggregates");

            migrationBuilder.DropPrimaryKey(
                name: "PK_HazardObservations",
                table: "HazardObservations");

            migrationBuilder.DropIndex(
                name: "IX_HazardObservations_ApiKeyId",
                table: "HazardObservations");

            migrationBuilder.DropIndex(
                name: "IX_HazardObservations_IsProcessed_CreatedAt",
                table: "HazardObservations");

            migrationBuilder.DropIndex(
                name: "IX_HazardObservations_Latitude_Longitude",
                table: "HazardObservations");

            migrationBuilder.DropIndex(
                name: "IX_HazardObservations_OsmWayId",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "GpsAccuracyMeters",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "HazardType",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "Heading",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "IsProcessed",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "OsmWayId",
                table: "HazardObservations");

            migrationBuilder.DropColumn(
                name: "Timestamp",
                table: "HazardObservations");

            migrationBuilder.RenameTable(
                name: "HazardObservations",
                newName: "HazardObservation");

            migrationBuilder.AlterColumn<int>(
                name: "ApiKeyId",
                table: "HazardObservation",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddForeignKey(
                name: "FK_HazardObservation_ApiKeys_ApiKeyId",
                table: "HazardObservation",
                column: "ApiKeyId",
                principalTable: "ApiKeys",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

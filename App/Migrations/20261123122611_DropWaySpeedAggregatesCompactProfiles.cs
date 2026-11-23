using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDBMigrations
{
    /// <inheritdoc />
    public partial class DropWaySpeedAggregatesCompactProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy per-slot history (36 GB), superseded by WayProfiles and unused since 2026-11-21.
            migrationBuilder.DropTable(
                name: "WaySpeedAggregates");

            // Profiles are now encoded by WayProfileCodec (~235 bytes): keep them inline and let
            // Postgres compress only the rare row above the 2 KB TOAST threshold.
            migrationBuilder.Sql("""ALTER TABLE "WayProfiles" ALTER COLUMN "Profile" SET STORAGE MAIN;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE "WayProfiles" ALTER COLUMN "Profile" SET STORAGE EXTENDED;""");

            migrationBuilder.CreateTable(
                name: "WaySpeedAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FiveMinuteBucket = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    SpeedLimitKmh = table.Column<double>(type: "double precision", nullable: true),
                    SumSpeedKmh = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaySpeedAggregates", x => x.Id);
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 80);

            migrationBuilder.CreateIndex(
                name: "IX_WaySpeedAggregates_OsmWayId_DayOfWeek_FiveMinuteBucket",
                table: "WaySpeedAggregates",
                columns: new[] { "OsmWayId", "DayOfWeek", "FiveMinuteBucket" },
                unique: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Shared.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "AggregationMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    WayCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AggregationMetrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BlockedIps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IpHash = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    IsManual = table.Column<bool>(type: "boolean", nullable: false),
                    BlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedIps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    EmailHash = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    IsEmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationToken = table.Column<string>(type: "text", nullable: true),
                    VerificationExpiry = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PendingEmailHash = table.Column<string>(type: "text", nullable: true),
                    PasswordResetToken = table.Column<string>(type: "text", nullable: true),
                    PasswordResetExpiry = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false, defaultValue: "User"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalContributions = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WayGeometries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    MinLat = table.Column<double>(type: "double precision", nullable: false),
                    MinLon = table.Column<double>(type: "double precision", nullable: false),
                    MaxLat = table.Column<double>(type: "double precision", nullable: false),
                    MaxLon = table.Column<double>(type: "double precision", nullable: false),
                    Geometry = table.Column<Geometry>(type: "geometry(LineString,4326)", nullable: true),
                    MaxSpeedKmh = table.Column<double>(type: "double precision", nullable: true),
                    NodeCount = table.Column<int>(type: "integer", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BBox = table.Column<Geometry>(type: "geometry(Polygon,4326)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WayGeometries", x => x.Id);
                });

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
                    SpeedLimitKmh = table.Column<double>(type: "double precision", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaySpeedAggregates", x => x.Id);
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 80);

            migrationBuilder.CreateTable(
                name: "WaySpeedSummaries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    FreeflowSpeed = table.Column<double>(type: "double precision", nullable: false),
                    ConstrainedSpeed = table.Column<double>(type: "double precision", nullable: true),
                    SpeedLimitKmh = table.Column<double>(type: "double precision", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EncodedSpeeds = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaySpeedSummaries", x => x.Id);
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 80);

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    KeyPrefix = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // EF doesn't support PARTITION BY RANGE — created via raw SQL.
            migrationBuilder.Sql("""
                CREATE TABLE "SpeedObservations" (
                    "Id"                bigint GENERATED BY DEFAULT AS IDENTITY NOT NULL,
                    "ApiKeyId"          integer NOT NULL,
                    "Timestamp"         timestamp with time zone NOT NULL,
                    "Latitude"          double precision NOT NULL,
                    "Longitude"         double precision NOT NULL,
                    "SpeedMs"           double precision NOT NULL,
                    "Heading"           real,
                    "AltitudeMeters"    real,
                    "GpsAccuracyMeters" real NOT NULL,
                    "TripId"            text NOT NULL,
                    "OsmWayId"          bigint,
                    "SpeedLimitKmh"     double precision,
                    "IsAggregated"      boolean NOT NULL DEFAULT FALSE,
                    "RoadWorks"         boolean NOT NULL DEFAULT FALSE,
                    "CreatedAt"         timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_SpeedObservations" PRIMARY KEY ("Id", "Timestamp"),
                    CONSTRAINT "FK_SpeedObservations_ApiKeys_ApiKeyId"
                        FOREIGN KEY ("ApiKeyId") REFERENCES "ApiKeys" ("Id") ON DELETE CASCADE
                ) PARTITION BY RANGE ("Timestamp");
                """);

            // Monthly partitions 2025–2027 + default catch-all.
            // PartitionMaintenanceService extends this automatically going forward.
            // Autovacuum parameters are set on leaf partitions (not allowed on the partitioned parent).
            var partitionSql = new System.Text.StringBuilder();
            for (var year = 2025; year <= 2027; year++)
                for (var month = 1; month <= 12; month++)
                {
                    var from = new DateTime(year, month, 1);
                    var to   = from.AddMonths(1);
                    partitionSql.AppendLine($"""
                        CREATE TABLE "SpeedObservations_{year:D4}_{month:D2}"
                            PARTITION OF "SpeedObservations"
                            FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}')
                            WITH (autovacuum_vacuum_scale_factor  = 0.01,
                                  autovacuum_analyze_scale_factor = 0.005,
                                  autovacuum_vacuum_cost_delay    = 2);
                        """);
                }
            partitionSql.AppendLine("""CREATE TABLE "SpeedObservations_default" PARTITION OF "SpeedObservations" DEFAULT;""");
            migrationBuilder.Sql(partitionSql.ToString());

            migrationBuilder.CreateIndex(
                name: "IX_AggregationMetrics_ProcessedAt",
                table: "AggregationMetrics",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyPrefix",
                table: "ApiKeys",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId",
                table: "ApiKeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedIps_IpHash",
                table: "BlockedIps",
                column: "IpHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_ApiKeyId",
                table: "SpeedObservations",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_IsAggregated_CreatedAt",
                table: "SpeedObservations",
                columns: new[] { "IsAggregated", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_Latitude_Longitude",
                table: "SpeedObservations",
                columns: new[] { "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_OsmWayId",
                table: "SpeedObservations",
                column: "OsmWayId",
                filter: "\"OsmWayId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_Timestamp",
                table: "SpeedObservations",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_Timestamp_Latitude_Longitude",
                table: "SpeedObservations",
                columns: new[] { "Timestamp", "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_Timestamp_OsmWayId",
                table: "SpeedObservations",
                columns: new[] { "Timestamp", "OsmWayId" },
                filter: "\"OsmWayId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_TripId_Unaggregated",
                table: "SpeedObservations",
                column: "TripId",
                filter: "\"OsmWayId\" IS NOT NULL AND \"IsAggregated\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_Users_EmailHash",
                table: "Users",
                column: "EmailHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_PasswordResetToken",
                table: "Users",
                column: "PasswordResetToken",
                filter: "\"PasswordResetToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_VerificationToken",
                table: "Users",
                column: "VerificationToken",
                filter: "\"VerificationToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_BBox",
                table: "WayGeometries",
                column: "BBox")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_WayGeometries_OsmWayId",
                table: "WayGeometries",
                column: "OsmWayId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaySpeedAggregates_OsmWayId_DayOfWeek_FiveMinuteBucket",
                table: "WaySpeedAggregates",
                columns: new[] { "OsmWayId", "DayOfWeek", "FiveMinuteBucket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaySpeedSummaries_LastUpdated",
                table: "WaySpeedSummaries",
                column: "LastUpdated");

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
                name: "AggregationMetrics");

            migrationBuilder.DropTable(
                name: "BlockedIps");

            migrationBuilder.Sql("""DROP TABLE "SpeedObservations" CASCADE;""");

            migrationBuilder.DropTable(
                name: "WayGeometries");

            migrationBuilder.DropTable(
                name: "WaySpeedAggregates");

            migrationBuilder.DropTable(
                name: "WaySpeedSummaries");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}

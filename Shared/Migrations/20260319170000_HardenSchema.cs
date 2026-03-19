using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    public partial class HardenSchema : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Remove IpHash ────────────────────────────────────────────────
            migrationBuilder.DropColumn(name: "IpHash", table: "SpeedObservations");

            // ── 2. Partition SpeedObservations by Timestamp ─────────────────────
            migrationBuilder.Sql("""
                ALTER TABLE "SpeedObservations" RENAME TO "SpeedObservations_old";

                CREATE TABLE "SpeedObservations" (
                    "Id"                bigint GENERATED ALWAYS AS IDENTITY NOT NULL,
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
                    PRIMARY KEY ("Id", "Timestamp")
                ) PARTITION BY RANGE ("Timestamp");
                """);

            // Monthly partitions 2025-2027
            for (var year = 2025; year <= 2027; year++)
                for (var month = 1; month <= 12; month++)
                {
                    var from = new System.DateTime(year, month, 1);
                    var to   = from.AddMonths(1);
                    migrationBuilder.Sql($"""
                        CREATE TABLE "SpeedObservations_{year:D4}_{month:D2}"
                            PARTITION OF "SpeedObservations"
                            FOR VALUES FROM ('{from:yyyy-MM-dd}') TO ('{to:yyyy-MM-dd}');
                        """);
                }

            migrationBuilder.Sql("""
                CREATE TABLE "SpeedObservations_default"
                    PARTITION OF "SpeedObservations" DEFAULT;
                """);

            // ── 3. Copy data and reset sequence ─────────────────────────────────
            migrationBuilder.Sql("""
                INSERT INTO "SpeedObservations"
                    ("Id","ApiKeyId","Timestamp","Latitude","Longitude","SpeedMs",
                     "Heading","AltitudeMeters","GpsAccuracyMeters","TripId",
                     "OsmWayId","SpeedLimitKmh","IsAggregated","RoadWorks","CreatedAt")
                OVERRIDING SYSTEM VALUE
                SELECT "Id","ApiKeyId","Timestamp","Latitude","Longitude","SpeedMs",
                       "Heading","AltitudeMeters","GpsAccuracyMeters","TripId",
                       "OsmWayId","SpeedLimitKmh","IsAggregated","RoadWorks","CreatedAt"
                FROM "SpeedObservations_old";

                SELECT setval(
                    pg_get_serial_sequence('"SpeedObservations"','Id'),
                    COALESCE((SELECT MAX("Id") FROM "SpeedObservations"), 1), true);
                """);

            // ── 4. Restore FK ────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                ALTER TABLE "SpeedObservations"
                    ADD CONSTRAINT "FK_SpeedObservations_ApiKeys_ApiKeyId"
                    FOREIGN KEY ("ApiKeyId") REFERENCES "ApiKeys" ("Id") ON DELETE CASCADE;
                """);

            // ── 5. Restore indexes ───────────────────────────────────────────────
            migrationBuilder.Sql("""
                CREATE INDEX "IX_SpeedObservations_ApiKeyId"
                    ON "SpeedObservations" ("ApiKeyId");
                CREATE INDEX "IX_SpeedObservations_OsmWayId"
                    ON "SpeedObservations" ("OsmWayId") WHERE "OsmWayId" IS NOT NULL;
                CREATE INDEX "IX_SpeedObservations_Timestamp"
                    ON "SpeedObservations" ("Timestamp");
                CREATE INDEX "IX_SpeedObservations_TripId_Unaggregated"
                    ON "SpeedObservations" ("TripId")
                    WHERE "OsmWayId" IS NOT NULL AND "IsAggregated" = FALSE;
                CREATE INDEX "IX_SpeedObservations_TripId_Unmatched"
                    ON "SpeedObservations" ("TripId")
                    WHERE "OsmWayId" IS NULL AND "IsAggregated" = FALSE;
                CREATE INDEX "IX_SpeedObservations_IsAggregated_CreatedAt"
                    ON "SpeedObservations" ("IsAggregated", "CreatedAt");
                CREATE INDEX "IX_SpeedObservations_Latitude_Longitude"
                    ON "SpeedObservations" ("Latitude", "Longitude");
                CREATE INDEX "IX_SpeedObservations_Timestamp_OsmWayId"
                    ON "SpeedObservations" ("Timestamp", "OsmWayId")
                    WHERE "OsmWayId" IS NOT NULL;
                CREATE INDEX "IX_SpeedObservations_Timestamp_Latitude_Longitude"
                    ON "SpeedObservations" ("Timestamp", "Latitude", "Longitude");
                """);

            // ── 6. Deduplication index ────────────────────────────────────────────
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_SpeedObservations_TripId_Timestamp_Dedup"
                    ON "SpeedObservations" ("TripId", "Timestamp");
                """);

            // ── 7. Drop old table ─────────────────────────────────────────────────
            migrationBuilder.Sql("""DROP TABLE "SpeedObservations_old";""");

            // ── 8. FILLFACTOR on WaySpeedAggregates ──────────────────────────────
            migrationBuilder.Sql("""
                ALTER TABLE "WaySpeedAggregates" SET (fillfactor = 80);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException(
                "Table partitioning and IpHash removal cannot be reversed via migration.");
        }
    }
}

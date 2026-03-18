using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class FillfactorAndSummaryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Set fillfactor=70 on upsert-heavy tables so HOT updates can happen in-place,
            // reducing index bloat and write amplification.
            migrationBuilder.Sql("""
                ALTER TABLE "WaySpeedAggregates" SET (fillfactor = 70);
                ALTER TABLE "WaySpeedSummaries"  SET (fillfactor = 70);
                """);

            // Index to support the tile query: WHERE wss."LastUpdated" >= NOW() - INTERVAL '30 minutes'
            migrationBuilder.CreateIndex(
                name: "IX_WaySpeedSummaries_LastUpdated",
                table: "WaySpeedSummaries",
                column: "LastUpdated");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WaySpeedSummaries_LastUpdated",
                table: "WaySpeedSummaries");

            migrationBuilder.Sql("""
                ALTER TABLE "WaySpeedAggregates" RESET (fillfactor);
                ALTER TABLE "WaySpeedSummaries"  RESET (fillfactor);
                """);
        }
    }
}

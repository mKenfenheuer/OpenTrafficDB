using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDBMigrations
{
    /// <inheritdoc />
    public partial class AddMatchAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "MatchAttempts",
                table: "SpeedObservations",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_MatchQueue",
                table: "SpeedObservations",
                columns: new[] { "TripId", "Id" },
                filter: "\"OsmWayId\" IS NULL AND \"IsAggregated\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_MatchQueue",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "MatchAttempts",
                table: "SpeedObservations");
        }
    }
}

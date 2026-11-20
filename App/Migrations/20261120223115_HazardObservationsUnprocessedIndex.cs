using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDBMigrations
{
    /// <inheritdoc />
    public partial class HazardObservationsUnprocessedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_HazardObservations_Unprocessed",
                table: "HazardObservations",
                column: "Id",
                filter: "\"IsProcessed\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HazardObservations_Unprocessed",
                table: "HazardObservations");
        }
    }
}

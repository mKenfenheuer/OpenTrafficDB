using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class IncrementalAggregation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAggregated",
                table: "SpeedObservations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_IsAggregated_CreatedAt",
                table: "SpeedObservations",
                columns: new[] { "IsAggregated", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_IsAggregated_CreatedAt",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "IsAggregated",
                table: "SpeedObservations");
        }
    }
}

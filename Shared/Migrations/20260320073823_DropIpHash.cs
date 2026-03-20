using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class DropIpHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SpeedObservations_IpHash",
                table: "SpeedObservations");

            migrationBuilder.DropColumn(
                name: "IpHash",
                table: "SpeedObservations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IpHash",
                table: "SpeedObservations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_IpHash",
                table: "SpeedObservations",
                column: "IpHash");
        }
    }
}

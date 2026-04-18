using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddHazardHeadingPermanentStreetName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "WayGeometries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Heading",
                table: "HazardAggregates",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HeadingCosSum",
                table: "HazardAggregates",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "HeadingSampleCount",
                table: "HazardAggregates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "HeadingSinSum",
                table: "HazardAggregates",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "IsPermanent",
                table: "HazardAggregates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "StreetName",
                table: "HazardAggregates",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Name",
                table: "WayGeometries");

            migrationBuilder.DropColumn(
                name: "Heading",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "HeadingCosSum",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "HeadingSampleCount",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "HeadingSinSum",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "IsPermanent",
                table: "HazardAggregates");

            migrationBuilder.DropColumn(
                name: "StreetName",
                table: "HazardAggregates");
        }
    }
}

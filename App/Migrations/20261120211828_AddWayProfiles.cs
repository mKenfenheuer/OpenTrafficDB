using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDBMigrations
{
    /// <inheritdoc />
    public partial class AddWayProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WayProfiles",
                columns: table => new
                {
                    OsmWayId = table.Column<long>(type: "bigint", nullable: false),
                    Profile = table.Column<byte[]>(type: "bytea", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WayProfiles", x => x.OsmWayId);
                })
                .Annotation("Npgsql:StorageParameter:fillfactor", 80);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WayProfiles");
        }
    }
}

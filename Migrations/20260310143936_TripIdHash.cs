using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class TripIdHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TripId",
                table: "SpeedObservations",
                type: "text",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "TripId",
                table: "SpeedObservations",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}

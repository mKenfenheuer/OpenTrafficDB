using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Shared.Migrations
{
    /// <inheritdoc />
    public partial class ConvertHazardAggregateMetadataToJsonb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullify any existing rows whose Metadata is not valid JSON
            // so the text→jsonb column conversion doesn't fail.
            migrationBuilder.Sql("""
                UPDATE "HazardAggregates"
                SET "Metadata" = NULL
                WHERE "Metadata" IS NOT NULL
                  AND left(trim("Metadata"), 1) != '{';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Metadata",
                table: "HazardAggregates",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Metadata",
                table: "HazardAggregates",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }
    }
}

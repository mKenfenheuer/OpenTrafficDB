using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDBMigrations
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

            migrationBuilder.Sql("""
                ALTER TABLE "HazardAggregates"
                ALTER COLUMN "Metadata" TYPE jsonb USING "Metadata"::jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "HazardAggregates"
                ALTER COLUMN "Metadata" TYPE text USING "Metadata"::text;
                """);
        }
    }
}

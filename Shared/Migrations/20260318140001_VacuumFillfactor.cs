using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class VacuumFillfactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // VACUUM FULL rewrites the tables to immediately reclaim space under the new fillfactor.
            // suppressTransaction: true is required because VACUUM cannot run inside a transaction.
            migrationBuilder.Sql("""
                VACUUM FULL "WaySpeedAggregates";
                VACUUM FULL "WaySpeedSummaries";
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // VACUUM cannot be meaningfully reversed.
        }
    }
}

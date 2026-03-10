using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenTrafficDB.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockedIps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IpHash = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    IsManual = table.Column<bool>(type: "boolean", nullable: false),
                    BlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockedIps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoadSpeedAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ValhallaEdgeId = table.Column<long>(type: "bigint", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    FiveMinuteBucket = table.Column<int>(type: "integer", nullable: false),
                    AvgSpeedKmh = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoadSpeedAggregates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    EmailHash = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    IsEmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    VerificationToken = table.Column<string>(type: "text", nullable: true),
                    VerificationExpiry = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false, defaultValue: "User"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalContributions = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    KeyPrefix = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpeedObservations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApiKeyId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    SpeedMs = table.Column<double>(type: "double precision", nullable: false),
                    Heading = table.Column<float>(type: "real", nullable: true),
                    AltitudeMeters = table.Column<float>(type: "real", nullable: true),
                    GpsAccuracyMeters = table.Column<float>(type: "real", nullable: false),
                    TripId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValhallaEdgeId = table.Column<long>(type: "bigint", nullable: true),
                    IpHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeedObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpeedObservations_ApiKeys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyPrefix",
                table: "ApiKeys",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId",
                table: "ApiKeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BlockedIps_IpHash",
                table: "BlockedIps",
                column: "IpHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoadSpeedAggregates_ValhallaEdgeId",
                table: "RoadSpeedAggregates",
                column: "ValhallaEdgeId");

            migrationBuilder.CreateIndex(
                name: "IX_RoadSpeedAggregates_ValhallaEdgeId_DayOfWeek_FiveMinuteBuck~",
                table: "RoadSpeedAggregates",
                columns: new[] { "ValhallaEdgeId", "DayOfWeek", "FiveMinuteBucket" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_ApiKeyId",
                table: "SpeedObservations",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_Latitude_Longitude",
                table: "SpeedObservations",
                columns: new[] { "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_Timestamp",
                table: "SpeedObservations",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_TripId",
                table: "SpeedObservations",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedObservations_ValhallaEdgeId",
                table: "SpeedObservations",
                column: "ValhallaEdgeId",
                filter: "\"ValhallaEdgeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_EmailHash",
                table: "Users",
                column: "EmailHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_VerificationToken",
                table: "Users",
                column: "VerificationToken",
                filter: "\"VerificationToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockedIps");

            migrationBuilder.DropTable(
                name: "RoadSpeedAggregates");

            migrationBuilder.DropTable(
                name: "SpeedObservations");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}

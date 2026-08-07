using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChunkBAirlineAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServiceCeilingFt",
                table: "AircraftTypes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Backfill real service ceilings for rows an earlier run of AircraftTypeSeeder
            // already inserted before this column existed - AddColumn above only sets new
            // rows going forward. On a brand new database these UPDATEs simply match zero
            // rows, since the seeder always runs after migrations.
            migrationBuilder.Sql(
                "UPDATE AircraftTypes SET ServiceCeilingFt = 41000 WHERE IcaoType = 'A319';" +
                "UPDATE AircraftTypes SET ServiceCeilingFt = 39000 WHERE IcaoType = 'A320';" +
                "UPDATE AircraftTypes SET ServiceCeilingFt = 39000 WHERE IcaoType = 'A321';" +
                "UPDATE AircraftTypes SET ServiceCeilingFt = 41000 WHERE IcaoType = 'B737';" +
                "UPDATE AircraftTypes SET ServiceCeilingFt = 41000 WHERE IcaoType = 'B738';" +
                "UPDATE AircraftTypes SET ServiceCeilingFt = 41000 WHERE IcaoType = 'B739';");

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", nullable: false),
                    DistanceUnit = table.Column<string>(type: "TEXT", nullable: false),
                    AltitudeUnit = table.Column<string>(type: "TEXT", nullable: false),
                    WeightUnit = table.Column<string>(type: "TEXT", nullable: false),
                    TimeDisplay = table.Column<string>(type: "TEXT", nullable: false),
                    Use24HourClock = table.Column<bool>(type: "INTEGER", nullable: false),
                    Theme = table.Column<string>(type: "TEXT", nullable: false),
                    CommunityFolderPath = table.Column<string>(type: "TEXT", nullable: true),
                    SimBriefPilotId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSettings_OwnerUserId",
                table: "UserSettings",
                column: "OwnerUserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ServiceCeilingFt",
                table: "AircraftTypes");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVirtualPilotScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Schedules");

            migrationBuilder.AddColumn<string>(
                name: "UnflyableReason",
                table: "Flights",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReservedForPlayer",
                table: "FleetAircraft",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // HAND-CORRECTED - do not trust the scaffolded "false for everyone" default above.
            // Keeping one aircraft free for the human requires exactly one
            // aircraft reserved per airline once its fleet exceeds one aircraft (and its single
            // aircraft reserved when it has only one) - a rule this app is only now old enough to
            // have pre-existing fleets for. Leaving every existing row at the scaffolded false
            // would silently strip that protection from every airline that already has a fleet the
            // moment E2 ships, exactly the kind of wrong-default bug the project has been bitten by
            // twice before (an enum's "" default, a sim rate's 0.0 default - see this migration's
            // own commit message). So: for every airline, flag exactly one of its non-deleted
            // FleetAircraft rows reserved - preferring whichever is parked at the airline's home
            // base (most likely to be "where the player actually is" per the plan's own preference
            // ordering), falling back to the oldest aircraft if none are at the hub. This is purely
            // additive/corrective (sets a flag, touches no other column, deletes nothing) so it is
            // safe to run against a database with real flight history.
            migrationBuilder.Sql(@"
                UPDATE FleetAircraft
                SET ReservedForPlayer = 1
                WHERE DeletedUtc IS NULL
                AND Id = (
                    SELECT fa.Id
                    FROM FleetAircraft fa
                    INNER JOIN Airlines a ON a.Id = fa.AirlineId
                    WHERE fa.AirlineId = FleetAircraft.AirlineId AND fa.DeletedUtc IS NULL
                    ORDER BY
                        CASE WHEN fa.LocationIcao = a.HomeAirportIcao THEN 0 ELSE 1 END,
                        fa.CreatedUtc
                    LIMIT 1
                );
            ");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastScheduleResolvedUtc",
                table: "EconomyStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PilotScheduleEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PilotScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    DepartureTimeUtc = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FleetAircraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PilotScheduleEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PilotSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PilotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PilotSchedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PilotScheduleEntries_PilotScheduleId",
                table: "PilotScheduleEntries",
                column: "PilotScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_PilotSchedules_AirlineId",
                table: "PilotSchedules",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_PilotSchedules_PilotId",
                table: "PilotSchedules",
                column: "PilotId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PilotScheduleEntries");

            migrationBuilder.DropTable(
                name: "PilotSchedules");

            migrationBuilder.DropColumn(
                name: "UnflyableReason",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "ReservedForPlayer",
                table: "FleetAircraft");

            migrationBuilder.DropColumn(
                name: "LastScheduleResolvedUtc",
                table: "EconomyStates");

            migrationBuilder.CreateTable(
                name: "Schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BlockMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DaysOfWeekMask = table.Column<int>(type: "INTEGER", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DepartureTimeUtc = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    FleetAircraftId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PilotId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_RouteId",
                table: "Schedules",
                column: "RouteId");
        }
    }
}

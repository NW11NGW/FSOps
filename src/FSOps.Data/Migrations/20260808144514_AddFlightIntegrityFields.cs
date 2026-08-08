using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFlightIntegrityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows predate this feature and were never measured for time acceleration -
            // 1.0 ("normal speed") is the honest default, not 0.0, which would misleadingly read
            // as a frozen clock.
            migrationBuilder.AddColumn<double>(
                name: "MaxSimulationRateObserved",
                table: "Flights",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<bool>(
                name: "PositionJumpDetected",
                table: "Flights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SimRateElevated",
                table: "Flights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SlewDetected",
                table: "Flights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxSimulationRateObserved",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "PositionJumpDetected",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "SimRateElevated",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "SlewDetected",
                table: "Flights");
        }
    }
}

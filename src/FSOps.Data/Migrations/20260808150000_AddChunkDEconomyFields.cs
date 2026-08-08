using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkDEconomyFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MTOW is a real, published figure per aircraft type - there is no sane single
            // default for every type, so new rows get a generic single-aisle placeholder and the
            // UPDATE statements below immediately correct every type AircraftTypeSeeder knows
            // about. A future type added without an UPDATE here would keep the placeholder rather
            // than fail outright, which is the honest degrade (still positive, so
            // FlightCostCalculator's weight-based fees don't throw).
            migrationBuilder.AddColumn<double>(
                name: "MtowTonnes",
                table: "AircraftTypes",
                type: "REAL",
                nullable: false,
                defaultValue: 78.0);

            migrationBuilder.Sql("UPDATE AircraftTypes SET MtowTonnes = 75.5 WHERE IcaoType = 'A319';");
            migrationBuilder.Sql("UPDATE AircraftTypes SET MtowTonnes = 78.0 WHERE IcaoType = 'A320';");
            migrationBuilder.Sql("UPDATE AircraftTypes SET MtowTonnes = 93.5 WHERE IcaoType = 'A321';");
            migrationBuilder.Sql("UPDATE AircraftTypes SET MtowTonnes = 70.1 WHERE IcaoType = 'B737';");
            migrationBuilder.Sql("UPDATE AircraftTypes SET MtowTonnes = 79.0 WHERE IcaoType = 'B738';");
            migrationBuilder.Sql("UPDATE AircraftTypes SET MtowTonnes = 85.1 WHERE IcaoType = 'B739';");

            // Existing completed flights predate ledger posting entirely - they genuinely never
            // had revenue/costs posted, so false (not-yet-posted) is the honest default rather
            // than something that would need backfilling.
            migrationBuilder.AddColumn<bool>(
                name: "RevenuePosted",
                table: "Flights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MtowTonnes",
                table: "AircraftTypes");

            migrationBuilder.DropColumn(
                name: "RevenuePosted",
                table: "Flights");
        }
    }
}

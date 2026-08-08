using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaystyleAndFuelOnBoard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "FuelOnBoardKg",
                table: "FleetAircraft",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            // Existing airlines predate the playstyle split entirely - Casual is what Chunk D
            // shipped and is the only honest default for a row that was created under those rules
            // (see docs/PLAN.md "Playstyle - Casual vs True-life"). The column is stored as the
            // enum's string name (HasConversion<string>() in AirlineConfiguration), so the default
            // must be a real, parseable member name, not an empty string.
            migrationBuilder.AddColumn<string>(
                name: "Playstyle",
                table: "Airlines",
                type: "TEXT",
                nullable: false,
                defaultValue: "Casual");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FuelOnBoardKg",
                table: "FleetAircraft");

            migrationBuilder.DropColumn(
                name: "Playstyle",
                table: "Airlines");
        }
    }
}

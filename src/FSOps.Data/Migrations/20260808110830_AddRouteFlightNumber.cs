using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRouteFlightNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FlightNumber",
                table: "Routes",
                type: "TEXT",
                maxLength: 5,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Routes_AirlineId_FlightNumber",
                table: "Routes",
                columns: new[] { "AirlineId", "FlightNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Routes_AirlineId_FlightNumber",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "FlightNumber",
                table: "Routes");
        }
    }
}

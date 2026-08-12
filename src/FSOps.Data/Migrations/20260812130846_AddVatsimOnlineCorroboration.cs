using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVatsimOnlineCorroboration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VatsimCid",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatsimCallsign",
                table: "Flights",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatsimControllersWorked",
                table: "Flights",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VatsimOnline",
                table: "Flights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "VatsimOnlineFraction",
                table: "Flights",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VatsimCid",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "VatsimCallsign",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "VatsimControllersWorked",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "VatsimOnline",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "VatsimOnlineFraction",
                table: "Flights");
        }
    }
}

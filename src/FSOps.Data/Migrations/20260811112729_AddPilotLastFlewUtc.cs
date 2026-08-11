using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPilotLastFlewUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Deliberately nullable with NO default and NO backfill - do not "helpfully" add one.
            // Skill decays with time since a pilot last flew, so this column is the idle clock.
            // NULL means "has never flown since this existed", which the skill calculator reads as
            // nothing to decay from: an existing pilot keeps the growth their real HoursFlown has
            // already earned and starts decaying only once they actually fly again. Backfilling any
            // date would either reset every pilot's idle clock to zero or, with an older date,
            // instantly decay pilots purely because the column did not exist yesterday.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastFlewUtc",
                table: "Pilots",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastFlewUtc",
                table: "Pilots");
        }
    }
}

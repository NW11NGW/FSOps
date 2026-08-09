using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenanceSuspensionAndAwaySummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true is LOAD-BEARING, not cosmetic - do not "tidy" it away. SQLite
            // backfills every existing row with it, which is the whole point: schedules built
            // before this feature existed must come out of the migration with suspension already
            // switched on. If they defaulted to false, a True-life airline whose aircraft was
            // mid-C-check on the day this lands would be charged a cancellation fee for every one
            // of the check's ~14 days - precisely the bug the feature was added to prevent. Nobody
            // has to opt in, so there is no case where false is right for a pre-existing row.
            migrationBuilder.AddColumn<bool>(
                name: "AutoSuspendOnMaintenance",
                table: "PilotSchedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // Nullable with no default on purpose: null means "this airline has never viewed an
            // away summary", which is the correct starting state for existing rows. A non-null
            // default would read as "already acknowledged" and would suppress the first summary
            // the player was meant to see.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AwaySummaryLastViewedUtc",
                table: "EconomyStates",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoSuspendOnMaintenance",
                table: "PilotSchedules");

            migrationBuilder.DropColumn(
                name: "AwaySummaryLastViewedUtc",
                table: "EconomyStates");
        }
    }
}

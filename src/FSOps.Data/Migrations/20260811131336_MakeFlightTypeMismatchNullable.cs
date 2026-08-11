using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeFlightTypeMismatchNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Widening only - every existing row holds a real 0 or 1 and keeps exactly the meaning
            // it already had. NULL is a new third state meaning "the sim reported no aircraft, so
            // nothing was ever compared", which is what this column could not express before: an
            // absent title was being negated into a positive mismatch, so the app told players
            // their aircraft was wrong whenever the sim simply was not connected. No defaultValue
            // here on purpose - a default would quietly rewrite the very rows this must leave alone.
            // Note SQLite implements AlterColumn by rebuilding the table, so this was verified by
            // migrating a file-backed database with real flight rows and reading every field back.
            migrationBuilder.AlterColumn<bool>(
                name: "TypeMismatch",
                table: "Flights",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back is necessarily lossy: NOT NULL cannot hold the "never checked" state, so
            // any NULL collapses to false ("checked and matched"). That is the least-wrong option
            // available going backwards - the alternative, false's opposite, would resurrect the
            // exact bug this migration exists to fix by reporting a mismatch that was never found.
            migrationBuilder.AlterColumn<bool>(
                name: "TypeMismatch",
                table: "Flights",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}

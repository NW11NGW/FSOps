using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <summary>
    /// Removes <c>Pilots.Status</c>. A pilot's status is now derived on every read by
    /// <c>PilotStatusCalculator</c> instead of stored - see that type for why a stored one could
    /// only ever be wrong (it was written once, at hire, and never again, so it said "Available"
    /// for a pilot who was in the air).
    /// <para>
    /// Its own migration, separate from DropCommunityFolderPath, so each column drop stays
    /// independently revertible and independently proved. Like that one this is a whole-table
    /// rebuild on SQLite rather than an <c>ALTER TABLE</c>, and this table carries career state -
    /// hours flown, last-flew timestamp, salary, and the soft-delete tombstone that decides whether
    /// a released pilot stays released. See PilotStatusColumnDropMigrationTests, which reads every
    /// one of those back after the rebuild, including on a soft-deleted row.
    /// </para>
    /// </summary>
    public partial class DropPilotStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "Pilots");
        }

        /// <summary>
        /// <b>Hand-corrected.</b> EF scaffolded this as <c>defaultValue: ""</c>, which is the exact
        /// mistake this project has already shipped twice: the column is a PilotStatus stored via
        /// <c>HasConversion&lt;string&gt;()</c>, and <c>""</c> parses as no member of that enum at
        /// all - so reverting would have restored the column with a value that every single
        /// existing pilot row then failed to read back. "Available" is the value the column
        /// genuinely held for every row before the drop (nothing in the app ever wrote another one,
        /// which is what made the column worth removing), so restoring it is a true restore rather
        /// than a guess.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Pilots",
                type: "TEXT",
                nullable: false,
                defaultValue: "Available");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <summary>
    /// Adds the four columns FSOps needs to know which aircraft the player can actually load in the
    /// simulator: where their Community folder is, which edition of MSFS 2024 they bought, the last
    /// scan of their sim folders, and whatever they have ticked or unticked by hand.
    ///
    /// <para>Purely additive. All four are appended with SQLite's real <c>ALTER TABLE ADD COLUMN</c>,
    /// so UserSettings is never rebuilt, no row is rewritten and the unique index on OwnerUserId is
    /// never dropped. Nothing else about the table changes.</para>
    ///
    /// <para><b>Read the defaults, do not trust them.</b> Three of these are nullable TEXT with no
    /// default at all, which is deliberate: an existing row has genuinely never been scanned and has
    /// genuinely never had a folder configured, and NULL says that where <c>""</c> would say
    /// "configured, to nothing". The fourth, SimEdition, is a string-converted enum, and the
    /// scaffolder emits <c>defaultValue: ""</c> for those unless the default is declared on the
    /// property - a bug this project has shipped before. UserSettingsConfiguration declares
    /// <c>HasDefaultValue(SimEdition.Standard)</c>, which is why 'Standard' appears below.</para>
    ///
    /// <para><b>Standard is the right answer for an existing row, not merely a safe one.</b> Every
    /// row already in the database belongs to somebody who has never been asked which edition they
    /// own, and Standard is the smallest aircraft set. Landing them there means the worst case is
    /// being asked to tick a box for an aircraft they have; landing them on Premium Deluxe would
    /// mean silently offering contracts in aircraft that are not in their simulator, which is the
    /// exact failure this whole feature exists to prevent.</para>
    /// </summary>
    public partial class AddSimAircraftSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommunityFolderPath",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SimAircraftOverridesJson",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SimAircraftScanJson",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SimEdition",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "Standard");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommunityFolderPath",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SimAircraftOverridesJson",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SimAircraftScanJson",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SimEdition",
                table: "UserSettings");
        }
    }
}

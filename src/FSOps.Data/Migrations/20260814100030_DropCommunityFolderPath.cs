using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <summary>
    /// Removes the last trace of the MSFS in-game toolbar panel, which was cut from the product:
    /// <c>UserSettings.CommunityFolderPath</c> held the player's Community folder so the panel could
    /// be installed there, and nothing has read or written it since.
    /// <para>
    /// This is its own migration on purpose. On SQLite a <c>DropColumn</c> is not an
    /// <c>ALTER TABLE</c> - EF rebuilds UserSettings into a temporary table, copies every row across,
    /// swaps it in and recreates the unique index on OwnerUserId. That is a whole-table rewrite
    /// against a database holding the player's real settings, and it deserves to be proved rather
    /// than assumed; folding it into an unrelated migration would have hidden a table rebuild inside
    /// a change nobody would think to check. See UserSettingsColumnDropMigrationTests, which seeds
    /// every field to a deliberately non-default value and reads them all back afterwards.
    /// </para>
    /// </summary>
    public partial class DropCommunityFolderPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommunityFolderPath",
                table: "UserSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommunityFolderPath",
                table: "UserSettings",
                type: "TEXT",
                nullable: true);
        }
    }
}

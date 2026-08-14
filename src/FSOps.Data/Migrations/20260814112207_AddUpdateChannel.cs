using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <summary>
    /// Adds UserSettings.UpdateChannel - which releases the in-app updater may offer.
    /// <para>
    /// Purely additive: SQLite can append a column with a default in a real <c>ALTER TABLE</c>, so
    /// there is no table rebuild here and no row is rewritten. Nothing else about the table changes.
    /// </para>
    /// <para>
    /// <b>The default is the whole migration.</b> Every row that already exists belongs to somebody
    /// who never asked for anything but released builds, and they must land on Stable. The scaffolded
    /// value is only 'Stable' because UserSettingsConfiguration declares
    /// <c>HasDefaultValue(UpdateChannel.Stable)</c>; without it EF emits <c>defaultValue: ""</c> for a
    /// string-converted enum, which is not a member of the enum and fails to parse for every existing
    /// row. This project has shipped that exact bug before, which is why the default is declared on
    /// the property rather than left to the scaffolder to guess.
    /// </para>
    /// </summary>
    public partial class AddUpdateChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpdateChannel",
                table: "UserSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "Stable");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateChannel",
                table: "UserSettings");
        }
    }
}

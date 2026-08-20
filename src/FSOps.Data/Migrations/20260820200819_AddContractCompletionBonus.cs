using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <summary>
    /// Adds the completion bonus - one lump paid for finishing a whole chain of legs. See
    /// <c>Contract.CompletionBonus</c> for why it exists.
    ///
    /// <para><b>The scaffolded default of 0 was checked and is correct here</b>, which is worth
    /// stating because this project has twice shipped a bug by trusting one that was not (an empty
    /// string for an enum stored via <c>HasConversion&lt;string&gt;()</c>, and 0.0 for a sim-rate
    /// column). Zero is right for a specific reason rather than by luck: every existing contract row
    /// was generated and priced before completion bonuses existed, and its fee was what the player
    /// was shown when they accepted it. Backfilling a non-zero bonus would retroactively promise
    /// money on jobs agreed under different terms - the exact thing the "stamped at generation"
    /// rule exists to prevent. Contracts written from here on carry a real figure.</para>
    ///
    /// <para>Purely additive: one new column on <c>Contracts</c>, no table rebuild, nothing rewritten.
    /// <c>Down</c> drops only the column it added.</para>
    /// </summary>
    public partial class AddContractCompletionBonus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CompletionBonus",
                table: "Contracts",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionBonus",
                table: "Contracts");
        }
    }
}

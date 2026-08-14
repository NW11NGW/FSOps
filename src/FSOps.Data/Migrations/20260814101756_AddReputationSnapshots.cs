using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <summary>
    /// Adds the insert-only <c>ReputationSnapshots</c> table - see
    /// <see cref="FSOps.Core.Entities.ReputationSnapshot"/> for why reputation history has to be
    /// recorded rather than reconstructed.
    /// <para>
    /// <b>Structurally non-destructive.</b> This migration only ever creates a new table and its
    /// index. It adds no column to an existing table, so SQLite performs no table rebuild, and it
    /// carries no <c>defaultValue</c> at all - the two ways a migration in this project has silently
    /// rewritten existing rows before (an empty-string default for a string-converted enum, and a
    /// 0.0 default for a sim-rate column). Rolling back drops only the new table; no pre-existing
    /// row is read, moved or altered in either direction.
    /// </para>
    /// </summary>
    public partial class AddReputationSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReputationSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DateUtc = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    RecordedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReputationSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReputationSnapshots_AirlineId_DateUtc",
                table: "ReputationSnapshots",
                columns: new[] { "AirlineId", "DateUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReputationSnapshots");
        }
    }
}

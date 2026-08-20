using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <summary>
    /// Contract flying: two new tables, and the change that lets a Flight row describe a sector flown
    /// for another operator - a route and a fleet aircraft it does not have.
    ///
    /// <para><b>Flights is the one table in this app that cannot be reconstructed.</b> An airline, a
    /// fleet, routes and schedules can all be rebuilt by playing. A flight history cannot: it is a
    /// record of evenings somebody actually spent flying. SQLite implements AlterColumn by rebuilding
    /// the whole table, so this migration physically recreates Flights and copies every row - which is
    /// why it is proved against a file-backed database with realistic rows in every status, every
    /// awkward nullable read back field by field, and its FlightEvent and LedgerTransaction
    /// relationships checked to have survived. See ContractFlyingMigrationTests.</para>
    ///
    /// <para><b>Widening only, and no defaultValue anywhere in Up.</b> Every existing row holds a real
    /// RouteId and a real FleetAircraftId and keeps exactly the meaning it already had; NULL is a new
    /// state that no historical row can be in, because contract flying did not exist when they were
    /// written. A defaultValue here would be the mistake this project has shipped twice - a scaffolded
    /// default quietly rewriting the very rows the migration must leave alone.</para>
    /// </summary>
    public partial class AddContractFlying : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable because a contract flight has no route of the airline's own. Deliberately not
            // a Guid.Empty sentinel: roughly thirty places read this column, and under a sentinel
            // every one of them would have compiled and every one of them would have been wrong in a
            // way nothing reported - see Flight.RouteId's own doc for the full reasoning and for what
            // the Finances page would have done with it.
            migrationBuilder.AlterColumn<Guid>(
                name: "RouteId",
                table: "Flights",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            // Nullable because the aeroplane belongs to the operator who offered the job. This null
            // is what makes "a contract flight never touches the player's fleet" structural rather
            // than a rule somebody has to keep remembering - see Flight.FleetAircraftId.
            migrationBuilder.AlterColumn<Guid>(
                name: "FleetAircraftId",
                table: "Flights",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            // No default, and none is wanted: every row that already exists is an airline sector, and
            // NULL is exactly what an airline sector should say here. A default of Guid.Empty would
            // have claimed every historical flight was flown against a contract leg that does not
            // exist.
            migrationBuilder.AddColumn<Guid>(
                name: "ContractLegId",
                table: "Flights",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Contracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    BoardBucket = table.Column<long>(type: "INTEGER", nullable: false),
                    BoardSlot = table.Column<int>(type: "INTEGER", nullable: false),
                    OperatorName = table.Column<string>(type: "TEXT", nullable: false),
                    AircraftTypeDesignator = table.Column<string>(type: "TEXT", nullable: false),
                    LoadDescription = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadKg = table.Column<int>(type: "INTEGER", nullable: false),
                    PaxCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Fee = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TotalDistanceNm = table.Column<double>(type: "REAL", nullable: false),
                    TotalPlannedBlockMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    OfferedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AcceptedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClosedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ClosedReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contracts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContractLegs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    DepartureIcao = table.Column<string>(type: "TEXT", nullable: false),
                    ArrivalIcao = table.Column<string>(type: "TEXT", nullable: false),
                    DistanceNm = table.Column<double>(type: "REAL", nullable: false),
                    PlannedBlockMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    FeeShare = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    FlightId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FlownUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractLegs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractLegs_Contracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "Contracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractLegs_ContractId",
                table: "ContractLegs",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_AirlineId",
                table: "Contracts",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_AirlineId_BoardBucket_BoardSlot",
                table: "Contracts",
                columns: new[] { "AirlineId", "BoardBucket", "BoardSlot" },
                unique: true);
        }

        /// <summary>
        /// Rolling back is necessarily lossy, and it is worth being precise about how. Dropping the
        /// contract tables discards the jobs themselves; narrowing the two columns back to NOT NULL
        /// then has nothing sensible to put in a contract flight's row, so
        /// <c>Guid.Empty</c> is what those sectors collapse to - a flight pointing at a route and an
        /// aircraft that have never existed.
        /// <para>
        /// There is no better option going backwards: the information genuinely is not representable
        /// in the old schema, which is the whole reason the schema changed. What matters is that this
        /// only affects contract sectors. Every ordinary airline flight keeps its real RouteId and
        /// FleetAircraftId untouched, so a rollback costs the player their contract history and
        /// nothing else.
        /// </para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractLegs");

            migrationBuilder.DropTable(
                name: "Contracts");

            migrationBuilder.DropColumn(
                name: "ContractLegId",
                table: "Flights");

            migrationBuilder.AlterColumn<Guid>(
                name: "RouteId",
                table: "Flights",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FleetAircraftId",
                table: "Flights",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FSOps.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AircraftTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IcaoType = table.Column<string>(type: "TEXT", nullable: false),
                    Family = table.Column<string>(type: "TEXT", nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    PaxCapacity = table.Column<int>(type: "INTEGER", nullable: false),
                    RangeNm = table.Column<int>(type: "INTEGER", nullable: false),
                    CruiseTasKts = table.Column<int>(type: "INTEGER", nullable: false),
                    FuelBurnKgPerHour = table.Column<double>(type: "REAL", nullable: false),
                    MinRunwayFt = table.Column<int>(type: "INTEGER", nullable: false),
                    PurchasePrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    MonthlyLeaseRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    MatchPatterns = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AircraftTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Airlines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IcaoCode = table.Column<string>(type: "TEXT", nullable: false),
                    HomeAirportIcao = table.Column<string>(type: "TEXT", nullable: false),
                    StrategyProfile = table.Column<string>(type: "TEXT", nullable: false),
                    AccentColour = table.Column<string>(type: "TEXT", nullable: false),
                    ReputationScore = table.Column<double>(type: "REAL", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Airlines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Airports",
                columns: table => new
                {
                    Icao = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    Iata = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Municipality = table.Column<string>(type: "TEXT", nullable: false),
                    Country = table.Column<string>(type: "TEXT", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    ElevationFt = table.Column<int>(type: "INTEGER", nullable: false),
                    SizeCategory = table.Column<string>(type: "TEXT", nullable: false),
                    HasScheduledService = table.Column<bool>(type: "INTEGER", nullable: false),
                    LongestRunwayFt = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Airports", x => x.Icao);
                });

            migrationBuilder.CreateTable(
                name: "EconomyStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastProcessedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FuelPricePerKg = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    WorldSeed = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EconomyStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FleetAircraft",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AircraftTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Registration = table.Column<string>(type: "TEXT", nullable: false),
                    Ownership = table.Column<string>(type: "TEXT", nullable: false),
                    AirframeHours = table.Column<double>(type: "REAL", nullable: false),
                    HoursSinceACheck = table.Column<double>(type: "REAL", nullable: false),
                    HoursSinceCCheck = table.Column<double>(type: "REAL", nullable: false),
                    ConditionPercent = table.Column<double>(type: "REAL", nullable: false),
                    LocationIcao = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FleetAircraft", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FlightEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FlightId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlightEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Flights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FleetAircraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PilotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PlannedDepartureUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PlannedBlockMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    OutUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OffUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    OnUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    InUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PaxBooked = table.Column<int>(type: "INTEGER", nullable: false),
                    PaxFlown = table.Column<int>(type: "INTEGER", nullable: false),
                    FuelPlannedKg = table.Column<double>(type: "REAL", nullable: false),
                    FuelUsedKg = table.Column<double>(type: "REAL", nullable: false),
                    LandingFpmFirst = table.Column<double>(type: "REAL", nullable: true),
                    LandingFpmHardest = table.Column<double>(type: "REAL", nullable: true),
                    LandingGForce = table.Column<double>(type: "REAL", nullable: true),
                    CentrelineDeviationM = table.Column<double>(type: "REAL", nullable: true),
                    TitleFlown = table.Column<string>(type: "TEXT", nullable: false),
                    TypeMismatch = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revenue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    TotalCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Flights", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Leases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FleetAircraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MonthlyRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LedgerTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    FlightId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Loans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Principal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    AnnualInterestRate = table.Column<double>(type: "REAL", nullable: false),
                    TermMonths = table.Column<int>(type: "INTEGER", nullable: false),
                    MonthlyPayment = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    RemainingBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsPaidOff = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FleetAircraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Cost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pilots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsPlayer = table.Column<bool>(type: "INTEGER", nullable: false),
                    MonthlySalary = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    HoursFlown = table.Column<double>(type: "REAL", nullable: false),
                    SkillRating = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pilots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirlineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DepartureIcao = table.Column<string>(type: "TEXT", nullable: false),
                    ArrivalIcao = table.Column<string>(type: "TEXT", nullable: false),
                    DistanceNm = table.Column<double>(type: "REAL", nullable: false),
                    BaseFare = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FleetAircraftId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PilotId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DepartureTimeUtc = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    DaysOfWeekMask = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runways",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirportIcao = table.Column<string>(type: "TEXT", nullable: false),
                    Designator = table.Column<string>(type: "TEXT", nullable: false),
                    LengthFt = table.Column<int>(type: "INTEGER", nullable: false),
                    WidthFt = table.Column<int>(type: "INTEGER", nullable: false),
                    Surface = table.Column<string>(type: "TEXT", nullable: false),
                    HeadingTrue = table.Column<double>(type: "REAL", nullable: false),
                    LatitudeStart = table.Column<double>(type: "REAL", nullable: true),
                    LongitudeStart = table.Column<double>(type: "REAL", nullable: true),
                    LatitudeEnd = table.Column<double>(type: "REAL", nullable: true),
                    LongitudeEnd = table.Column<double>(type: "REAL", nullable: true),
                    IsLighted = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runways", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runways_Airports_AirportIcao",
                        column: x => x.AirportIcao,
                        principalTable: "Airports",
                        principalColumn: "Icao",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AircraftTypes_IcaoType",
                table: "AircraftTypes",
                column: "IcaoType");

            migrationBuilder.CreateIndex(
                name: "IX_Airlines_OwnerUserId",
                table: "Airlines",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Airports_Iata",
                table: "Airports",
                column: "Iata");

            migrationBuilder.CreateIndex(
                name: "IX_Airports_Icao",
                table: "Airports",
                column: "Icao");

            migrationBuilder.CreateIndex(
                name: "IX_Airports_Name",
                table: "Airports",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_FleetAircraft_AirlineId",
                table: "FleetAircraft",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_FlightEvents_FlightId",
                table: "FlightEvents",
                column: "FlightId");

            migrationBuilder.CreateIndex(
                name: "IX_Flights_AirlineId",
                table: "Flights",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_Leases_AirlineId",
                table: "Leases",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerTransactions_AirlineId",
                table: "LedgerTransactions",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_AirlineId",
                table: "Loans",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceEvents_FleetAircraftId",
                table: "MaintenanceEvents",
                column: "FleetAircraftId");

            migrationBuilder.CreateIndex(
                name: "IX_Pilots_AirlineId",
                table: "Pilots",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_Routes_AirlineId",
                table: "Routes",
                column: "AirlineId");

            migrationBuilder.CreateIndex(
                name: "IX_Runways_AirportIcao",
                table: "Runways",
                column: "AirportIcao");

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_RouteId",
                table: "Schedules",
                column: "RouteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AircraftTypes");

            migrationBuilder.DropTable(
                name: "Airlines");

            migrationBuilder.DropTable(
                name: "EconomyStates");

            migrationBuilder.DropTable(
                name: "FleetAircraft");

            migrationBuilder.DropTable(
                name: "FlightEvents");

            migrationBuilder.DropTable(
                name: "Flights");

            migrationBuilder.DropTable(
                name: "Leases");

            migrationBuilder.DropTable(
                name: "LedgerTransactions");

            migrationBuilder.DropTable(
                name: "Loans");

            migrationBuilder.DropTable(
                name: "MaintenanceEvents");

            migrationBuilder.DropTable(
                name: "Pilots");

            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropTable(
                name: "Runways");

            migrationBuilder.DropTable(
                name: "Schedules");

            migrationBuilder.DropTable(
                name: "Airports");
        }
    }
}

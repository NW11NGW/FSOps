using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// A fresh, isolated <see cref="FsOpsDbContext"/> backed by an in-memory SQLite database -
/// never the real file at AppPaths.DatabasePath. Each test builds its own connection, applies
/// migrations, and seeds a minimal airline/airport/fleet so route pairing tests never touch
/// %LOCALAPPDATA%\FSOps or run against each other's data.
/// </summary>
internal sealed class RouteTestContext : IDisposable
{
    public FsOpsDbContext Db { get; }

    public ICurrentUser CurrentUser { get; } = new LocalUser();

    public Airline Airline { get; private set; } = null!;

    public AircraftType AircraftType { get; private set; } = null!;

    /// <summary>Exposed so tests that need a second <see cref="FsOpsDbContext"/> pointed at the
    /// same in-memory database (e.g. to exercise code that resolves its own scope via DI) can open
    /// one against the same live connection instead of getting a separate, empty database.</summary>
    public SqliteConnection Connection => _connection;

    private readonly SqliteConnection _connection;

    private RouteTestContext(SqliteConnection connection, FsOpsDbContext db)
    {
        _connection = connection;
        Db = db;
    }

    public static async Task<RouteTestContext> CreateAsync()
    {
        // Keeping the connection open for the lifetime of the context is what keeps a
        // ":memory:" SQLite database alive across calls - EF Core never closes a connection it
        // didn't open itself.
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<FsOpsDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new FsOpsDbContext(options);
        await db.Database.MigrateAsync();

        var context = new RouteTestContext(connection, db);
        await context.SeedAsync();
        return context;
    }

    private async Task SeedAsync()
    {
        Db.Airports.AddRange(
            new Airport
            {
                Icao = "EGGD",
                Iata = "BRS",
                Name = "Bristol Airport",
                Municipality = "Bristol",
                Country = "GB",
                Latitude = 51.3827,
                Longitude = -2.7191,
                ElevationFt = 622,
                SizeCategory = AirportSizeCategory.Medium,
                HasScheduledService = true,
                LongestRunwayFt = 8000,
            },
            new Airport
            {
                Icao = "EGPH",
                Iata = "EDI",
                Name = "Edinburgh Airport",
                Municipality = "Edinburgh",
                Country = "GB",
                Latitude = 55.9500,
                Longitude = -3.3725,
                ElevationFt = 136,
                SizeCategory = AirportSizeCategory.Medium,
                HasScheduledService = true,
                LongestRunwayFt = 8500,
            },
            new Airport
            {
                Icao = "EGSS",
                Iata = "STN",
                Name = "London Stansted Airport",
                Municipality = "London",
                Country = "GB",
                Latitude = 51.8860,
                Longitude = 0.2389,
                ElevationFt = 348,
                SizeCategory = AirportSizeCategory.Medium,
                HasScheduledService = true,
                LongestRunwayFt = 10000,
            },
            new Airport
            {
                Icao = "EGPF",
                Iata = "GLA",
                Name = "Glasgow Airport",
                Municipality = "Glasgow",
                Country = "GB",
                Latitude = 55.8719,
                Longitude = -4.4331,
                ElevationFt = 26,
                SizeCategory = AirportSizeCategory.Medium,
                HasScheduledService = true,
                LongestRunwayFt = 8700,
            });

        AircraftType = new AircraftType
        {
            Id = Guid.NewGuid(),
            IcaoType = "A320",
            Family = "A320",
            Manufacturer = "Airbus",
            Name = "A320neo",
            PaxCapacity = 180,
            RangeNm = 3400,
            CruiseTasKts = 450,
            FuelBurnKgPerHour = 2400,
            MtowTonnes = 78.0,
            MinRunwayFt = 5500,
            ServiceCeilingFt = 39000,
            PurchasePrice = 100_000_000m,
            // Deliberately NOT the Casual (30,000) or True-life (380,000) A320 rate from
            // economy-config.json - lease pricing must never read this column (see
            // EconomyConfig.LeaseRates' doc comment), so tests that assert a specific charged rate
            // resolve it from EconomyConfigCatalog, not from this field.
            MonthlyLeaseRate = 500_000m,
            MatchPatterns = "[]",
        };
        Db.AircraftTypes.Add(AircraftType);

        Airline = new Airline
        {
            Id = Guid.NewGuid(),
            Name = "Test Air",
            IcaoCode = "TST",
            HomeAirportIcao = "EGGD",
            StrategyProfile = AirlineStrategyProfile.LowCost,
            OwnerUserId = CurrentUser.UserId,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        Db.Airlines.Add(Airline);

        Db.FleetAircraft.Add(new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = Airline.Id,
            AircraftTypeId = AircraftType.Id,
            Registration = "G-TEST",
            Ownership = AircraftOwnership.Owned,
            LocationIcao = "EGGD",
            Status = FleetAircraftStatus.Active,
            // Matches production: AirlineEndpoints.CreateAsync reserves a brand-new single-aircraft
            // fleet's sole airframe by default (docs/PLAN.md "3a" - "the single-aircraft case is
            // unchanged: the player's explicit choice IS the reservation"). Reservation is now a
            // hard, enforced invariant on both the Fly screen and the schedule builder, so a fixture
            // that diverges from what founding an airline actually does produces false greens -
            // tests that need a SCHEDULABLE aircraft instead call ReleaseReservationAsync (or
            // equivalent) explicitly, the same way they already had to for other non-default states.
            ReservedForPlayer = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        await Db.SaveChangesAsync();
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}

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

    /// <summary>The same database as <see cref="Connection"/>, addressable by name so a caller can
    /// open its OWN connection to it rather than sharing this one. Hand this to DI instead of the
    /// connection object whenever the code under test resolves scopes of its own: sharing a single
    /// connection across scopes means a scope initialising EF while another holds an open reader
    /// fails with "unable to delete/modify user-function due to active statements", which surfaces
    /// as a test that passes alone and fails under load. Production gives every scope its own
    /// connection, so this is also the more faithful arrangement.</summary>
    public string ConnectionString { get; }

    private readonly SqliteConnection _connection;

    private RouteTestContext(SqliteConnection connection, string connectionString, FsOpsDbContext db)
    {
        _connection = connection;
        ConnectionString = connectionString;
        Db = db;
    }

    public static async Task<RouteTestContext> CreateAsync()
    {
        // A NAMED shared-cache in-memory database rather than a bare ":memory:" one, which is
        // private to the single connection that opened it and therefore impossible to reach from a
        // second connection. The name is unique per context so tests stay isolated from each other.
        // Keeping this connection open for the lifetime of the context is still what keeps the
        // database alive - it is discarded the moment the last connection to it closes.
        var connectionString = $"Data Source=file:{Guid.NewGuid():N}?Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        var options = new DbContextOptionsBuilder<FsOpsDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new FsOpsDbContext(options);
        await db.Database.MigrateAsync();

        var context = new RouteTestContext(connection, connectionString, db);
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
            // fleet's sole airframe by default - with one airframe the player explicitly chooses
            // reserve-it-or-schedule-it, and that choice IS the reservation. Reservation is now a
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

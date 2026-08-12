using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Data.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests.WorldData;

/// <summary>
/// The proof that refreshing world data cannot damage a player's history.
///
/// <para>Everything here runs against a <b>file-backed</b> SQLite database on the real migrated
/// schema, seeded with the shape of a save that has actually been played: an airline with a home
/// base, two routes, an aircraft parked away from base, a completed flight with its OOOI times,
/// its append-only flight events, and its ledger lines. A second, deliberately hostile bundle is
/// then applied over it - one airport has <b>vanished</b> from the source, another's details have
/// <b>changed</b>, one is <b>new</b>, and one is untouched - and every field of every user-owned
/// row is read back afterwards.</para>
///
/// <para>The vanished airport is the case that matters. OurAirports removes and reclassifies
/// entries upstream, and nothing in the app can tell an editorial cleanup from a real closure. The
/// chosen behaviour is to keep such an airport exactly as it is - it is still the arrival of a
/// route the player built and still the location of an aircraft they own, and Route/FleetAircraft
/// reference airports by plain ICAO string with no foreign key, so deleting one would not fail
/// loudly, it would silently orphan real history.</para>
/// </summary>
public class WorldDataRefreshNonDestructiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fsops-worlddata-tests", Guid.NewGuid().ToString("N"));

    private string SeedDirectory => Path.Combine(_root, "seed");

    private string StampDirectory => Path.Combine(_root, "userdata");

    private string DatabasePath => Path.Combine(_root, "fsops.db");

    private DbContextOptions<FsOpsDbContext> Options =>
        new DbContextOptionsBuilder<FsOpsDbContext>()
            .UseSqlite($"Data Source={DatabasePath}")
            .Options;

    public WorldDataRefreshNonDestructiveTests()
    {
        Directory.CreateDirectory(SeedDirectory);
        Directory.CreateDirectory(StampDirectory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    // -------------------------------------------------------------------------------------
    // The two bundles
    // -------------------------------------------------------------------------------------

    /// <summary>The bundle the player's install was first seeded from.</summary>
    private static (AirportRow[] Airports, RunwayRow[] Runways) BundleOne() =>
    (
        new[]
        {
            new AirportRow("EGGD", "medium_airport", "Bristol Airport", 51.382702, -2.71909, 622, "GB", "Bristol", "yes", IataCode: "BRS"),
            new AirportRow("EGPH", "medium_airport", "Edinburgh Airport", 55.950100, -3.372500, 135, "GB", "Edinburgh", "yes", IataCode: "EDI"),
            new AirportRow("EGHI", "medium_airport", "Southampton Airport", 50.950298, -1.35679, 44, "GB", "Southampton", "yes", IataCode: "SOU"),
            new AirportRow("EGFF", "medium_airport", "Cardiff Airport", 51.396702, -3.343330, 220, "GB", "Cardiff", "yes", IataCode: "CWL"),
        },
        new[]
        {
            new RunwayRow("EGGD", 6598, 148, "ASP", true, false, "09", "27"),
            new RunwayRow("EGPH", 8400, 150, "ASP", true, false, "06", "24"),
            new RunwayRow("EGHI", 5653, 121, "ASP", true, false, "02", "20"),
            new RunwayRow("EGFF", 7999, 151, "ASP", true, false, "12", "30"),
        }
    );

    /// <summary>
    /// A newer bundle, built to be as awkward as the real upstream data gets:
    /// EGHI has vanished entirely, EGPH's details have changed (including its size category and
    /// scheduled-service flag), EGGD is byte-identical, and LFPG is new.
    /// </summary>
    private static (AirportRow[] Airports, RunwayRow[] Runways) BundleTwo() =>
    (
        new[]
        {
            new AirportRow("EGGD", "medium_airport", "Bristol Airport", 51.382702, -2.71909, 622, "GB", "Bristol", "yes", IataCode: "BRS"),
            // Changed: name, coordinates, elevation, municipality, size category, scheduled service, IATA.
            new AirportRow("EGPH", "large_airport", "Edinburgh International", 55.951000, -3.373000, 141, "GB", "Edinburgh City", "no", IataCode: "EDX"),
            // EGHI is gone from the source entirely.
            new AirportRow("EGFF", "medium_airport", "Cardiff Airport", 51.396702, -3.343330, 220, "GB", "Cardiff", "yes", IataCode: "CWL"),
            new AirportRow("LFPG", "large_airport", "Charles de Gaulle International", 49.012798, 2.550000, 392, "FR", "Paris", "yes", IataCode: "CDG"),
        },
        new[]
        {
            new RunwayRow("EGGD", 6598, 148, "ASP", true, false, "09", "27"),
            // EGPH: the old 06/24 is replaced by a longer, differently named pair.
            new RunwayRow("EGPH", 9200, 150, "CON", true, false, "06R", "24L"),
            new RunwayRow("EGFF", 7999, 151, "ASP", true, false, "12", "30"),
            new RunwayRow("LFPG", 13829, 197, "ASP", true, false, "08L", "26R"),
        }
    );

    private void WriteBundleOne()
    {
        var (airports, runways) = BundleOne();
        WorldDataFixtures.WriteBundle(SeedDirectory, airports, runways);
    }

    private void WriteBundleTwo()
    {
        var (airports, runways) = BundleTwo();
        WorldDataFixtures.WriteBundle(SeedDirectory, airports, runways);
    }

    private WorldDataImporter NewImporter() =>
        new(NullLogger<WorldDataImporter>.Instance, new WorldDataImportProgress());

    private async Task<WorldDataImportOutcome> RunAsync(bool force = false, WorldDataImporter? importer = null)
    {
        await using var db = new FsOpsDbContext(Options);
        return await (importer ?? NewImporter()).RunAsync(db, SeedDirectory, StampDirectory, force);
    }

    // -------------------------------------------------------------------------------------
    // A played save
    // -------------------------------------------------------------------------------------

    private static readonly Guid AirlineId = Guid.Parse("1B0F0F2A-6C2E-4E1E-9A55-8C7D0B111111");
    private static readonly Guid AircraftTypeId = Guid.Parse("2B0F0F2A-6C2E-4E1E-9A55-8C7D0B222222");
    private static readonly Guid HubAircraftId = Guid.Parse("3B0F0F2A-6C2E-4E1E-9A55-8C7D0B333333");
    private static readonly Guid AwayAircraftId = Guid.Parse("4B0F0F2A-6C2E-4E1E-9A55-8C7D0B444444");
    private static readonly Guid EdinburghRouteId = Guid.Parse("5B0F0F2A-6C2E-4E1E-9A55-8C7D0B555555");
    private static readonly Guid SouthamptonRouteId = Guid.Parse("6B0F0F2A-6C2E-4E1E-9A55-8C7D0B666666");
    private static readonly Guid FlightId = Guid.Parse("7B0F0F2A-6C2E-4E1E-9A55-8C7D0B777777");
    private static readonly Guid PilotId = Guid.Parse("8B0F0F2A-6C2E-4E1E-9A55-8C7D0B888888");

    private static readonly DateTimeOffset Departure = new(2026, 7, 14, 8, 30, 0, TimeSpan.Zero);

    private async Task SeedPlayedSaveAsync()
    {
        await using var db = new FsOpsDbContext(Options);

        db.Airlines.Add(new Airline
        {
            Id = AirlineId,
            Name = "Severn Air",
            IcaoCode = "SVN",
            HomeAirportIcao = "EGGD",
            StrategyProfile = AirlineStrategyProfile.Domestic,
            Playstyle = AirlinePlaystyle.Casual,
            AccentColour = "#0ea5e9",
            ReputationScore = 63.5,
            OwnerUserId = Guid.Parse("9B0F0F2A-6C2E-4E1E-9A55-8C7D0B999999"),
            CreatedUtc = Departure.AddMonths(-2),
        });

        db.AircraftTypes.Add(new AircraftType
        {
            Id = AircraftTypeId,
            IcaoType = "AT72",
            Family = "ATR",
            Manufacturer = "ATR",
            Name = "ATR 72-600",
            PaxCapacity = 70,
            RangeNm = 825,
            CruiseTasKts = 300,
            FuelBurnKgPerHour = 650,
            MtowTonnes = 23,
            MinRunwayFt = 4300,
            ServiceCeilingFt = 25000,
            PurchasePrice = 16_000_000m,
            MonthlyLeaseRate = 115_000m,
            MatchPatterns = "[]",
        });

        db.FleetAircraft.AddRange(
            new FleetAircraft
            {
                Id = HubAircraftId,
                AirlineId = AirlineId,
                AircraftTypeId = AircraftTypeId,
                Registration = "G-SVNA",
                Ownership = AircraftOwnership.Leased,
                AirframeHours = 812.25,
                HoursSinceACheck = 41.5,
                HoursSinceCCheck = 812.25,
                ConditionPercent = 94.75,
                FuelOnBoardKg = 1500,
                LocationIcao = "EGGD",
                Status = FleetAircraftStatus.Active,
                ReservedForPlayer = true,
                CreatedUtc = Departure.AddMonths(-2),
            },
            // Parked at the airport that is about to disappear from the world data.
            new FleetAircraft
            {
                Id = AwayAircraftId,
                AirlineId = AirlineId,
                AircraftTypeId = AircraftTypeId,
                Registration = "G-SVNB",
                Ownership = AircraftOwnership.Owned,
                AirframeHours = 233.75,
                HoursSinceACheck = 12.0,
                HoursSinceCCheck = 233.75,
                ConditionPercent = 99.5,
                FuelOnBoardKg = 2210.5,
                LocationIcao = "EGHI",
                Status = FleetAircraftStatus.Active,
                ReservedForPlayer = false,
                CreatedUtc = Departure.AddMonths(-1),
            });

        db.Pilots.Add(new Pilot
        {
            Id = PilotId,
            AirlineId = AirlineId,
            Name = "R. Whitfield",
            Status = PilotStatus.Available,
            HoursFlown = 640.5,
            SkillRating = 58.5,
            MonthlySalary = 4200m,
            CreatedUtc = Departure.AddMonths(-2),
        });

        db.Routes.AddRange(
            new Route
            {
                Id = EdinburghRouteId,
                AirlineId = AirlineId,
                DepartureIcao = "EGGD",
                ArrivalIcao = "EGPH",
                FlightNumber = "101",
                DistanceNm = 285.5,
                BaseFare = 78.50m,
                IsActive = true,
                CreatedUtc = Departure.AddMonths(-2),
            },
            // The route whose arrival airport vanishes upstream.
            new Route
            {
                Id = SouthamptonRouteId,
                AirlineId = AirlineId,
                DepartureIcao = "EGGD",
                ArrivalIcao = "EGHI",
                FlightNumber = "204A",
                DistanceNm = 62.25,
                BaseFare = 44.00m,
                IsActive = true,
                CreatedUtc = Departure.AddMonths(-1),
            });

        // A sector that was actually flown, to the airport that vanishes.
        db.Flights.Add(new Flight
        {
            Id = FlightId,
            AirlineId = AirlineId,
            RouteId = SouthamptonRouteId,
            FleetAircraftId = AwayAircraftId,
            PilotId = PilotId,
            Status = FlightStatus.Completed,
            PlannedDepartureUtc = Departure,
            PlannedBlockMinutes = 45,
            OutUtc = Departure.AddMinutes(3),
            OffUtc = Departure.AddMinutes(9),
            OnUtc = Departure.AddMinutes(41),
            InUtc = Departure.AddMinutes(47),
            PaxBooked = 63,
            PaxFlown = 61,
            FuelPlannedKg = 780,
            FuelUsedKg = 742.5,
            LandingFpmFirst = -188.5,
            LandingFpmHardest = -188.5,
            LandingGForce = 1.24,
            CentrelineDeviationM = 2.75,
            TitleFlown = "ATR 72-600 Severn Air",
            TypeMismatch = false,
            Revenue = 2684.00m,
            TotalCost = 1902.35m,
            RevenuePosted = true,
            CreatedUtc = Departure.AddDays(-1),
        });

        db.FlightEvents.AddRange(
            new FlightEvent { Id = Guid.NewGuid(), FlightId = FlightId, Utc = Departure.AddMinutes(3), Type = FlightEventType.PhaseChange, PayloadJson = "{\"phase\":\"Taxi\"}" },
            new FlightEvent { Id = Guid.NewGuid(), FlightId = FlightId, Utc = Departure.AddMinutes(41), Type = FlightEventType.Touchdown, PayloadJson = "{\"fpm\":-188.5}" });

        db.LedgerTransactions.AddRange(
            new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = AirlineId, Utc = Departure.AddMinutes(47), Category = LedgerCategory.TicketRevenue, Amount = 2684.00m, FlightId = FlightId, Description = "SVN204A EGGD-EGHI" },
            new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = AirlineId, Utc = Departure.AddMinutes(9), Category = LedgerCategory.Fuel, Amount = -612.35m, FlightId = FlightId, Description = "Fuel uplift EGGD" },
            new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = AirlineId, Utc = Departure.AddMinutes(47), Category = LedgerCategory.LandingFees, Amount = -290.00m, FlightId = FlightId, Description = "Landing EGHI" });

        await db.SaveChangesAsync();
    }

    private async Task MigrateAsync()
    {
        await using var db = new FsOpsDbContext(Options);
        await db.Database.MigrateAsync();
    }

    // -------------------------------------------------------------------------------------
    // The proof
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task ARefreshOverAPlayedSave_KeepsEveryUserRowAndEveryVanishedAirportItStillPointsAt()
    {
        await MigrateAsync();
        WriteBundleOne();

        var firstRun = await RunAsync();
        Assert.Equal(WorldDataImportResult.Seeded, firstRun.Result);
        Assert.Equal(4, firstRun.AirportCount);

        await SeedPlayedSaveAsync();

        // The newer bundle arrives with the next app update.
        WriteBundleTwo();

        var refresh = await RunAsync();

        Assert.Equal(WorldDataImportResult.Refreshed, refresh.Result);
        Assert.Equal(1, refresh.AirportsInserted);            // LFPG
        // Only EGPH: the count is rows actually rewritten, not rows looked at. EGGD and EGFF are
        // identical in both bundles and must cost nothing.
        Assert.Equal(1, refresh.AirportsChanged);
        Assert.Equal(1, refresh.AirportsRetainedNotInSource); // EGHI
        Assert.Equal(5, refresh.AirportCount);
        // Counted from the tables, so the retained airport's two runways are included. Counting
        // rows processed instead under-reports by exactly what was kept, which reads on screen as
        // "the refresh lost some of my data" when the opposite happened.
        Assert.Equal(10, refresh.RunwayCount);

        await using var db = new FsOpsDbContext(Options);

        // ---- The vanished airport is still there, byte for byte as it was seeded ----
        var southampton = await db.Airports.AsNoTracking().SingleOrDefaultAsync(a => a.Icao == "EGHI");
        Assert.NotNull(southampton);
        Assert.Equal("Southampton Airport", southampton!.Name);
        Assert.Equal("SOU", southampton.Iata);
        Assert.Equal("Southampton", southampton.Municipality);
        Assert.Equal("GB", southampton.Country);
        Assert.Equal(50.950298, southampton.Latitude, 6);
        Assert.Equal(-1.35679, southampton.Longitude, 6);
        Assert.Equal(44, southampton.ElevationFt);
        Assert.Equal(AirportSizeCategory.Medium, southampton.SizeCategory);
        Assert.True(southampton.HasScheduledService);
        // Not reset to zero by the refresh - its runways were never touched either.
        Assert.Equal(5653, southampton.LongestRunwayFt);

        var southamptonRunways = await db.Runways.AsNoTracking()
            .Where(r => r.AirportIcao == "EGHI").OrderBy(r => r.Designator).ToListAsync();
        Assert.Equal(2, southamptonRunways.Count);
        Assert.Equal(new[] { "02", "20" }, southamptonRunways.Select(r => r.Designator).ToArray());
        Assert.All(southamptonRunways, r => Assert.Equal(5653, r.LengthFt));

        // ---- The changed airport picked up every new value ----
        var edinburgh = await db.Airports.AsNoTracking().SingleAsync(a => a.Icao == "EGPH");
        Assert.Equal("Edinburgh International", edinburgh.Name);
        Assert.Equal("EDX", edinburgh.Iata);
        Assert.Equal("Edinburgh City", edinburgh.Municipality);
        Assert.Equal(55.951000, edinburgh.Latitude, 6);
        Assert.Equal(-3.373000, edinburgh.Longitude, 6);
        Assert.Equal(141, edinburgh.ElevationFt);
        Assert.Equal(AirportSizeCategory.Large, edinburgh.SizeCategory);
        Assert.False(edinburgh.HasScheduledService);
        Assert.Equal(9200, edinburgh.LongestRunwayFt);

        // Its runways were replaced, not doubled up.
        var edinburghRunways = await db.Runways.AsNoTracking()
            .Where(r => r.AirportIcao == "EGPH").OrderBy(r => r.Designator).ToListAsync();
        Assert.Equal(new[] { "06R", "24L" }, edinburghRunways.Select(r => r.Designator).ToArray());
        Assert.All(edinburghRunways, r => Assert.Equal("CON", r.Surface));

        // ---- The unchanged and the brand new ----
        var bristol = await db.Airports.AsNoTracking().SingleAsync(a => a.Icao == "EGGD");
        Assert.Equal("Bristol Airport", bristol.Name);
        Assert.Equal(6598, bristol.LongestRunwayFt);
        Assert.Equal(2, await db.Runways.CountAsync(r => r.AirportIcao == "EGGD"));

        var paris = await db.Airports.AsNoTracking().SingleAsync(a => a.Icao == "LFPG");
        Assert.Equal("Charles de Gaulle International", paris.Name);
        Assert.Equal("CDG", paris.Iata);
        Assert.Equal(13829, paris.LongestRunwayFt);

        Assert.Equal(5, await db.Airports.CountAsync());
        // 2 each for the four bundled airports, plus the retained EGHI's two.
        Assert.Equal(10, await db.Runways.CountAsync());

        // ---- Every user-owned row, field by field ----
        var airline = await db.Airlines.AsNoTracking().SingleAsync(a => a.Id == AirlineId);
        Assert.Equal("Severn Air", airline.Name);
        Assert.Equal("SVN", airline.IcaoCode);
        Assert.Equal("EGGD", airline.HomeAirportIcao);
        Assert.Equal(63.5, airline.ReputationScore);
        Assert.Equal(AirlinePlaystyle.Casual, airline.Playstyle);
        Assert.Null(airline.DeletedUtc);

        var edinburghRoute = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == EdinburghRouteId);
        Assert.Equal("EGGD", edinburghRoute.DepartureIcao);
        Assert.Equal("EGPH", edinburghRoute.ArrivalIcao);
        Assert.Equal("101", edinburghRoute.FlightNumber);
        Assert.Equal(285.5, edinburghRoute.DistanceNm);
        Assert.Equal(78.50m, edinburghRoute.BaseFare);
        Assert.True(edinburghRoute.IsActive);

        var southamptonRoute = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == SouthamptonRouteId);
        Assert.Equal("EGGD", southamptonRoute.DepartureIcao);
        Assert.Equal("EGHI", southamptonRoute.ArrivalIcao);
        Assert.Equal("204A", southamptonRoute.FlightNumber);
        Assert.Equal(62.25, southamptonRoute.DistanceNm);
        Assert.Equal(44.00m, southamptonRoute.BaseFare);

        var flight = await db.Flights.AsNoTracking().SingleAsync(f => f.Id == FlightId);
        Assert.Equal(FlightStatus.Completed, flight.Status);
        Assert.Equal(SouthamptonRouteId, flight.RouteId);
        Assert.Equal(AwayAircraftId, flight.FleetAircraftId);
        Assert.Equal(PilotId, flight.PilotId);
        Assert.Equal(Departure, flight.PlannedDepartureUtc);
        Assert.Equal(45, flight.PlannedBlockMinutes);
        Assert.Equal(Departure.AddMinutes(3), flight.OutUtc);
        Assert.Equal(Departure.AddMinutes(9), flight.OffUtc);
        Assert.Equal(Departure.AddMinutes(41), flight.OnUtc);
        Assert.Equal(Departure.AddMinutes(47), flight.InUtc);
        Assert.Equal(63, flight.PaxBooked);
        Assert.Equal(61, flight.PaxFlown);
        Assert.Equal(780, flight.FuelPlannedKg);
        Assert.Equal(742.5, flight.FuelUsedKg);
        Assert.Equal(-188.5, flight.LandingFpmFirst);
        Assert.Equal(1.24, flight.LandingGForce);
        Assert.Equal(2.75, flight.CentrelineDeviationM);
        Assert.Equal("ATR 72-600 Severn Air", flight.TitleFlown);
        Assert.False(flight.TypeMismatch);
        Assert.Equal(2684.00m, flight.Revenue);
        Assert.Equal(1902.35m, flight.TotalCost);
        Assert.True(flight.RevenuePosted);

        // Append-only tables are exactly as long as they were.
        Assert.Equal(2, await db.FlightEvents.CountAsync(e => e.FlightId == FlightId));
        var ledger = await db.LedgerTransactions.AsNoTracking().Where(l => l.AirlineId == AirlineId).ToListAsync();
        Assert.Equal(3, ledger.Count);
        // Materialised first - summing decimals in the database does not translate.
        Assert.Equal(1781.65m, ledger.Sum(l => l.Amount));

        var awayAircraft = await db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == AwayAircraftId);
        Assert.Equal("G-SVNB", awayAircraft.Registration);
        Assert.Equal("EGHI", awayAircraft.LocationIcao);
        Assert.Equal(233.75, awayAircraft.AirframeHours);
        Assert.Equal(99.5, awayAircraft.ConditionPercent);
        Assert.Equal(2210.5, awayAircraft.FuelOnBoardKg);
        Assert.Equal(AircraftOwnership.Owned, awayAircraft.Ownership);

        var hubAircraft = await db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == HubAircraftId);
        Assert.Equal("EGGD", hubAircraft.LocationIcao);
        Assert.True(hubAircraft.ReservedForPlayer);
        Assert.Equal(94.75, hubAircraft.ConditionPercent);

        // ---- And the references still resolve, which is the whole point ----
        var routeArrivals = await db.Routes.AsNoTracking()
            .Join(db.Airports.AsNoTracking(), r => r.ArrivalIcao, a => a.Icao, (r, a) => new { r.Id, a.Icao, a.Name })
            .ToListAsync();
        Assert.Equal(2, routeArrivals.Count);
        Assert.Contains(routeArrivals, x => x.Id == SouthamptonRouteId && x.Name == "Southampton Airport");

        var parkedAt = await db.FleetAircraft.AsNoTracking()
            .Join(db.Airports.AsNoTracking(), f => f.LocationIcao, a => a.Icao, (f, a) => new { f.Registration, a.Icao })
            .ToListAsync();
        Assert.Equal(2, parkedAt.Count);
        Assert.Contains(parkedAt, x => x.Registration == "G-SVNB" && x.Icao == "EGHI");

        var hubResolves = await db.Airlines.AsNoTracking()
            .Join(db.Airports.AsNoTracking(), al => al.HomeAirportIcao, a => a.Icao, (al, a) => a.Icao)
            .SingleAsync();
        Assert.Equal("EGGD", hubResolves);
    }

    [Fact]
    public async Task RefreshingTwiceOverTheSameBundle_ChangesNothingAtAll()
    {
        await MigrateAsync();
        WriteBundleOne();
        await RunAsync();
        await SeedPlayedSaveAsync();
        WriteBundleTwo();
        await RunAsync();

        List<Airport> before;
        List<Runway> beforeRunways;
        await using (var snapshot = new FsOpsDbContext(Options))
        {
            before = await snapshot.Airports.AsNoTracking().OrderBy(a => a.Icao).ToListAsync();
            beforeRunways = await snapshot.Runways.AsNoTracking().ToListAsync();
        }

        // Forced, so it genuinely re-reads and re-applies rather than short-circuiting on the stamp.
        var second = await RunAsync(force: true);
        Assert.Equal(WorldDataImportResult.Refreshed, second.Result);
        Assert.Equal(0, second.AirportsInserted);

        await using var db = new FsOpsDbContext(Options);
        var after = await db.Airports.AsNoTracking().OrderBy(a => a.Icao).ToListAsync();
        var afterRunways = await db.Runways.AsNoTracking().ToListAsync();

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Icao, after[i].Icao);
            Assert.Equal(before[i].Name, after[i].Name);
            Assert.Equal(before[i].Iata, after[i].Iata);
            Assert.Equal(before[i].Latitude, after[i].Latitude);
            Assert.Equal(before[i].Longitude, after[i].Longitude);
            Assert.Equal(before[i].ElevationFt, after[i].ElevationFt);
            Assert.Equal(before[i].SizeCategory, after[i].SizeCategory);
            Assert.Equal(before[i].HasScheduledService, after[i].HasScheduledService);
            Assert.Equal(before[i].LongestRunwayFt, after[i].LongestRunwayFt);
        }

        // Runways are replaced per airport, so a repeat must not accumulate duplicates.
        Assert.Equal(beforeRunways.Count, afterRunways.Count);

        // And the played save is still intact after a second pass over it.
        Assert.Equal(2, await db.Routes.CountAsync());
        Assert.Equal(1, await db.Flights.CountAsync());
        Assert.Equal(3, await db.LedgerTransactions.CountAsync());
    }

    // -------------------------------------------------------------------------------------
    // The version stamp
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// J28 - a closed runway must never count towards an airport's LongestRunwayFt: it isn't ground
    /// a player can actually use, and RunwaySuitabilityAssessor's length check (and every message
    /// that quotes this figure) trusts it as "the longest runway you could actually land on".
    /// EGGD's only long runway here is closed; its one open runway is much shorter, so a defect that
    /// ignored the closed flag would stamp the closed runway's length instead of the open one's.
    /// </summary>
    [Fact]
    public async Task AClosedRunway_NeverCountsTowardsAnAirportsLongestRunway()
    {
        await MigrateAsync();
        WorldDataFixtures.WriteBundle(
            SeedDirectory,
            new[] { new AirportRow("EGGD", "medium_airport", "Bristol Airport", 51.382702, -2.71909, 622, "GB", "Bristol", "yes", IataCode: "BRS") },
            new[]
            {
                // Closed and much longer than the open runway - a defect that ignores IsClosed
                // would stamp 9999, not 2000.
                new RunwayRow("EGGD", 9999, 148, "ASP", true, Closed: true, "09", "27"),
                new RunwayRow("EGGD", 2000, 100, "ASP", true, Closed: false, "13", "31"),
            });

        await RunAsync();

        await using var db = new FsOpsDbContext(Options);
        var bristol = await db.Airports.AsNoTracking().SingleAsync(a => a.Icao == "EGGD");
        Assert.Equal(2000, bristol.LongestRunwayFt);

        var runways = await db.Runways.AsNoTracking().Where(r => r.AirportIcao == "EGGD").ToListAsync();
        Assert.Equal(4, runways.Count); // 2 directional rows per physical runway
        Assert.Contains(runways, r => r.LengthFt == 9999 && r.IsClosed);
        Assert.Contains(runways, r => r.LengthFt == 2000 && !r.IsClosed);
    }

    [Fact]
    public async Task AnUnchangedBundle_IsNotReimportedOnTheNextLaunch()
    {
        await MigrateAsync();
        WriteBundleOne();

        Assert.Equal(WorldDataImportResult.Seeded, (await RunAsync()).Result);

        // Simulates the next launch: same files, same app.
        var second = await RunAsync();
        Assert.Equal(WorldDataImportResult.UpToDate, second.Result);

        Assert.True(File.Exists(Path.Combine(StampDirectory, WorldDataStamp.FileName)));
    }

    [Fact]
    public async Task ANewerBundle_RefreshesWithoutAnyoneAskingForIt()
    {
        await MigrateAsync();
        WriteBundleOne();
        await RunAsync();

        // The app updates and ships fresher CSVs.
        WriteBundleTwo();

        var afterUpdate = await RunAsync();
        Assert.Equal(WorldDataImportResult.Refreshed, afterUpdate.Result);

        await using var db = new FsOpsDbContext(Options);
        Assert.Equal("Edinburgh International", (await db.Airports.SingleAsync(a => a.Icao == "EGPH")).Name);
        Assert.NotNull(await db.Airports.SingleOrDefaultAsync(a => a.Icao == "LFPG"));
    }

    /// <summary>
    /// The failure this asymmetry exists for: a stamp file left behind by a database that was
    /// deleted or replaced must never convince the app that it already has airports.
    /// </summary>
    [Fact]
    public async Task AStampClaimingCurrentDataOverAnEmptyDatabase_StillSeeds()
    {
        await MigrateAsync();
        WriteBundleOne();
        await RunAsync();

        await using (var wipe = new FsOpsDbContext(Options))
        {
            await wipe.Runways.ExecuteDeleteAsync();
            await wipe.Airports.ExecuteDeleteAsync();
        }

        Assert.True(File.Exists(Path.Combine(StampDirectory, WorldDataStamp.FileName)));

        var recovered = await RunAsync();
        Assert.Equal(WorldDataImportResult.Seeded, recovered.Result);

        await using var db = new FsOpsDbContext(Options);
        Assert.Equal(4, await db.Airports.CountAsync());
    }

    /// <summary>The reverse: full tables and no stamp must refresh, not sit on unknown data forever.</summary>
    [Fact]
    public async Task AMissingStampOverFullTables_Refreshes()
    {
        await MigrateAsync();
        WriteBundleOne();
        await RunAsync();

        File.Delete(Path.Combine(StampDirectory, WorldDataStamp.FileName));

        var recovered = await RunAsync();
        Assert.Equal(WorldDataImportResult.Refreshed, recovered.Result);
        Assert.Equal(0, recovered.AirportsInserted);
        // Re-applying the identical bundle rewrites nothing at all.
        Assert.Equal(0, recovered.AirportsChanged);
    }

    [Fact]
    public async Task AnUnreadableStamp_IsTreatedAsMissingRatherThanFailing()
    {
        await MigrateAsync();
        WriteBundleOne();
        await RunAsync();

        await File.WriteAllTextAsync(Path.Combine(StampDirectory, WorldDataStamp.FileName), "{ this is not json");

        var recovered = await RunAsync();
        Assert.Equal(WorldDataImportResult.Refreshed, recovered.Result);
    }

    /// <summary>
    /// The runway pass deletes an airport's existing runways before inserting its new ones, and
    /// those deletes go through <c>ExecuteDelete</c> rather than the change tracker - which does
    /// not obviously join the enclosing transaction. If it did not, a failure after the deletes
    /// would leave airports with no runways at all and no way back. This forces exactly that
    /// failure and proves the deleted rows come back.
    /// </summary>
    [Fact]
    public async Task AFailureAfterRunwaysHaveBeenDeleted_BringsEveryDeletedRunwayBack()
    {
        await MigrateAsync();
        WriteBundleOne();
        await RunAsync();
        await SeedPlayedSaveAsync();

        List<Runway> before;
        await using (var snapshot = new FsOpsDbContext(Options))
        {
            before = await snapshot.Runways.AsNoTracking()
                .OrderBy(r => r.AirportIcao).ThenBy(r => r.Designator).ToListAsync();
        }
        Assert.Equal(8, before.Count);

        WriteBundleTwo();

        var interceptor = new ThrowOnRunwayInsertInterceptor();
        var sabotaged = new DbContextOptionsBuilder<FsOpsDbContext>()
            .UseSqlite($"Data Source={DatabasePath}")
            .AddInterceptors(interceptor)
            .Options;

        await using (var db = new FsOpsDbContext(sabotaged))
        {
            // Fails on the first runway INSERT - by which point the purge has already deleted the
            // existing runways of at least one airport.
            await Assert.ThrowsAnyAsync<Exception>(
                () => NewImporter().RunAsync(db, SeedDirectory, StampDirectory, force: true));
        }

        Assert.True(interceptor.Fired, "The test must actually have failed during a runway insert.");

        await using var verify = new FsOpsDbContext(Options);
        var after = await verify.Runways.AsNoTracking()
            .OrderBy(r => r.AirportIcao).ThenBy(r => r.Designator).ToListAsync();

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Id, after[i].Id);
            Assert.Equal(before[i].AirportIcao, after[i].AirportIcao);
            Assert.Equal(before[i].Designator, after[i].Designator);
            Assert.Equal(before[i].LengthFt, after[i].LengthFt);
            Assert.Equal(before[i].Surface, after[i].Surface);
        }

        // The airport upsert that ran before the failure is gone too - not a half-applied mixture.
        Assert.Equal("Edinburgh Airport", (await verify.Airports.SingleAsync(a => a.Icao == "EGPH")).Name);
        Assert.Null(await verify.Airports.SingleOrDefaultAsync(a => a.Icao == "LFPG"));
    }

    private sealed class ThrowOnRunwayInsertInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            Guard(command);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Guard(command);
            return ValueTask.FromResult(result);
        }

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> NonQueryExecuting(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            Guard(command);
            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Guard(command);
            return ValueTask.FromResult(result);
        }

        private void Guard(System.Data.Common.DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO \"Runways\"", StringComparison.OrdinalIgnoreCase))
            {
                Fired = true;
                throw new InvalidOperationException("Simulated failure while writing runways.");
            }
        }
    }

    /// <summary>
    /// A bundle whose runway file decompresses to nothing must be refused rather than committed. A
    /// truncated gzip does not reliably throw - it can simply yield no rows - and committing that
    /// would stamp the damaged bundle as applied, so the app would never retry it.
    /// </summary>
    [Fact]
    public async Task ABundleWhoseRunwayFileYieldsNothing_IsRefusedAndChangesNothing()
    {
        await MigrateAsync();
        WriteBundleOne();
        await RunAsync();
        await SeedPlayedSaveAsync();

        List<Airport> before;
        List<Runway> beforeRunways;
        await using (var snapshot = new FsOpsDbContext(Options))
        {
            before = await snapshot.Airports.AsNoTracking().OrderBy(a => a.Icao).ToListAsync();
            beforeRunways = await snapshot.Runways.AsNoTracking().OrderBy(r => r.AirportIcao).ThenBy(r => r.Designator).ToListAsync();
        }

        // The new bundle's airports parse fine, then the runway file turns out to be unreadable -
        // so the failure lands after the airport upsert and after the first runway deletes.
        var (airports, _) = BundleTwo();
        WorldDataFixtures.WriteBundle(SeedDirectory, airports, Array.Empty<RunwayRow>());
        await File.WriteAllBytesAsync(Path.Combine(SeedDirectory, "runways.csv.gz"), new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0x41, 0x42, 0x43, 0x44 });

        await Assert.ThrowsAnyAsync<Exception>(() => RunAsync());

        await using var db = new FsOpsDbContext(Options);
        var after = await db.Airports.AsNoTracking().OrderBy(a => a.Icao).ToListAsync();
        var afterRunways = await db.Runways.AsNoTracking().OrderBy(r => r.AirportIcao).ThenBy(r => r.Designator).ToListAsync();

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Icao, after[i].Icao);
            Assert.Equal(before[i].Name, after[i].Name);
            Assert.Equal(before[i].Iata, after[i].Iata);
            Assert.Equal(before[i].SizeCategory, after[i].SizeCategory);
            Assert.Equal(before[i].LongestRunwayFt, after[i].LongestRunwayFt);
        }

        Assert.Equal(beforeRunways.Count, afterRunways.Count);
        for (var i = 0; i < beforeRunways.Count; i++)
        {
            Assert.Equal(beforeRunways[i].AirportIcao, afterRunways[i].AirportIcao);
            Assert.Equal(beforeRunways[i].Designator, afterRunways[i].Designator);
            Assert.Equal(beforeRunways[i].LengthFt, afterRunways[i].LengthFt);
        }

        // The stamp was not written, so the next launch retries rather than sitting on data that
        // never actually landed.
        var stamp = WorldDataStampStore.TryRead(StampDirectory);
        Assert.NotNull(stamp);
        Assert.Equal(4, stamp!.AirportCount);

        // And the slot was released, so a later refresh is not locked out by the failed one.
        WriteBundleOne();
        var retry = await RunAsync(force: true);
        Assert.Equal(WorldDataImportResult.Refreshed, retry.Result);
    }

    [Fact]
    public async Task MissingSeedFiles_ChangeNothingAndDoNotThrow()
    {
        await MigrateAsync();
        WriteBundleOne();
        await RunAsync();
        await SeedPlayedSaveAsync();

        File.Delete(Path.Combine(SeedDirectory, "airports.csv.gz"));

        var outcome = await RunAsync();
        Assert.Equal(WorldDataImportResult.SeedFilesMissing, outcome.Result);

        await using var db = new FsOpsDbContext(Options);
        Assert.Equal(4, await db.Airports.CountAsync());
        Assert.Equal(2, await db.Routes.CountAsync());
    }

    /// <summary>
    /// Two refreshes cannot run at once - the startup check and the Settings button can genuinely
    /// race, and two importers writing the same tables would fight over SQLite's write lock.
    /// </summary>
    [Fact]
    public async Task ARefreshWhileAnotherIsRunning_IsTurnedAwayRatherThanRunConcurrently()
    {
        await MigrateAsync();
        WriteBundleOne();

        var progress = new WorldDataImportProgress();
        var importer = new WorldDataImporter(NullLogger<WorldDataImporter>.Instance, progress);

        Assert.True(progress.TryBegin(), "The test itself must be able to claim the slot first.");
        try
        {
            await using var db = new FsOpsDbContext(Options);
            var outcome = await importer.RunAsync(db, SeedDirectory, StampDirectory, force: true);
            Assert.Equal(WorldDataImportResult.AlreadyRunning, outcome.Result);
            Assert.Equal(0, await db.Airports.CountAsync());
        }
        finally
        {
            progress.End();
        }
    }

    /// <summary>
    /// A refresh must not make the UI claim the app has no airports - every screen keeps working
    /// throughout, because the rows are all still there while the upsert runs.
    /// </summary>
    [Fact]
    public async Task DuringARefresh_TheStatusStillReportsSeededData()
    {
        await MigrateAsync();
        WriteBundleOne();

        var progress = new WorldDataImportProgress();
        var importer = new WorldDataImporter(NullLogger<WorldDataImporter>.Instance, progress);

        await using (var seedDb = new FsOpsDbContext(Options))
        {
            await importer.RunAsync(seedDb, SeedDirectory, StampDirectory, force: false);
        }

        progress.MarkRefreshStarted();
        Assert.True(progress.Seeded);
        Assert.False(progress.ImportInProgress);
        Assert.True(progress.RefreshInProgress);

        // A first-time seed reports the opposite, which is what the dashboard banner keys off.
        progress.MarkStarted();
        Assert.False(progress.Seeded);
        Assert.True(progress.ImportInProgress);
        Assert.False(progress.RefreshInProgress);
    }
}

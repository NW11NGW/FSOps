using System.Text.Json;
using System.Threading.Channels;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using FSOps.Sim;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The question the whole first-fix fix exists to answer, asked the only way that actually answers
/// it: replay the REAL recorded EGGD-EGPH telemetry through the REAL pipeline, let it finish, and
/// look in the LEDGER. "Valid for payment" is a flag; being paid is a row of money, and only the
/// second one is what the player lost.
/// <para>
/// Everything here is production wiring. The flight is created by
/// <see cref="FlightEndpoints.StartAsync"/> (which is what calls
/// <c>FlightLifecycleService.BeginTracking</c> with the departure airport's real position, arming
/// the integrity monitor's opening-fix guard); telemetry goes through a real
/// <see cref="SimTelemetryService"/>, so the <c>PositionAcquisitionGate</c> is genuinely in the
/// path; the phase machine reaches Shutdown off the samples themselves and fires
/// <c>FinalizeFlightAsync</c> on its own. Nothing is called directly to make the money appear.
/// </para>
/// <para>
/// Honest limit: the recorded fixture is the persisted 15-second PositionSnapshot stream and it
/// stops during taxi-in, because that is where the recording stops - so the taxi-to-stand and
/// shutdown that end the sector are appended synthetically (engines off, parking brake set, at the
/// last recorded position). Everything from the bad opening fix through to touchdown is the real
/// flight, unmodified. This proves the recorded shape of a real flight now pays. It is not a
/// simulator and does not claim to be.
/// </para>
/// </summary>
public class RealFlightIsPaidEndToEndTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The uninitialised opening fix that voided the real sector: the Bay of Bengal.</summary>
    private const double BadFixLat = 0.0;
    private const double BadFixLon = 90.0;

    private const double StartFuelKg = 5000;
    private const double EndFuelKg = 3200;

    [Fact]
    public async Task TheRealEggdToEgphFlight_ReplayedThroughTheWholePipeline_IsPaid()
    {
        var recorded = LoadRecordedFlight();
        Assert.True(recorded.Count > 200, "the fixture is the evidence; if it shrank, this proves nothing");

        // Guard the premise: the flight really does still open on the fix that caused all this.
        Assert.True(
            FSOps.Core.Planning.GreatCircle.DistanceNm(BadFixLat, BadFixLon, recorded[0].Lat, recorded[0].Lon) < 1.0,
            "the fixture no longer opens on the bad fix, so this test would pass for the wrong reason");

        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAndPilotAsync(ctx);

        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var samples = BuildTelemetry(recorded);

        // The flight is released in two parts. RouteTestContext backs every DbContext with ONE
        // shared in-memory SqliteConnection, so the batched flight-event writer saving at the same
        // instant as FinalizeFlightAsync throws "SqliteConnection does not support nested
        // transactions" - a harness limitation only (production gives each DI scope its own
        // connection). Letting the ~280 recorded snapshots drain before the shutdown samples arrive
        // removes the overlap without weakening anything: every sample still goes through the real
        // pump, the real gate, and the real subscription.
        var flightSamples = samples.Take(recorded.Count).ToList();
        var shutdownSamples = samples.Skip(recorded.Count).ToList();

        var source = new ReplaySimSource(flightSamples, complete: false);
        await using var telemetry = new SimTelemetryService(source, new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var errors = new CapturingLogger();
        var lifecycle = CreateLifecycle(ctx, telemetry, errors);

        // Subscribes to the telemetry service exactly as the running host does.
        await lifecycle.StartAsync(CancellationToken.None);

        // The real start path - this is what arms the integrity monitor with the departure position.
        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry,
            economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id);
        Assert.Equal(flight.Id, lifecycle.ActiveFlightId);

        // Nothing is on the ledger yet: fuel bills on burn, revenue on completion.
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync());

        await telemetry.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => telemetry.LastSampleUtc == flightSamples[^1].TimestampUtc, TimeSpan.FromSeconds(30));
        Assert.Equal(flightSamples[^1].TimestampUtc, telemetry.LastSampleUtc);

        // Let the batched flight-event writer finish the recorded flight before the shutdown that
        // triggers finalisation - see the pacing note above.
        await Task.Delay(TimeSpan.FromSeconds(2));
        source.Write(shutdownSamples);
        source.CompleteWriting();

        await WaitUntilAsync(() => telemetry.LastSampleUtc == samples[^1].TimestampUtc, TimeSpan.FromSeconds(30));
        Assert.Equal(samples[^1].TimestampUtc, telemetry.LastSampleUtc);

        // FinalizeFlightAsync is fired off the hot path when the machine reaches Shutdown, so wait
        // for the row rather than racing it.
        await WaitUntilAsync(
            () => ctx.Db.Flights.AsNoTracking().Single(f => f.Id == flight.Id).Status == FlightStatus.Completed,
            TimeSpan.FromSeconds(30));

        await telemetry.StopAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);

        var completed = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);

        // ---- 1. The sector is structurally valid ----
        Assert.True(
            completed.Status == FlightStatus.Completed,
            $"the sector ended as {completed.Status}. Errors logged: {string.Join(" | ", errors.Errors)}");
        Assert.False(completed.PositionJumpDetected, "the clean sector was voided for a position jump again");
        Assert.False(completed.SlewDetected);
        Assert.False(completed.SimRateElevated);

        // ---- 2. ...and the money actually moved ----
        var ledger = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.True(completed.RevenuePosted);

        var ticketRevenue = Assert.Single(ledger.Where(t => t.Category == LedgerCategory.TicketRevenue));
        Assert.True(ticketRevenue.Amount > 0, "the sector completed but no ticket revenue was posted");
        Assert.Equal(flight.Id, ticketRevenue.FlightId);
        Assert.True(completed.Revenue > 0);
        Assert.True(completed.PaxFlown > 0);

        // Every cost line a real sector incurs, so "paid" can't quietly mean "revenue only".
        Assert.Contains(ledger, t => t.Category == LedgerCategory.Fuel && t.Amount < 0);
        Assert.Contains(ledger, t => t.Category == LedgerCategory.LandingFees && t.Amount < 0);
        Assert.Contains(ledger, t => t.Category == LedgerCategory.Handling && t.Amount < 0);
        Assert.Contains(ledger, t => t.Category == LedgerCategory.CrewCost && t.Amount < 0);
        Assert.Contains(ledger, t => t.Category == LedgerCategory.Maintenance && t.Amount < 0);

        // Cash is SUM(LedgerTransaction.Amount) and nothing else has touched this airline, so this
        // is the flight's whole effect on the balance - checked against the Flight row's own
        // independently maintained cache of the same postings rather than against itself.
        Assert.Equal(ledger.Sum(t => t.Amount), completed.Revenue - completed.TotalCost);

        // ---- 3. ...and the burn billed is the one that was measured, not a planned fallback ----
        // Below the full synthetic 1,800 kg on purpose, and correctly so: the decreases during
        // Preflight, before the engines are seen running, are excluded from burn by design (a spawn
        // load or a menu fuel set is not fuel flown off - see FuelBurnResolver.Measure's tier 1).
        // The band is wide enough not to encode that arithmetic, tight enough that a planned-charge
        // fallback or a zero would fail it.
        Assert.InRange(completed.FuelUsedKg, 1200, StartFuelKg - EndFuelKg);

        // ---- 4. OOOI came off the real samples ----
        Assert.NotNull(completed.OutUtc);
        Assert.NotNull(completed.OffUtc);
        Assert.NotNull(completed.OnUtc);
        Assert.NotNull(completed.InUtc);

        // ---- 5. The aircraft ended up at Edinburgh, from telemetry, and is available again ----
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().FirstAsync();
        Assert.Equal("EGPH", fleetAircraft.LocationIcao);
        Assert.Equal(FleetAircraftStatus.Active, fleetAircraft.Status);
    }

    /// <summary>
    /// The same replay with the bad opening fix removed. If this sector's economics differ from the
    /// one above in any way, then the bad fix is still leaving a mark somewhere even though it no
    /// longer voids the sector - and "paid, but for slightly the wrong thing" is not a fix either.
    /// </summary>
    [Fact]
    public async Task TheRealFlightPaysTheSame_WithAndWithoutTheBadOpeningFix()
    {
        var recorded = LoadRecordedFlight();

        var withBadFix = await RunAndSummariseAsync(recorded, "with the bad opening fix");
        var withoutBadFix = await RunAndSummariseAsync(recorded.Skip(1).ToList(), "without the bad opening fix");

        Assert.Equal(withoutBadFix.LedgerTotal, withBadFix.LedgerTotal);
        Assert.Equal(withoutBadFix.Revenue, withBadFix.Revenue);
        Assert.Equal(withoutBadFix.TotalCost, withBadFix.TotalCost);
        Assert.Equal(withoutBadFix.PaxFlown, withBadFix.PaxFlown);
        Assert.Equal(withoutBadFix.OutUtc, withBadFix.OutUtc);
        Assert.Equal(withoutBadFix.InUtc, withBadFix.InUtc);
        Assert.True(withBadFix.Revenue > 0);
    }

    /// <summary>
    /// DEFECT, CHARACTERISED - and nothing to do with the first-fix work; found while enumerating
    /// every path that can stop a flown sector being paid.
    /// <para>
    /// <c>Route</c> is soft-deleted (<c>RouteEndpoints.DeleteAsync</c>) and carries a global query
    /// filter on <c>DeletedUtc == null</c>, and <c>RouteEndpoints.DeleteAsync</c> does not check
    /// whether a flight on that route is in progress. So a player who tidies up their route list
    /// while flying gets a sector that reaches <c>FlightStatus.Completed</c> with NO ticket revenue,
    /// no fees, and <c>RevenuePosted</c> still false - because <c>FinalizeFlightAsync</c> resolves
    /// the route to null and skips its whole economics block, having already set the status.
    /// </para>
    /// <para>
    /// It is also unrecoverable through the UI: <c>CompleteManualAsync</c> only accepts a flight
    /// that is InProgress or Interrupted, and this one is Completed. The flight is flown, logged,
    /// charged nothing and paid nothing.
    /// </para>
    /// Asserts current behaviour so the suite stays honest and green. Invert, do not delete.
    /// </summary>
    [Fact]
    public async Task DEFECT_RouteDeletedWhileTheFlightIsAirborne_CompletesTheSectorButPaysNothing()
    {
        var recorded = LoadRecordedFlight();

        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAndPilotAsync(ctx);
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == route.DepartureIcao);

        var samples = BuildTelemetry(recorded);
        await using var telemetry = new SimTelemetryService(
            new ReplaySimSource([]), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var errors = new CapturingLogger();
        var lifecycle = CreateLifecycle(ctx, telemetry, errors);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry,
            EconomyConfigCatalog.Default(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flightId = (await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id)).Id;
        var fleetAircraftId = (await ctx.Db.FleetAircraft.AsNoTracking().FirstAsync()).Id;

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flightId,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraftId,
            ArrivalIcao = route.ArrivalIcao,
            PlannedBlockMinutes = 60,
            Machine = new FSOps.Core.Flights.FlightPhaseStateMachine(),
            IntegrityMonitor = new FSOps.Core.Flights.FlightIntegrityMonitor((departure.Latitude, departure.Longitude)),
        };
        lifecycle.SetActiveTrackerForTests(tracker);

        // Fly everything up to the shutdown tail...
        var gate = new FSOps.Core.Flights.PositionAcquisitionGate();
        var upToTouchdown = samples.Take(recorded.Count).ToList();
        foreach (var sample in upToTouchdown)
        {
            if (gate.Accept(sample.LatitudeDeg, sample.LongitudeDeg, sample.TimestampUtc, sample.SimulationRate))
            {
                lifecycle.ProcessSample(tracker, sample);
            }
        }

        // ...then the player deletes the route they are in the middle of flying. This is the real
        // endpoint, with the real (absent) guard.
        var deleteResult = await RouteEndpoints.DeleteAsync(route.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(deleteResult));

        // ...and then parks and shuts down, which finalises the flight.
        foreach (var sample in samples.Skip(recorded.Count))
        {
            lifecycle.ProcessSample(tracker, sample);
        }

        await WaitUntilAsync(
            () => ctx.Db.Flights.AsNoTracking().Single(f => f.Id == flightId).Status == FlightStatus.Completed,
            TimeSpan.FromSeconds(30));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flightId);
        Assert.Equal(FlightStatus.Completed, flight.Status);

        // The sector was flown cleanly - this is not an integrity finding.
        Assert.False(flight.PositionJumpDetected);
        Assert.False(flight.SlewDetected);

        // And yet: nothing was paid, and nothing was charged either.
        var ledger = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.DoesNotContain(ledger, t => t.Category == LedgerCategory.TicketRevenue);
        Assert.Equal(0m, flight.Revenue);
        Assert.False(flight.RevenuePosted);

        // Unrecoverable: the manual-completion escape hatch refuses a Completed flight.
        // Deliberately through a FRESH DbContext on the same database, because that is what a real
        // HTTP request scope gets. Reusing ctx.Db would hand CompleteManualAsync the stale Flight
        // entity it has tracked since StartAsync - still reading InProgress, since finalisation
        // wrote through a different scope - and the call would wrongly appear to succeed.
        using var requestScopedDb = new FsOpsDbContext(
            new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(ctx.Connection).Options);
        var manualResult = await FlightEndpoints.CompleteManualAsync(
            flightId, requestScopedDb, ctx.CurrentUser, lifecycle, EconomyConfigCatalog.Default(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(manualResult));
    }

    private sealed record CompletionSummary(
        decimal LedgerTotal, decimal Revenue, decimal TotalCost, int PaxFlown,
        DateTimeOffset? OutUtc, DateTimeOffset? InUtc);

    /// <summary>
    /// Drives one replay to completion and reports its economics.
    /// <para>
    /// Deliberately does NOT start the lifecycle's own hosted background writer, and pushes samples
    /// through <c>ProcessSample</c> synchronously rather than through the telemetry pump. This is a
    /// TEST-HARNESS accommodation, not a shortcut around the code under test: RouteTestContext
    /// backs every DbContext with one shared in-memory <c>SqliteConnection</c>, and the batched
    /// flight-event writer saving on that connection at the same moment as <c>FinalizeFlightAsync</c>
    /// throws "SqliteConnection does not support nested transactions". Production gives each scope
    /// its own connection, so the collision cannot arise there. The
    /// <see cref="PositionAcquisitionGate"/> is still genuinely in the path - applied here exactly as
    /// <c>SimTelemetryService.AcceptPosition</c> applies it - because the whole point of this test is
    /// what the gate does and does not pass through.
    /// </para>
    /// <para>
    /// The full-pipeline proof, pump and all, is
    /// <see cref="TheRealEggdToEgphFlight_ReplayedThroughTheWholePipeline_IsPaid"/> above.
    /// </para>
    /// </summary>
    private static async Task<CompletionSummary> RunAndSummariseAsync(IReadOnlyList<RecordedSnapshot> recorded, string label)
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAndPilotAsync(ctx);
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == route.DepartureIcao);

        var samples = BuildTelemetry(recorded);
        await using var telemetry = new SimTelemetryService(
            new ReplaySimSource([]), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var errors = new CapturingLogger();
        var lifecycle = CreateLifecycle(ctx, telemetry, errors);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry,
            EconomyConfigCatalog.Default(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flightId = (await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id)).Id;
        var fleetAircraftId = (await ctx.Db.FleetAircraft.AsNoTracking().FirstAsync()).Id;

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flightId,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraftId,
            ArrivalIcao = route.ArrivalIcao,
            PlannedBlockMinutes = 60,
            Machine = new FSOps.Core.Flights.FlightPhaseStateMachine(),
            // Exactly what BeginTracking arms the monitor with - see FlightEndpoints.StartAsync.
            IntegrityMonitor = new FSOps.Core.Flights.FlightIntegrityMonitor((departure.Latitude, departure.Longitude)),
        };
        lifecycle.SetActiveTrackerForTests(tracker);

        var gate = new FSOps.Core.Flights.PositionAcquisitionGate();
        foreach (var sample in samples)
        {
            if (!gate.Accept(sample.LatitudeDeg, sample.LongitudeDeg, sample.TimestampUtc, sample.SimulationRate))
            {
                continue;
            }

            lifecycle.ProcessSample(tracker, sample);
        }

        await WaitUntilAsync(
            () => ctx.Db.Flights.AsNoTracking().Single(f => f.Id == flightId).Status == FlightStatus.Completed,
            TimeSpan.FromSeconds(30));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flightId);
        Assert.True(
            flight.Status == FlightStatus.Completed,
            $"the run {label} ended as {flight.Status}. Errors logged: {string.Join(" | ", errors.Errors)}");
        Assert.False(flight.PositionJumpDetected, $"the run {label} was voided for a position jump");

        var ledgerTotal = (await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync())
            .Sum(t => t.Amount);

        return new CompletionSummary(
            ledgerTotal, flight.Revenue, flight.TotalCost, flight.PaxFlown, flight.OutUtc, flight.InUtc);
    }

    /// <summary>
    /// Rebuilds full telemetry from the recorded 15-second snapshots, then appends the taxi-to-stand
    /// and shutdown the recording stops short of. The on-ground/engine/brake reconstruction is the
    /// same one FirstFixGatePipelineTests uses (and which is already proven to walk the flight
    /// through the same OOOI it walked on the day); fuel is a linear burn, since the snapshot stream
    /// never carried it.
    /// </summary>
    private static List<TelemetrySample> BuildTelemetry(IReadOnlyList<RecordedSnapshot> recorded)
    {
        var samples = new List<TelemetrySample>(recorded.Count + 8);

        // Anchored to the recording's END and a fixed span, deliberately NOT to the sample INDEX or
        // to the list's own first sample: TheRealFlightPaysTheSame_WithAndWithoutTheBadOpeningFix
        // replays the same flight with one leading sample removed, and an index-based curve would
        // hand the two runs slightly different fuel and make them differ for a reason that has
        // nothing to do with what the test is asking.
        var burnEndUtc = recorded[^1].Utc;
        var burnStartUtc = burnEndUtc - TimeSpan.FromMinutes(70);
        var burnSpan = (burnEndUtc - burnStartUtc).TotalSeconds;

        for (var i = 0; i < recorded.Count; i++)
        {
            var s = recorded[i];
            var elapsed = Math.Clamp((s.Utc - burnStartUtc).TotalSeconds / burnSpan, 0, 1);
            var fuelKg = StartFuelKg - (StartFuelKg - EndFuelKg) * elapsed;
            samples.Add(new TelemetrySample(
                s.Utc, s.Lat, s.Lon, s.AltAglFt + 100, s.AltAglFt,
                s.GsKt, s.GsKt, s.VsFpm, 0, 0,
                OnGround: s.AltAglFt < 15,
                EngineRunning: s.Phase != "Preflight" || s.GsKt > 0.1,
                ParkingBrakeSet: false,
                GForce: 1.0, TouchdownNormalVelocityFps: 0, TotalFuelKg: fuelKg,
                AircraftTitle: "Test Aircraft", AtcModel: "TEST", AtcType: "Test",
                SimulationRate: 1.0, IsSlewActive: false));
        }

        // On stand: stopped, engines off, parking brake set. This is what takes the phase machine
        // TaxiIn -> Shutdown, which is what fires finalisation.
        var last = recorded[^1];
        var utc = last.Utc;
        for (var i = 0; i < 4; i++)
        {
            utc = utc.AddSeconds(15);
            samples.Add(new TelemetrySample(
                utc, last.Lat, last.Lon, last.AltAglFt + 100, last.AltAglFt,
                0, 0, 0, 0, 0,
                OnGround: true, EngineRunning: false, ParkingBrakeSet: true,
                GForce: 1.0, TouchdownNormalVelocityFps: 0, TotalFuelKg: EndFuelKg,
                AircraftTitle: "Test Aircraft", AtcModel: "TEST", AtcType: "Test",
                SimulationRate: 1.0, IsSlewActive: false));
        }

        return samples;
    }

    private static async Task<Route> SeedRouteAndPilotAsync(RouteTestContext ctx)
    {
        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            FlightNumber = "101",
            DistanceNm = 280,
            BaseFare = 90m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);

        ctx.Db.Pilots.Add(new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = "Test Pilot",
            IsPlayer = true,
            MonthlySalary = 9000m,
            SkillRating = 50,
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        await ctx.Db.SaveChangesAsync();
        return route;
    }

    private static FlightLifecycleService CreateLifecycle(
        RouteTestContext ctx, SimTelemetryService telemetry, CapturingLogger? logger = null)
    {
        var services = new ServiceCollection();
        // The connection STRING, not the shared connection object: this replays 280 samples and
        // finalises on a background task, so a scope can be initialising EF while another still
        // holds an open reader. Sharing one connection makes that fail with "unable to
        // delete/modify user-function due to active statements" - which passes alone and fails in a
        // full-solution run, the most misleading shape a test can have.
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.ConnectionString));
        var provider = services.BuildServiceProvider();

        return new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            telemetry,
            new NoOpHubContext(),
            EconomyConfigCatalog.Default(),
            vatsimCorroboration: null,
            (ILogger<FlightLifecycleService>?)logger ?? NullLogger<FlightLifecycleService>.Instance);
    }

    /// <summary>
    /// Finalisation runs through <c>FlightLifecycleService.FireAndForget</c>, which catches and LOGS
    /// whatever it throws - so against a null logger a failed completion is indistinguishable from a
    /// slow one, and the test would report "still InProgress" for a reason it cannot see. This makes
    /// the swallowed failure visible.
    /// </summary>
    private sealed class CapturingLogger : ILogger<FlightLifecycleService>
    {
        private readonly List<string> _errors = new();

        public IReadOnlyList<string> Errors
        {
            get { lock (_errors) { return _errors.ToList(); } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error)
            {
                return;
            }

            lock (_errors)
            {
                _errors.Add($"{formatter(state, exception)} :: {exception}");
            }
        }
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private static List<RecordedSnapshot> LoadRecordedFlight()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Flights", "Fixtures", "eggd-egph-20260812-snapshots.json");
        return JsonSerializer.Deserialize<RecordedFlight>(File.ReadAllText(path), JsonOptions)!.Snapshots.ToList();
    }

    /// <summary>An ISimSource whose whole stream is written and closed up front, so the pump drains
    /// it and ends deterministically instead of the test having to sleep.</summary>
    private sealed class ReplaySimSource : ISimSource
    {
        private readonly Channel<TelemetrySample> _channel = Channel.CreateUnbounded<TelemetrySample>();

        public ReplaySimSource(IEnumerable<TelemetrySample> samples, bool complete = true)
        {
            Write(samples);
            if (complete)
            {
                _channel.Writer.Complete();
            }
        }

        /// <summary>Lets a test release the rest of the flight later - see the pacing note in
        /// <see cref="TheRealEggdToEgphFlight_ReplayedThroughTheWholePipeline_IsPaid"/>.</summary>
        public void Write(IEnumerable<TelemetrySample> samples)
        {
            foreach (var sample in samples)
            {
                _channel.Writer.TryWrite(sample);
            }
        }

        public void CompleteWriting() => _channel.Writer.Complete();

        public string Kind => "Replay";

        public SimConnectionState ConnectionState => SimConnectionState.Connected;

        public event EventHandler<SimConnectionState>? ConnectionStateChanged { add { } remove { } }

        public AircraftIdentity? CurrentAircraft => new("Test Aircraft", "TEST", "Test");

        public ChannelReader<TelemetrySample> Telemetry => _channel.Reader;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record RecordedFlight(string Source, IReadOnlyList<RecordedSnapshot> Snapshots);

    private sealed record RecordedSnapshot(
        DateTimeOffset Utc, double Lat, double Lon, double AltAglFt, double GsKt, double VsFpm, string Phase);
}

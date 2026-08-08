using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Core.Planning;
using FSOps.Data;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Route = FSOps.Core.Entities.Route;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves the fuel-honesty fix end to end, through the real path the player uses - never a
/// calculator called in isolation. <c>FlightEndpoints.StartAsync</c> posts the real fuel charge
/// (now <see cref="FuelBreakdown.ChargedFuelKg"/>, not the full block figure - see
/// docs/PLAN.md "Persistent fuel state and tankering" and "Status after the fuel-honesty fix"),
/// and <c>FlightLifecycleService.FinalizeFlightAsync</c> posts everything else, exactly as a real
/// completed flight does. Every figure asserted here comes from the actual posted ledger rows.
///
/// <para>Before the fix, <c>FlightEndpoints.StartAsync</c> charged the full
/// <c>BlockFuelEstimator</c> block-fuel total - trip + taxi + contingency + a flat alternate
/// allowance + a 30-minute final reserve - every sector, even though <c>FleetAircraft</c> has no
/// persisted tank state to carry the unburned alternate/reserve fuel forward. A live check on a
/// 275 nm EGGD->EGPH sector at the suggested fare showed a structural loss from this alone. The
/// three scenarios below cover exactly that regression: a short-haul sector must now be
/// profitable, a medium sector must be profitable and roughly in line with
/// <c>StartupTrajectoryTests</c>' figures, and the micro-sector exploit must stay loss-making (the
/// fix must not accidentally make trivial sectors pay).</para>
/// </summary>
public class FlightFuelChargeBalanceTests
{
    private static readonly DateTimeOffset Base = new(2026, 4, 15, 8, 0, 0, TimeSpan.Zero); // a Wednesday in April - matches StartupTrajectoryTests' scenario day.

    [Fact]
    public async Task ShortHaulSector_275Nm_AtTheSuggestedFare_IsProfitable()
    {
        var outcome = await FlyRealisticSectorAsync(
            arrivalIcao: "EGPH", latitudeDeg: 55.9500, longitudeDeg: -3.3725, sizeCategory: AirportSizeCategory.Medium);

        Assert.InRange(outcome.DistanceNm, 250, 350); // the plan's own "typical short-haul" band.
        Assert.True(outcome.NetProfit > 0,
            $"A {outcome.DistanceNm:F0} nm short-haul sector at the suggested fare ({outcome.Fare:C2}) should be " +
            $"profitable: revenue {outcome.Revenue:C2}, cost {outcome.TotalCost:C2}, net {outcome.NetProfit:C2} " +
            $"(fuel charge {outcome.FuelCost:C2}).");
        Assert.Equal(outcome.NetProfit, outcome.CashDelta);
    }

    [Fact]
    public async Task MediumSector_600Nm_AtTheSuggestedFare_IsProfitable_AndRoughlyMatchesStartupTrajectoryTests()
    {
        // No seeded airport pair is ~600 nm apart, so this adds one placed due south of EGGD -
        // GreatCircle.DistanceNm confirms the real resulting distance rather than assuming it.
        var outcome = await FlyRealisticSectorAsync(
            arrivalIcao: "LFXX", latitudeDeg: 41.3827, longitudeDeg: -2.7191, sizeCategory: AirportSizeCategory.Medium,
            seedArrivalAirport: true);

        Assert.InRange(outcome.DistanceNm, 550, 650);
        Assert.True(outcome.NetProfit > 0,
            $"A {outcome.DistanceNm:F0} nm medium sector at the suggested fare ({outcome.Fare:C2}) should be " +
            $"profitable: revenue {outcome.Revenue:C2}, cost {outcome.TotalCost:C2}, net {outcome.NetProfit:C2}.");

        // "Roughly consistent" with StartupTrajectoryTests' ~£2,612 (post fuel-honesty fix) figure
        // for its own 600 nm/Domestic/Medium-Medium/suggested-fare scenario - not exact, since the
        // seeded AircraftType here (450 kt cruise, 2,400 kg/h burn, RouteTestContext) differs
        // slightly from the A320 figures StartupTrajectoryTests uses, and this route's airports
        // aren't literally Medium-Medium-in-the-same-region. Same order of magnitude confirms the
        // two paths (calculator-only vs real end-to-end ledger postings) agree.
        Assert.InRange(outcome.NetProfit, 1_000m, 6_000m);
    }

    [Fact]
    public async Task MicroSectorExploit_20Nm_RemainsLossMaking()
    {
        // A trivial hop close enough that the fare floor and flat per-sector costs (turnaround fee,
        // minimum crew duty hour) dominate - the exploit the plan's cost model exists to defeat.
        // The fuel-honesty fix must not accidentally make this pay by undercharging fuel here too.
        var outcome = await FlyRealisticSectorAsync(
            arrivalIcao: "EGXX", latitudeDeg: 51.7160, longitudeDeg: -2.7191, sizeCategory: AirportSizeCategory.Medium,
            seedArrivalAirport: true);

        Assert.InRange(outcome.DistanceNm, 15, 30);
        Assert.True(outcome.NetProfit < 0,
            $"A {outcome.DistanceNm:F0} nm micro-hop at the suggested fare ({outcome.Fare:C2}) must remain loss-making " +
            $"(the exploit this cost model defeats), but netted {outcome.NetProfit:C2} " +
            $"(revenue {outcome.Revenue:C2}, cost {outcome.TotalCost:C2}).");
    }

    private sealed record SectorOutcome(
        double DistanceNm, decimal Fare, decimal Revenue, decimal TotalCost, decimal NetProfit,
        decimal CashDelta, decimal FuelCost);

    /// <summary>
    /// Flies one sector end to end through the real player-facing path: seeds a route at the real
    /// great-circle distance and the suggested (reference) fare, starts the flight
    /// (<c>FlightEndpoints.StartAsync</c> posts the real fuel charge), then completes it through
    /// <c>FlightLifecycleService.FinalizeFlightAsync</c> with a synthetic phase machine timed to
    /// this sector's real estimated block time - exactly the real-telemetry completion path, not
    /// the manual-completion one. Returns the actual posted ledger figures.
    /// </summary>
    private static async Task<SectorOutcome> FlyRealisticSectorAsync(
        string arrivalIcao, double latitudeDeg, double longitudeDeg, AirportSizeCategory sizeCategory, bool seedArrivalAirport = false)
    {
        using var ctx = await RouteTestContext.CreateAsync();

        // Domestic, not the RouteTestContext default of LowCost, to match StartupTrajectoryTests'
        // scenario and avoid the fare floor dominating every distance band.
        ctx.Airline.StrategyProfile = AirlineStrategyProfile.Domestic;

        if (seedArrivalAirport)
        {
            ctx.Db.Airports.Add(new Airport
            {
                Icao = arrivalIcao,
                Iata = arrivalIcao[1..],
                Name = $"Test Airport {arrivalIcao}",
                Municipality = "Test",
                Country = "GB",
                Latitude = latitudeDeg,
                Longitude = longitudeDeg,
                ElevationFt = 300,
                SizeCategory = sizeCategory,
                HasScheduledService = true,
                LongestRunwayFt = 8000,
            });
        }

        var economyConfig = EconomyConfig.Default();
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        var distanceNm = GreatCircle.DistanceNm(departure.Latitude, departure.Longitude, latitudeDeg, longitudeDeg);
        // The suggested fare is now the ONE TRUE SOURCE, ReferenceFareCalculator - see
        // RoutePreviewCalculator's doc comment on why the old, separately-hardcoded FareEstimator
        // was removed.
        var fare = ReferenceFareCalculator.Calculate(economyConfig, ctx.Airline.StrategyProfile, distanceNm);

        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = arrivalIcao,
            FlightNumber = "101",
            DistanceNm = distanceNm,
            BaseFare = fare,
            IsActive = true,
            CreatedUtc = Base,
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
            CreatedUtc = Base,
        });

        await ctx.Db.SaveChangesAsync();

        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfig);

        // Snapshot BEFORE StartAsync - it's the one that posts the fuel charge, so a "before"
        // snapshot taken after it would silently exclude fuel from the cash delta below and
        // wrongly disagree with Flight.Revenue/TotalCost (which do include it).
        var cashBefore = await CashBalanceAsync(ctx);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfig, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, ((IStatusCodeHttpResult)startResult).StatusCode);

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();

        // Timed to this sector's own real estimated block time (matching what RoutePreviewCalculator
        // told the player to expect), not a fixed generic duration - so maintenance/crew cost is
        // exactly what a real flight of this length would accrue.
        var blockTime = BlockTimeEstimator.Estimate(distanceNm, ctx.AircraftType.CruiseTasKts);
        var machine = FlightPhaseStateMachine.RestoreFrom(new[]
        {
            PhaseChangeEvent(flight.Id, Base, FlightPhase.Preflight, FlightPhase.TaxiOut),
            PhaseChangeEvent(flight.Id, Base + TimeSpan.FromMinutes(blockTime.TotalMinutes), FlightPhase.TaxiIn, FlightPhase.Shutdown),
        });

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = arrivalIcao,
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = machine,
            LatestSnapshot = new LiveFlightSnapshot(
                flight.Id, FlightPhase.Shutdown.ToString(), latitudeDeg, longitudeDeg,
                AltitudeMslFt: 0, AltitudeAglFt: 0, IndicatedAirspeedKt: 0, GroundSpeedKt: 0, VerticalSpeedFpm: 0,
                FuelRemainingKg: 500, ElapsedBlockMinutes: blockTime.TotalMinutes, PlannedBlockMinutes: flight.PlannedBlockMinutes,
                AwaitingSimReconnect: false, TimestampUtc: Base + TimeSpan.FromMinutes(blockTime.TotalMinutes)),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Completed, updatedFlight.Status);
        Assert.True(updatedFlight.RevenuePosted);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var fuelLine = ledgerLines.Single(t => t.Category == LedgerCategory.Fuel);
        var cashAfter = await CashBalanceAsync(ctx);

        return new SectorOutcome(
            DistanceNm: distanceNm,
            Fare: fare,
            Revenue: updatedFlight.Revenue,
            TotalCost: updatedFlight.TotalCost,
            NetProfit: updatedFlight.Revenue - updatedFlight.TotalCost,
            CashDelta: cashAfter - cashBefore,
            FuelCost: -fuelLine.Amount);
    }

    /// <summary>SQLite can't translate SumAsync over decimal - materialise the rows first.</summary>
    private static async Task<decimal> CashBalanceAsync(RouteTestContext ctx) =>
        (await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync()).Sum(t => t.Amount);

    private static FlightEvent PhaseChangeEvent(Guid flightId, DateTimeOffset utc, FlightPhase from, FlightPhase to) => new()
    {
        Id = Guid.NewGuid(),
        FlightId = flightId,
        Utc = utc,
        Type = FlightEventType.PhaseChange,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PhaseChangePayload(from.ToString(), to.ToString(), false)),
    };

    /// <summary>Same construction FlightLedgerPostingTests uses: a real FlightLifecycleService (and
    /// the SimTelemetryService it needs) whose db-scope resolves a fresh FsOpsDbContext against the
    /// same live in-memory SQLite connection RouteTestContext seeded.</summary>
    private static (FlightLifecycleService Lifecycle, SimTelemetryService Telemetry) CreateLifecycleAndTelemetry(
        RouteTestContext ctx, EconomyConfig economyConfig)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(), telemetry, new NoOpHubContext(),
            economyConfig, NullLogger<FlightLifecycleService>.Instance);
        return (lifecycle, telemetry);
    }
}

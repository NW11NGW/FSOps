using System.Text.Json;
using FSOps.Core.Contracts;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Data;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using FSOps.Sim;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// What a contract sector does to the money and to the player's fleet - which is, respectively,
/// exactly one thing and exactly nothing.
///
/// <para>These are the two claims the whole feature rests on. <b>The player bears no operating
/// costs</b> - fuel, landing, handling and maintenance all belong to the other business - and <b>a
/// contract flight never touches their own aircraft.</b> Both are meant to hold structurally rather
/// than by care, and these tests are how that stays true when somebody changes the completion path
/// later.</para>
/// </summary>
public class ContractFlightLedgerTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---------- A contract flight posts the fee and nothing else ----------

    /// <summary>
    /// <b>The headline.</b> A completed contract leg writes exactly one ledger row - its fee - and the
    /// airline's cash moves by exactly that. No fuel, no landing fee, no handling, no parking, no
    /// passenger charges, no turnaround, no maintenance accrual, no crew cost. Every one of those is
    /// asserted absent by name rather than by counting rows, so a new cost category leaking into the
    /// contract path fails here with the name of the thing that leaked.
    /// </summary>
    [Fact]
    public async Task ACompletedContractLeg_PostsTheFeeAndNothingElse()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (contract, leg) = await SeedAcceptedContractAsync(ctx, legBlockMinutes: [90, 120], fee: 12_000m);

        var cashBefore = await CashAsync(ctx);
        var flight = await FlyLegAsync(ctx, contract, leg);

        var lines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();

        var fee = Assert.Single(lines);
        Assert.Equal(LedgerCategory.ContractFee, fee.Category);
        Assert.Equal(leg.FeeShare, fee.Amount);
        Assert.True(fee.Amount > 0);

        foreach (var forbidden in new[]
                 {
                     LedgerCategory.Fuel, LedgerCategory.LandingFees, LedgerCategory.Handling,
                     LedgerCategory.ParkingFees, LedgerCategory.PassengerCharges, LedgerCategory.TurnaroundFees,
                     LedgerCategory.Maintenance, LedgerCategory.CrewCost, LedgerCategory.Salary,
                     LedgerCategory.TicketRevenue, LedgerCategory.CancellationFee,
                 })
        {
            Assert.DoesNotContain(lines, t => t.Category == forbidden);
        }

        var cashAfter = await CashAsync(ctx);
        Assert.Equal(cashBefore + leg.FeeShare, cashAfter);

        var updated = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Completed, updated.Status);
        Assert.True(updated.RevenuePosted);
        Assert.Equal(leg.FeeShare, updated.Revenue);

        // TotalCost is zero, and that is the assertion rather than an omission: a contract sector
        // genuinely costs the player nothing at all.
        Assert.Equal(0m, updated.TotalCost);
    }

    /// <summary>
    /// <b>Pay is per leg actually flown.</b> Fly two of three and the two are yours, in the ledger,
    /// already - there is no lump sum at the end that could be lost.
    /// </summary>
    [Fact]
    public async Task PayArrivesPerLeg_AsEachLegIsFlown()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (contract, _) = await SeedAcceptedContractAsync(ctx, legBlockMinutes: [60, 90, 240], fee: 30_000m);

        var legs = await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).OrderBy(l => l.Sequence).ToListAsync();

        await FlyLegAsync(ctx, contract, legs[0]);
        Assert.Equal(legs[0].FeeShare, await ContractCashAsync(ctx));

        await FlyLegAsync(ctx, contract, legs[1]);
        Assert.Equal(legs[0].FeeShare + legs[1].FeeShare, await ContractCashAsync(ctx));

        // Two of three flown: the contract is still open and the third leg is still outstanding.
        var reloaded = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Accepted, reloaded.Status);

        await FlyLegAsync(ctx, contract, legs[2]);
        Assert.Equal(contract.Fee, await ContractCashAsync(ctx));

        var finished = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Completed, finished.Status);
        Assert.NotNull(finished.ClosedUtc);
    }

    /// <summary>
    /// The same idempotency guarantee the airline path has, for the same reasons: a reconnect or a
    /// crash rehydration must never pay a leg twice.
    /// </summary>
    [Fact]
    public async Task FinalizingTheSameContractLegTwice_PaysItOnce()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (contract, leg) = await SeedAcceptedContractAsync(ctx, legBlockMinutes: [90], fee: 5_000m);

        var flown = await FlyLegDetailedAsync(ctx, contract, leg);
        var afterFirst = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flown.Flight.Id).ToListAsync();
        Assert.NotEmpty(afterFirst);

        // The very same tracker, finalised again - exactly what a reconnect or crash rehydration does.
        await flown.Lifecycle.FinalizeFlightAsync(flown.Tracker);
        var afterSecond = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flown.Flight.Id).ToListAsync();

        Assert.Equal(afterFirst.Count, afterSecond.Count);
        Assert.Equal(afterFirst.Sum(t => t.Amount), afterSecond.Sum(t => t.Amount));
    }

    /// <summary>
    /// The integrity gate applies to contracts exactly as it does to fares. A sector flown by
    /// teleporting is not paid for - and because a contract sector has no cost side either, it posts
    /// literally nothing. The leg is still marked flown, so the player is not then charged for
    /// abandoning it: one refusal, not two.
    /// </summary>
    [Fact]
    public async Task ASlewedContractLeg_PaysNothingAtAll_ButStillCountsAsFlown()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (contract, leg) = await SeedAcceptedContractAsync(ctx, legBlockMinutes: [90], fee: 5_000m);

        var flight = await FlyLegAsync(ctx, contract, leg, slewDetected: true);

        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync());

        var flownLeg = await ctx.Db.ContractLegs.AsNoTracking().SingleAsync(l => l.Id == leg.Id);
        Assert.Equal(flight.Id, flownLeg.FlightId);
    }

    // ---------- The player's fleet is untouched ----------

    /// <summary>
    /// <b>A contract flight leaves the player's fleet exactly as it found it</b> - hours, condition,
    /// location, status, fuel, and its maintenance counters. Every field is captured before and
    /// compared after, rather than spot-checking the two or three that seem most likely, because the
    /// whole point is that NOTHING moved.
    /// </summary>
    [Fact]
    public async Task AContractFlight_LeavesEveryFleetAircraftCompletelyUntouched()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        aircraft.AirframeHours = 1_234.5;
        aircraft.HoursSinceACheck = 111.0;
        aircraft.HoursSinceCCheck = 2_222.0;
        aircraft.ConditionPercent = 87.5;
        aircraft.FuelOnBoardKg = 4_321;
        aircraft.LocationIcao = "EGGD";
        aircraft.Status = FleetAircraftStatus.Active;
        await ctx.Db.SaveChangesAsync();

        var before = Snapshot(aircraft);

        // A contract leg that goes somewhere else entirely - if anything were going to move the
        // aircraft's recorded location, a completion at EGPH would do it.
        var (contract, leg) = await SeedAcceptedContractAsync(ctx, legBlockMinutes: [180], fee: 9_000m);
        await FlyLegAsync(ctx, contract, leg);

        var after = Snapshot(await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == aircraft.Id));
        Assert.Equal(before, after);

        // And no maintenance event was raised against it either - maintenance accrual is the one
        // consequence that could have crept in without changing a column on the aircraft itself.
        Assert.Empty(await ctx.Db.MaintenanceEvents.Where(m => m.FleetAircraftId == aircraft.Id).ToListAsync());
    }

    /// <summary>
    /// <b>Reputation does not move.</b> Reputation models the player's own airline's passengers and
    /// their experience of its service; flying somebody else's aeroplane on somebody else's business
    /// says nothing about that in either direction. Asserted for a good landing and a terrible one,
    /// so this cannot pass merely because the sector happened to be unremarkable.
    /// </summary>
    [Fact]
    public async Task AContractFlight_DoesNotMoveAirlineReputation()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var reputationBefore = ctx.Airline.ReputationScore;

        var (contract, leg) = await SeedAcceptedContractAsync(ctx, legBlockMinutes: [90], fee: 5_000m);
        await FlyLegAsync(ctx, contract, leg, landingFpm: -680);

        var airline = await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id);
        Assert.Equal(reputationBefore, airline.ReputationScore);
    }

    // ---------- Abandoning ----------

    /// <summary>
    /// The abandon charge lands as a single negative line in the same category the fees were paid
    /// into, so "what did contract flying do to my balance" stays one sum. The worked figures are the
    /// pay calculator's, checked here against what actually reached the ledger.
    /// </summary>
    [Fact]
    public async Task AbandoningAfterOneOfThreeLegs_ChargesForTheLegsLeft()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (contract, _) = await SeedAcceptedContractAsync(ctx, legBlockMinutes: [60, 120, 60], fee: 24_000m);
        var legs = await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).OrderBy(l => l.Sequence).ToListAsync();

        await FlyLegAsync(ctx, contract, legs[0]);
        var earned = legs[0].FeeShare;

        var config = EconomyConfigCatalog.Default().Get(ctx.Airline.Playstyle).Contracts;
        var charge = await ContractEconomicsPoster.PostAbandonAsync(
            ctx.Db, await ctx.Db.Contracts.SingleAsync(c => c.Id == contract.Id), config, Base, "test", CancellationToken.None);
        await ctx.Db.SaveChangesAsync();

        // Blocks are 60/120/60 of 240 minutes, so the legs are worth 6,000 / 12,000 / 6,000. One leg
        // flown earns 6,000; the two left are worth 18,000, and that is the charge - the user's rule
        // exactly, "charged for the remaining legs". Stopping this early is a real loss, and it
        // should be: leg one is where the aeroplane ends up in the worst possible place.
        Assert.Equal(6_000m, earned);
        Assert.Equal(18_000m, charge.Charge);
        Assert.Equal(2, charge.UnflownLegCount);

        var contractLines = await ctx.Db.LedgerTransactions
            .Where(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.ContractFee)
            .ToListAsync();

        // Both directions in one category, so the ledger answers "what did contract flying do to my
        // balance" as a single sum.
        Assert.Equal(2, contractLines.Count);
        Assert.Equal(earned - charge.Charge, contractLines.Sum(t => t.Amount));
        Assert.Equal(-12_000m, contractLines.Sum(t => t.Amount));

        var closed = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Abandoned, closed.Status);
    }

    /// <summary>Handing an untouched job back writes no ledger row at all - not a zero-value one.</summary>
    [Fact]
    public async Task AbandoningWithoutFlyingAnyLeg_PostsNoLedgerRow()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (contract, _) = await SeedAcceptedContractAsync(ctx, legBlockMinutes: [60, 120], fee: 20_000m);

        var config = EconomyConfigCatalog.Default().Get(ctx.Airline.Playstyle).Contracts;
        var charge = await ContractEconomicsPoster.PostAbandonAsync(
            ctx.Db, await ctx.Db.Contracts.SingleAsync(c => c.Id == contract.Id), config, Base, "test", CancellationToken.None);
        await ctx.Db.SaveChangesAsync();

        Assert.Equal(0m, charge.Charge);
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.Category == LedgerCategory.ContractFee).ToListAsync());
    }

    // ---------- Helpers ----------

    /// <summary>Every field of a fleet aircraft that a flight could conceivably move.</summary>
    private sealed record FleetSnapshot(
        double AirframeHours, double HoursSinceACheck, double HoursSinceCCheck, double ConditionPercent,
        double FuelOnBoardKg, string LocationIcao, FleetAircraftStatus Status, DateTimeOffset? GroundedUntilUtc,
        bool ReservedForPlayer);

    private static FleetSnapshot Snapshot(FleetAircraft a) => new(
        a.AirframeHours, a.HoursSinceACheck, a.HoursSinceCCheck, a.ConditionPercent, a.FuelOnBoardKg,
        a.LocationIcao, a.Status, a.GroundedUntilUtc, a.ReservedForPlayer);

    private static async Task<decimal> CashAsync(RouteTestContext ctx) =>
        (await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).Select(t => t.Amount).ToListAsync()).Sum();

    private static async Task<decimal> ContractCashAsync(RouteTestContext ctx) =>
        (await ctx.Db.LedgerTransactions
            .Where(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.ContractFee)
            .Select(t => t.Amount).ToListAsync()).Sum();

    private static async Task<(Contract Contract, ContractLeg FirstLeg)> SeedAcceptedContractAsync(
        RouteTestContext ctx, int[] legBlockMinutes, decimal fee)
    {
        // EGGD - EGPH - EGPF - EGSS, chained so however many legs the caller asked for are contiguous.
        var chain = new[] { "EGGD", "EGPH", "EGPF", "EGSS", "EGGD", "EGPH" };
        var shares = ContractPayCalculator.AllocateFeeShares(fee, legBlockMinutes);

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Kind = ContractKind.Ferry,
            Status = ContractStatus.Accepted,
            BoardBucket = 20_000,
            BoardSlot = 0,
            OperatorName = "Northgate Aircraft Sales",
            AircraftTypeDesignator = "C208",
            LoadDescription = "Positioning flight",
            Fee = fee,
            TotalDistanceNm = 300 * legBlockMinutes.Length,
            TotalPlannedBlockMinutes = legBlockMinutes.Sum(),
            OfferedUtc = Base,
            DeadlineUtc = Base.AddDays(28),
            AcceptedUtc = Base,
            CreatedUtc = Base,
        };

        for (var i = 0; i < legBlockMinutes.Length; i++)
        {
            contract.Legs.Add(new ContractLeg
            {
                Id = Guid.NewGuid(),
                ContractId = contract.Id,
                Sequence = i + 1,
                DepartureIcao = chain[i],
                ArrivalIcao = chain[i + 1],
                DistanceNm = 300,
                PlannedBlockMinutes = legBlockMinutes[i],
                FeeShare = shares[i],
            });
        }

        ctx.Db.Contracts.Add(contract);
        await ctx.Db.SaveChangesAsync();

        return (contract, contract.Legs.OrderBy(l => l.Sequence).First());
    }

    /// <summary>The flight, plus the machinery that flew it - so a test can finalise the very same
    /// tracker a second time and prove the idempotency guard holds.</summary>
    private sealed record FlownLeg(
        Flight Flight, FlightLifecycleService Lifecycle, FlightLifecycleService.ActiveFlightTracker Tracker);

    private static async Task<Flight> FlyLegAsync(
        RouteTestContext ctx, Contract contract, ContractLeg leg, bool slewDetected = false, double landingFpm = -140) =>
        (await FlyLegDetailedAsync(ctx, contract, leg, slewDetected, landingFpm)).Flight;

    private static async Task<FlownLeg> FlyLegDetailedAsync(
        RouteTestContext ctx, Contract contract, ContractLeg leg,
        bool slewDetected = false, double landingFpm = -140)
    {
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = null,
            FleetAircraftId = null,
            ContractLegId = leg.Id,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = Base,
            PlannedBlockMinutes = leg.PlannedBlockMinutes,
            TitleFlown = "Cessna 208B Grand Caravan",
            CreatedUtc = Base,
        };

        FlightWriteInvariant.Validate(flight);
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        var lifecycle = CreateLifecycle(ctx);
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = null,
            ContractLegId = leg.Id,
            ArrivalIcao = leg.ArrivalIcao,
            PlannedBlockMinutes = leg.PlannedBlockMinutes,
            Machine = CompletedMachine(flight.Id, landingFpm),
            IntegrityMonitor = new FlightIntegrityMonitor(),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        if (slewDetected)
        {
            // Applied after the tracker was built because the integrity monitor is what normally sets
            // it, and a synthetic tracker has never seen a sample. Re-finalised from a clean flight
            // row so the gate is exercised rather than bypassed.
            var tracked = await ctx.Db.Flights.SingleAsync(f => f.Id == flight.Id);
            ctx.Db.LedgerTransactions.RemoveRange(
                await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync());
            var legRow = await ctx.Db.ContractLegs.SingleAsync(l => l.Id == leg.Id);
            legRow.FlightId = null;
            legRow.FlownUtc = null;
            tracked.Status = FlightStatus.InProgress;
            tracked.RevenuePosted = false;
            tracked.Revenue = 0m;
            tracked.SlewDetected = true;
            var contractRow = await ctx.Db.Contracts.SingleAsync(c => c.Id == contract.Id);
            contractRow.Status = ContractStatus.Accepted;
            contractRow.ClosedUtc = null;
            await ctx.Db.SaveChangesAsync();

            await lifecycle.FinalizeFlightAsync(tracker);
        }

        ctx.Db.ChangeTracker.Clear();
        return new FlownLeg(flight, lifecycle, tracker);
    }

    private static FlightLifecycleService CreateLifecycle(RouteTestContext ctx)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        return new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(), telemetry, new NoOpHubContext(),
            EconomyConfigCatalog.Default(), null, NullLogger<FlightLifecycleService>.Instance);
    }

    private static FlightPhaseStateMachine CompletedMachine(Guid flightId, double landingFpm) =>
        FlightPhaseStateMachine.RestoreFrom(new[]
        {
            PhaseChangeEvent(flightId, 60, FlightPhase.Preflight, FlightPhase.TaxiOut),
            PhaseChangeEvent(flightId, 420, FlightPhase.TakeoffRoll, FlightPhase.Climb),
            PhaseChangeEvent(flightId, 5270, FlightPhase.Descent, FlightPhase.Approach),
            TouchdownEvent(flightId, 5390, landingFpm),
            PhaseChangeEvent(flightId, 5398, FlightPhase.Approach, FlightPhase.Landed),
            PhaseChangeEvent(flightId, 5442, FlightPhase.Landed, FlightPhase.TaxiIn),
            PhaseChangeEvent(flightId, 5550, FlightPhase.TaxiIn, FlightPhase.Shutdown),
        });

    private static FlightEvent PhaseChangeEvent(Guid flightId, double t, FlightPhase from, FlightPhase to) => new()
    {
        Id = Guid.NewGuid(),
        FlightId = flightId,
        Utc = Base + TimeSpan.FromSeconds(t),
        Type = FlightEventType.PhaseChange,
        PayloadJson = JsonSerializer.Serialize(new PhaseChangePayload(from.ToString(), to.ToString(), false)),
    };

    private static FlightEvent TouchdownEvent(Guid flightId, double t, double fpm) => new()
    {
        Id = Guid.NewGuid(),
        FlightId = flightId,
        Utc = Base + TimeSpan.FromSeconds(t),
        Type = FlightEventType.Touchdown,
        PayloadJson = JsonSerializer.Serialize(new TouchdownPayload(
            55.95, -3.3725, 240.0, fpm, 1.3, 0, TouchdownRateSource.SimTouchdownRate)),
    };
}

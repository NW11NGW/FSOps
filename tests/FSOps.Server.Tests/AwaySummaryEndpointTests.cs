using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// "While you were away" - the app must explain catch-up: on startup after a real
/// gap, the player should be told what happened (charges by category, what virtual pilots flew and
/// earned, maintenance that fell due, anything skipped/cancelled/suspended and why) rather than
/// opening to a changed balance with no explanation. Drives MaintenanceEndpoints' away-summary
/// handlers directly against an isolated in-memory RouteTestContext with a FakeClock.
/// </summary>
public class AwaySummaryEndpointTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static T ValueOf<T>(IResult result) => (T)Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;

    [Fact]
    public async Task FirstEverCall_Bootstraps_AndReportsNothing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedEconomyStateAsync(ctx, awaySummaryLastViewedUtc: null);
        var clock = new FakeClock(Base);

        var result = await MaintenanceEndpoints.AwaySummaryAsync(ctx.Db, ctx.CurrentUser, clock, new StartupReconciliationState(), CancellationToken.None);
        var summary = ValueOf<AwaySummaryResponse>(result);

        Assert.False(summary.HasSummary);

        var state = await ctx.Db.EconomyStates.AsNoTracking().SingleAsync();
        Assert.Equal(Base, state.AwaySummaryLastViewedUtc);
    }

    [Fact]
    public async Task ShortGap_BelowThreshold_ReportsNothing_EvenWithNewLedgerActivity()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);
        await AddLedgerLineAsync(ctx, lastViewed.AddMinutes(1), LedgerCategory.Fuel, -100m);

        // Only 2 minutes since last viewed - below the 5-minute "away" threshold, so this reads as
        // a normal reload, not a genuine gap.
        var clock = new FakeClock(lastViewed.AddMinutes(2));

        var result = await MaintenanceEndpoints.AwaySummaryAsync(ctx.Db, ctx.CurrentUser, clock, new StartupReconciliationState(), CancellationToken.None);
        var summary = ValueOf<AwaySummaryResponse>(result);

        Assert.False(summary.HasSummary);
    }

    [Fact]
    public async Task LongGap_WithChargesAndVirtualPilotActivity_ReportsChargesByCategoryAndPilotNet()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);

        var virtualPilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = "First Officer Test",
            IsPlayer = false,
            MonthlySalary = 9000m,
            SkillRating = 50,
            CreatedUtc = Base,
        };
        ctx.Db.Pilots.Add(virtualPilot);

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
            CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);
        await ctx.Db.SaveChangesAsync();

        var withinWindow = lastViewed.AddDays(3);

        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = (await ctx.Db.FleetAircraft.FirstAsync()).Id,
            PilotId = virtualPilot.Id,
            Status = FlightStatus.Completed,
            PlannedDepartureUtc = withinWindow,
            PlannedBlockMinutes = 90,
            Revenue = 5000m,
            TotalCost = 2000m,
            RevenuePosted = true,
            CreatedUtc = withinWindow,
        };
        ctx.Db.Flights.Add(flight);

        await AddLedgerLineAsync(ctx, withinWindow, LedgerCategory.LeasePayment, -50000m);
        await AddLedgerLineAsync(ctx, withinWindow, LedgerCategory.Salary, -9000m);
        await AddLedgerLineAsync(ctx, withinWindow, LedgerCategory.TicketRevenue, 5000m);
        // Outside the window (before lastViewed) - must never be counted.
        await AddLedgerLineAsync(ctx, lastViewed.AddDays(-1), LedgerCategory.Insurance, -1000m);

        var now = lastViewed.AddDays(7);
        var clock = new FakeClock(now);

        var result = await MaintenanceEndpoints.AwaySummaryAsync(ctx.Db, ctx.CurrentUser, clock, new StartupReconciliationState(), CancellationToken.None);
        var summary = ValueOf<AwaySummaryResponse>(result);

        Assert.True(summary.HasSummary);
        Assert.Equal(lastViewed, summary.SinceUtc);
        Assert.Equal(now, summary.UntilUtc);

        // Only the three in-window lines - the pre-window Insurance line must be excluded.
        Assert.Equal(3, summary.ChargesByCategory.Sum(c => c.Count));
        Assert.DoesNotContain(summary.ChargesByCategory, c => c.Category == "Insurance");
        var lease = Assert.Single(summary.ChargesByCategory, c => c.Category == "LeasePayment");
        Assert.Equal(-50000m, lease.Amount);

        Assert.Equal(1, summary.VirtualPilots.SectorsFlown);
        Assert.Equal(5000m, summary.VirtualPilots.Revenue);
        Assert.Equal(2000m, summary.VirtualPilots.Cost);
        Assert.Equal(3000m, summary.VirtualPilots.Net);

        // Cash balance is the full ledger, including the pre-window line - never recomputed from
        // the window alone.
        Assert.Equal(5000m - 50000m - 9000m - 1000m, summary.CashBalance);
    }

    [Fact]
    public async Task LongGap_WithMaintenanceAndSuspendedOccurrence_ReportsBoth()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var withinWindow = lastViewed.AddDays(3);

        ctx.Db.MaintenanceEvents.Add(new MaintenanceEvent
        {
            Id = Guid.NewGuid(),
            FleetAircraftId = fleetAircraft.Id,
            Type = MaintenanceEventType.CCheck,
            StartUtc = withinWindow,
            EndUtc = withinWindow.AddDays(14),
            Cost = 320_000m,
            Notes = "CCheck triggered",
            CreatedUtc = withinWindow,
        });

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
            CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);

        ctx.Db.Flights.Add(new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.Suspended,
            PlannedDepartureUtc = withinWindow,
            PlannedBlockMinutes = 90,
            RevenuePosted = true,
            UnflyableReason = "Schedule suspended - G-TEST is in maintenance until later.",
            CreatedUtc = withinWindow,
        });

        await ctx.Db.SaveChangesAsync();

        var now = lastViewed.AddDays(7);
        var clock = new FakeClock(now);

        var result = await MaintenanceEndpoints.AwaySummaryAsync(ctx.Db, ctx.CurrentUser, clock, new StartupReconciliationState(), CancellationToken.None);
        var summary = ValueOf<AwaySummaryResponse>(result);

        Assert.True(summary.HasSummary);
        var maintenance = Assert.Single(summary.MaintenanceEvents);
        Assert.Equal("CCheck", maintenance.Type);
        Assert.Equal("G-TEST", maintenance.Registration);

        var unresolved = Assert.Single(summary.UnresolvedOccurrences);
        Assert.Equal("Suspended", unresolved.Status);
        Assert.NotNull(unresolved.Reason);
    }

    [Fact]
    public async Task Acknowledge_MovesTheCursorForward_SoTheNextCallReportsNothingUntilANewGap()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);
        await AddLedgerLineAsync(ctx, lastViewed.AddDays(1), LedgerCategory.Fuel, -500m);

        var ackTime = lastViewed.AddDays(2);
        var ackResult = await MaintenanceEndpoints.AcknowledgeAwaySummaryAsync(ctx.Db, ctx.CurrentUser, new FakeClock(ackTime), new StartupReconciliationState(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status204NoContent, Assert.IsAssignableFrom<IStatusCodeHttpResult>(ackResult).StatusCode);

        var state = await ctx.Db.EconomyStates.AsNoTracking().SingleAsync();
        Assert.Equal(ackTime, state.AwaySummaryLastViewedUtc);

        // Immediately after acknowledging: no new activity since ackTime, so nothing to report.
        var result = await MaintenanceEndpoints.AwaySummaryAsync(ctx.Db, ctx.CurrentUser, new FakeClock(ackTime.AddMinutes(1)), new StartupReconciliationState(), CancellationToken.None);
        Assert.False(ValueOf<AwaySummaryResponse>(result).HasSummary);
    }

    private static async Task SeedEconomyStateAsync(RouteTestContext ctx, DateTimeOffset? awaySummaryLastViewedUtc)
    {
        ctx.Db.EconomyStates.Add(new EconomyState
        {
            Id = Guid.NewGuid(),
            LastProcessedUtc = Base,
            LastScheduleResolvedUtc = Base,
            AwaySummaryLastViewedUtc = awaySummaryLastViewedUtc,
            WorldSeed = 1,
            FuelPricePerKg = 0m,
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task AddLedgerLineAsync(RouteTestContext ctx, DateTimeOffset utc, LedgerCategory category, decimal amount, string description = "Test line")
    {
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = utc,
            Category = category,
            Amount = amount,
            Description = description,
        });
        await ctx.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task LongGap_WithLeaseDepositAndOrdinaryLeasePayment_ReportsThemUnderSeparateCategories()
    {
        // A lease deposit (paid when adding an aircraft - AirlineEndpoints.CreateAsync/
        // FleetEndpoints.LeaseAsync) is posted under the same LedgerCategory.LeasePayment as the
        // ordinary recurring monthly charge EconomyClockService bills. Lumped together in the
        // away-summary's "Charges by category" breakdown, a deposit paid because the player added an
        // aircraft would misleadingly read as a lease payment charged while they were away - a
        // reporting-window attribution bug, not a double-charge (cash itself was always correct).
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);

        var withinWindow = lastViewed.AddDays(3);
        await AddLedgerLineAsync(ctx, withinWindow, LedgerCategory.LeasePayment, -5000m, "Lease deposit (1 months): Test Type (G-TEST)");
        await AddLedgerLineAsync(ctx, withinWindow, LedgerCategory.LeasePayment, -1200m, "Monthly lease: G-TEST");

        var now = lastViewed.AddDays(7);
        var clock = new FakeClock(now);

        var result = await MaintenanceEndpoints.AwaySummaryAsync(ctx.Db, ctx.CurrentUser, clock, new StartupReconciliationState(), CancellationToken.None);
        var summary = ValueOf<AwaySummaryResponse>(result);

        Assert.True(summary.HasSummary);

        var deposit = Assert.Single(summary.ChargesByCategory, c => c.Category == "LeaseDeposit");
        Assert.Equal(-5000m, deposit.Amount);
        Assert.Equal(1, deposit.Count);

        var ordinaryPayment = Assert.Single(summary.ChargesByCategory, c => c.Category == "LeasePayment");
        Assert.Equal(-1200m, ordinaryPayment.Amount);
        Assert.Equal(1, ordinaryPayment.Count);
    }

    /// <summary>
    /// The case the requirement is built around: a failed catch-up pass means real money movements
    /// may be missing, and the player was previously told nothing at all about it. This has to
    /// surface even well inside the normal 5-minute "away" threshold and even with zero ledger
    /// activity - waiting for a genuine away-gap before mentioning a startup failure would be too
    /// late for the player who reopens the tab a few seconds after boot.
    /// </summary>
    [Fact]
    public async Task CatchUpFailure_SurfacesImmediately_EvenWithNoAwayGapOrLedgerActivity()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);

        var startupState = new StartupReconciliationState();
        var failedAt = lastViewed.AddSeconds(1);
        startupState.RecordCatchUpFailure(nameof(EconomyClockService), new InvalidOperationException("database unreadable"), failedAt);

        // Only 2 seconds since last viewed - nowhere near the 5-minute away threshold, and no
        // ledger/flight/maintenance activity either. Without the startup finding this would be
        // AwaySummaryResponse.None.
        var result = await MaintenanceEndpoints.AwaySummaryAsync(
            ctx.Db, ctx.CurrentUser, new FakeClock(lastViewed.AddSeconds(2)), startupState, CancellationToken.None);
        var summary = ValueOf<AwaySummaryResponse>(result);

        Assert.True(summary.HasSummary);
        var failure = Assert.Single(summary.CatchUpFailures);
        Assert.Equal(nameof(EconomyClockService), failure.Service);
        Assert.Contains("database unreadable", failure.Message);
        Assert.Equal(failedAt, failure.OccurredUtc);
        Assert.Null(summary.ReservationChanges);
    }

    [Fact]
    public async Task CatchUpFailure_ClearsAfterAcknowledge_ButANewOneAfterThatStillSurfaces()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);

        var startupState = new StartupReconciliationState();
        startupState.RecordCatchUpFailure(nameof(EconomyClockService), new InvalidOperationException("first failure"), lastViewed);

        var ackTime = lastViewed.AddSeconds(5);
        await MaintenanceEndpoints.AcknowledgeAwaySummaryAsync(ctx.Db, ctx.CurrentUser, new FakeClock(ackTime), startupState, CancellationToken.None);

        // Same startupState instance (mirrors it being a DI singleton in production) - the
        // already-acknowledged failure must not reappear.
        var afterAck = ValueOf<AwaySummaryResponse>(await MaintenanceEndpoints.AwaySummaryAsync(
            ctx.Db, ctx.CurrentUser, new FakeClock(ackTime.AddSeconds(1)), startupState, CancellationToken.None));
        Assert.False(afterAck.HasSummary);

        // A genuinely new failure after acknowledging must still be reported - acknowledging must
        // not have silenced the channel permanently, only what existed at that moment.
        startupState.RecordCatchUpFailure(nameof(VirtualFlightResolverService), new InvalidOperationException("second failure"), ackTime.AddSeconds(2));
        var afterNewFailure = ValueOf<AwaySummaryResponse>(await MaintenanceEndpoints.AwaySummaryAsync(
            ctx.Db, ctx.CurrentUser, new FakeClock(ackTime.AddSeconds(3)), startupState, CancellationToken.None));

        Assert.True(afterNewFailure.HasSummary);
        var failure = Assert.Single(afterNewFailure.CatchUpFailures);
        Assert.Equal(nameof(VirtualFlightResolverService), failure.Service);
        Assert.Contains("second failure", failure.Message);
    }

    [Fact]
    public async Task ReservationReconciliation_SurfacesOnlyForThisAirline_AndOnlyUntilAcknowledged()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);

        var otherAirlineId = Guid.NewGuid();
        var thisAirlineAircraftId = (await ctx.Db.FleetAircraft.FirstAsync()).Id;

        var startupState = new StartupReconciliationState
        {
            Result = new ReservationReconciliationResult(
                Released:
                [
                    new ReleasedAircraft(thisAirlineAircraftId, "G-TEST", ctx.Airline.Id, ScheduledLegCount: 3),
                    new ReleasedAircraft(Guid.NewGuid(), "G-OTHER", otherAirlineId, ScheduledLegCount: 1),
                ],
                FallbackReserved: [],
                AirlinesLeftWithNoReservedAircraft: []),
        };

        var result = ValueOf<AwaySummaryResponse>(await MaintenanceEndpoints.AwaySummaryAsync(
            ctx.Db, ctx.CurrentUser, new FakeClock(lastViewed.AddSeconds(1)), startupState, CancellationToken.None));

        Assert.True(result.HasSummary);
        Assert.NotNull(result.ReservationChanges);
        var released = Assert.Single(result.ReservationChanges!.Released);
        Assert.Equal("G-TEST", released.Registration);
        Assert.Equal(3, released.ScheduledLegCount);
        Assert.Empty(result.CatchUpFailures);

        await MaintenanceEndpoints.AcknowledgeAwaySummaryAsync(ctx.Db, ctx.CurrentUser, new FakeClock(lastViewed.AddSeconds(2)), startupState, CancellationToken.None);

        var afterAck = ValueOf<AwaySummaryResponse>(await MaintenanceEndpoints.AwaySummaryAsync(
            ctx.Db, ctx.CurrentUser, new FakeClock(lastViewed.AddSeconds(3)), startupState, CancellationToken.None));
        Assert.False(afterAck.HasSummary);
    }

    /// <summary>
    /// The requirement's own negative case: a reconciliation pass that changed nothing (the common
    /// case - most databases have no contradiction to fix) and no catch-up failures must show
    /// nothing at all, never an empty dialog. Otherwise every launch would surface something to
    /// dismiss and the player would learn to stop reading it.
    /// </summary>
    [Fact]
    public async Task ReconciliationWithNoFindings_AndNoCatchUpFailures_ShowsNothing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var lastViewed = Base;
        await SeedEconomyStateAsync(ctx, lastViewed);

        var startupState = new StartupReconciliationState { Result = ReservationReconciliationResult.Empty };

        var result = ValueOf<AwaySummaryResponse>(await MaintenanceEndpoints.AwaySummaryAsync(
            ctx.Db, ctx.CurrentUser, new FakeClock(lastViewed.AddSeconds(1)), startupState, CancellationToken.None));

        Assert.False(result.HasSummary);
    }
}

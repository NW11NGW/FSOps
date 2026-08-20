using System.Globalization;
using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Covers GET /stats/trends - the airline's direction of travel over time.
///
/// The theme running through these tests is that every series must be traceable to rows that
/// already exist, and that a day with nothing to say must be null rather than zero. The cash series
/// in particular has to survive the case that broke the naive version: a window that starts long
/// after the airline did, where ignoring earlier ledger rows would draw the balance starting from
/// zero and make a healthy airline look like it had just been founded.
/// </summary>
public class StatsTrendsEndpointTests
{
    private static T OkValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record TrendsProbe(int PeriodDays, List<TrendPointProbe> Points, double? CurrentReputation, int ReputationRecordedDays);

    private sealed record TrendPointProbe(
        string DateUtc, decimal CashBalance, int SectorsFlown, double? OnTimePercent, double? LoadFactorPercent,
        double? Reputation, double? ReputationPressure);

    private static string DayKey(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// A day to seed a flight on: <paramref name="daysAgo"/> days back, <b>pinned to 06:00 UTC</b>
    /// rather than to whatever time of day the test happens to run at.
    ///
    /// <para><b>Why this exists.</b> These tests bucket a flight by its <c>InUtc</c>, which is the
    /// planned departure plus block time plus any delay - up to five and a half hours after the
    /// moment they seed. Seeding at <c>DateTimeOffset.UtcNow</c> therefore pushed that arrival past
    /// midnight, into the <i>next</i> UTC day, whenever the suite ran after roughly 18:30 UTC. The
    /// day the test staged and the day the endpoint bucketed into were then different days, and
    /// <c>OnTimeAgreesWithThePerformanceEndpointForTheSameDay</c> failed every evening while passing
    /// all morning.</para>
    ///
    /// <para>06:00 leaves the whole working day of headroom before midnight, so the arrival always
    /// lands on the day the test intended, whatever time it is run. Nothing about what these tests
    /// <i>claim</i> changes - this only fixes how they stage the conditions, the same distinction the
    /// pinned-schema conversions drew (see <see cref="PinnedSchemaRead"/>).</para>
    ///
    /// <para>Applied to <b>every</b> flight-seeding site in this file, not only the one that was
    /// failing. Two of the others straddle midnight in a one-minute window rather than a five-hour
    /// one, so they would have gone on passing almost always - and a fix applied only to the instance
    /// that happens to be red is a fix that schedules its own recurrence. Ledger-based tests are left
    /// alone: a ledger row is bucketed by its own timestamp, with nothing added to it, so they were
    /// never exposed to this.</para>
    /// </summary>
    private static DateTimeOffset FlightDayUtc(int daysAgo)
    {
        var date = DateTimeOffset.UtcNow.AddDays(-daysAgo).UtcDateTime.Date;
        return new DateTimeOffset(date.AddHours(6), TimeSpan.Zero);
    }

    private static void AddLedger(RouteTestContext ctx, DateTimeOffset utc, decimal amount, LedgerCategory category = LedgerCategory.TicketRevenue)
    {
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = utc,
            Category = category,
            Amount = amount,
            Description = "Test line",
        });
    }

    private static Route SeedRoute(RouteTestContext ctx)
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
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-90),
        };
        ctx.Db.Routes.Add(route);
        return route;
    }

    private static Pilot SeedPilot(RouteTestContext ctx)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = "Player",
            IsPlayer = true,
            MonthlySalary = 9000m,
            SkillRating = 50,
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-90),
        };
        ctx.Db.Pilots.Add(pilot);
        return pilot;
    }

    private static void SeedCompletedFlight(
        RouteTestContext ctx, Route route, Guid fleetAircraftId, Guid pilotId, DateTimeOffset plannedDepartureUtc,
        int plannedBlockMinutes, double arrivalDelayMinutes, int paxFlown, double? landingFpm = null, bool simRateElevated = false)
    {
        ctx.Db.Flights.Add(new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraftId,
            PilotId = pilotId,
            Status = FlightStatus.Completed,
            PlannedDepartureUtc = plannedDepartureUtc,
            PlannedBlockMinutes = plannedBlockMinutes,
            OutUtc = plannedDepartureUtc,
            InUtc = plannedDepartureUtc.AddMinutes(plannedBlockMinutes + arrivalDelayMinutes),
            PaxBooked = paxFlown,
            PaxFlown = paxFlown,
            RevenuePosted = true,
            SimRateElevated = simRateElevated,
            LandingFpmFirst = landingFpm,
            TitleFlown = "Test Aircraft",
            CreatedUtc = plannedDepartureUtc,
        });
    }

    // ---------- Cash ----------

    [Fact]
    public async Task Cash_CarriesAnOpeningBalanceFromBeforeTheWindow()
    {
        // The regression this exists for: an airline founded months ago, viewed over a 7-day window.
        // Summing only the transactions inside the window would start the line at zero and show a
        // solvent airline as though it had just been created.
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        AddLedger(ctx, now.AddDays(-60), 500_000m, LedgerCategory.StartingCapital);
        AddLedger(ctx, now.AddDays(-1), -20_000m, LedgerCategory.Fuel);
        await ctx.Db.SaveChangesAsync();

        var result = await StatsEndpoints.TrendsAsync(7, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var body = OkValueOf<TrendsProbe>(result);

        Assert.Equal(7, body.Points.Count);
        // Every day before the fuel line still carries the full opening balance.
        Assert.Equal(500_000m, body.Points[0].CashBalance);
        // The last two days sit after the charge.
        Assert.Equal(480_000m, body.Points[^1].CashBalance);
    }

    [Fact]
    public async Task Cash_IsPresentOnEveryDayInTheWindow_EvenOnesWithNoActivity()
    {
        // Cash does not stop existing on a quiet day. Unlike on-time performance, an absent point
        // here would be wrong rather than honest.
        using var ctx = await RouteTestContext.CreateAsync();
        AddLedger(ctx, DateTimeOffset.UtcNow.AddDays(-30), 100_000m, LedgerCategory.StartingCapital);
        await ctx.Db.SaveChangesAsync();

        var result = await StatsEndpoints.TrendsAsync(14, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var body = OkValueOf<TrendsProbe>(result);

        Assert.Equal(14, body.Points.Count);
        Assert.All(body.Points, point => Assert.Equal(100_000m, point.CashBalance));
    }

    [Fact]
    public async Task Cash_MatchesTheAppsOwnDefinitionOfTheBalance_OnTheFinalDay()
    {
        // The app defines cash as SUM(LedgerTransaction.Amount). The last point of the series is
        // therefore required to equal that sum exactly, or the chart and the Finances page disagree.
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        AddLedger(ctx, now.AddDays(-40), 250_000m, LedgerCategory.StartingCapital);
        AddLedger(ctx, now.AddDays(-5), 18_400m);
        AddLedger(ctx, now.AddDays(-3), -6_250m, LedgerCategory.Fuel);
        AddLedger(ctx, now.AddHours(-2), -1_000m, LedgerCategory.Handling);
        await ctx.Db.SaveChangesAsync();

        var expected = (await ctx.Db.LedgerTransactions.Select(t => t.Amount).ToListAsync()).Sum();

        var result = await StatsEndpoints.TrendsAsync(30, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var body = OkValueOf<TrendsProbe>(result);

        Assert.Equal(expected, body.Points[^1].CashBalance);
    }

    // ---------- On-time / load factor ----------

    [Fact]
    public async Task OnTimeAndLoadFactor_AreNullOnDaysNothingFlew_NeverZero()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flightDay = FlightDayUtc(2);

        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, flightDay, plannedBlockMinutes: 90, arrivalDelayMinutes: 1, paxFlown: 90);
        await ctx.Db.SaveChangesAsync();

        var result = await StatsEndpoints.TrendsAsync(7, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var body = OkValueOf<TrendsProbe>(result);

        var flown = body.Points.Single(p => p.DateUtc == DayKey(flightDay.AddMinutes(91)));
        Assert.Equal(1, flown.SectorsFlown);
        Assert.Equal(100.0, flown.OnTimePercent);
        Assert.Equal(50.0, flown.LoadFactorPercent);

        foreach (var quiet in body.Points.Where(p => p.DateUtc != flown.DateUtc))
        {
            Assert.Equal(0, quiet.SectorsFlown);
            Assert.Null(quiet.OnTimePercent);
            Assert.Null(quiet.LoadFactorPercent);
        }
    }

    [Fact]
    public async Task OnTimeAgreesWithThePerformanceEndpointForTheSameDay()
    {
        // Both endpoints run through the same shared helper. This test is the guard that keeps them
        // that way: the two pages must never be able to report different numbers for the same day.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        // Both sectors must land on the same UTC day for this comparison to mean anything - the
        // second arrives five and a half hours after the first departs. See FlightDayUtc.
        var day = FlightDayUtc(1);

        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, day, plannedBlockMinutes: 90, arrivalDelayMinutes: 2, paxFlown: 90);
        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, day.AddHours(3), plannedBlockMinutes: 90, arrivalDelayMinutes: 60, paxFlown: 180);
        await ctx.Db.SaveChangesAsync();

        var catalog = EconomyConfigCatalog.Default();
        var trends = OkValueOf<TrendsProbe>(await StatsEndpoints.TrendsAsync(7, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None));
        var performance = OkValueOf<PerformanceProbe>(await StatsEndpoints.PerformanceAsync(7, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None));

        var performancePoint = Assert.Single(performance.Points);
        var trendPoint = trends.Points.Single(p => p.DateUtc == performancePoint.DateUtc);

        Assert.Equal(performancePoint.OnTimePercent, trendPoint.OnTimePercent);
        Assert.Equal(performancePoint.LoadFactorPercent, trendPoint.LoadFactorPercent);
        Assert.Equal(performancePoint.SectorsFlown, trendPoint.SectorsFlown);
    }

    private sealed record PerformanceProbe(int PeriodDays, List<PerformancePointProbe> Points);

    private sealed record PerformancePointProbe(string DateUtc, int SectorsFlown, double? OnTimePercent, double? LoadFactorPercent);

    // ---------- Reputation ----------

    [Fact]
    public async Task RecordedReputation_IsReadFromSnapshots_AndAbsentDaysStayNull()
    {
        // The whole reason the snapshot table exists is that reputation cannot be honestly
        // reconstructed. It follows that a day FSOps never observed must show a gap - carrying the
        // previous day's score forward would be claiming an observation that was never made.
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        ctx.Db.ReputationSnapshots.Add(new ReputationSnapshot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DateUtc = DayKey(now.AddDays(-3)),
            // Deliberately not a .x5 midpoint: this test is about a snapshot being read back on the
            // right day, not about which way Math.Round breaks a tie, and pinning that here would
            // turn an incidental rounding rule into an asserted requirement.
            Score = 61.24,
            RecordedUtc = now.AddDays(-3),
        });
        await ctx.Db.SaveChangesAsync();

        var result = await StatsEndpoints.TrendsAsync(7, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var body = OkValueOf<TrendsProbe>(result);

        Assert.Equal(1, body.ReputationRecordedDays);
        Assert.Equal(61.2, body.Points.Single(p => p.DateUtc == DayKey(now.AddDays(-3))).Reputation);
        Assert.All(body.Points.Where(p => p.DateUtc != DayKey(now.AddDays(-3))), p => Assert.Null(p.Reputation));
    }

    [Fact]
    public async Task ReputationSnapshotsForAnotherAirlineAreNotRead()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        ctx.Db.ReputationSnapshots.Add(new ReputationSnapshot
        {
            Id = Guid.NewGuid(),
            AirlineId = Guid.NewGuid(),
            DateUtc = DayKey(now),
            Score = 99,
            RecordedUtc = now,
        });
        await ctx.Db.SaveChangesAsync();

        var body = OkValueOf<TrendsProbe>(await StatsEndpoints.TrendsAsync(7, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None));

        Assert.Equal(0, body.ReputationRecordedDays);
        Assert.All(body.Points, p => Assert.Null(p.Reputation));
    }

    [Fact]
    public async Task ReputationPressure_IsTheSameTargetTheReputationCardUses()
    {
        // Pressure is not a proxy invented for this chart: it is ReputationCalculator's own
        // per-sector target, the exact figure GetReputationAsync averages to label the dashboard
        // card. This test pins that equality so the two can never drift apart.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var day = FlightDayUtc(1);
        var catalog = EconomyConfigCatalog.Default();
        var config = catalog.Get(ctx.Airline.Playstyle);

        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, day, plannedBlockMinutes: 90, arrivalDelayMinutes: 0, paxFlown: 120, landingFpm: -150);
        await ctx.Db.SaveChangesAsync();

        var expected = ReputationCalculator.TargetForCompletedFlight(config.Reputation, 0, -150);

        var body = OkValueOf<TrendsProbe>(await StatsEndpoints.TrendsAsync(7, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None));
        var point = body.Points.Single(p => p.DateUtc == DayKey(day.AddMinutes(91)));

        Assert.NotNull(expected);
        Assert.Equal(Math.Round(expected!.Value, 1), point.ReputationPressure);
    }

    [Fact]
    public async Task ReputationPressure_IsNullWhenTheDaysSectorsCarriedNoMeasurableSignal()
    {
        // A sim-rate-elevated sector with no landing rate has nothing honest to score at all. The
        // day still counts as flown, but its pressure must be null rather than a guessed value.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var day = FlightDayUtc(1);

        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, day, plannedBlockMinutes: 90, arrivalDelayMinutes: 0, paxFlown: 120, landingFpm: null, simRateElevated: true);
        await ctx.Db.SaveChangesAsync();

        var body = OkValueOf<TrendsProbe>(await StatsEndpoints.TrendsAsync(7, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None));
        var point = body.Points.Single(p => p.DateUtc == DayKey(day.AddMinutes(91)));

        Assert.Equal(1, point.SectorsFlown);
        Assert.Null(point.ReputationPressure);
    }

    [Fact]
    public async Task CurrentReputation_IsTheAirlinesLiveScore()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Airline.ReputationScore = 57.44;
        await ctx.Db.SaveChangesAsync();

        var body = OkValueOf<TrendsProbe>(await StatsEndpoints.TrendsAsync(7, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None));

        Assert.Equal(57.4, body.CurrentReputation);
    }
}

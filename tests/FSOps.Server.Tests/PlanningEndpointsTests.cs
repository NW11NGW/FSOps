using System.Collections;
using System.Reflection;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// The three decision surfaces: what a fare change would do, where to fly next, and what to buy
/// next. What these tests are really guarding is that none of the three ever becomes a second
/// economic model - a figure quoted before a player commits and a figure posted to the ledger
/// afterwards must be the same figure, produced by the same code.
/// </summary>
public class PlanningEndpointsTests
{
    private static T? GetProp<T>(object obj, string name)
    {
        var property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}.");
        return (T?)property.GetValue(obj);
    }

    private static object ValueOf(IResult result) => ((IValueHttpResult)result).Value!;

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static List<object> ListOf(object obj, string name)
    {
        var value = GetProp<object>(obj, name);
        return value is IEnumerable enumerable ? enumerable.Cast<object>().ToList() : new List<object>();
    }

    private static async Task<Route> SeedRouteAsync(RouteTestContext ctx, string departureIcao, string arrivalIcao, decimal fare)
    {
        var departure = await ctx.Db.Airports.FirstAsync(a => a.Icao == departureIcao);
        var arrival = await ctx.Db.Airports.FirstAsync(a => a.Icao == arrivalIcao);
        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = departureIcao,
            ArrivalIcao = arrivalIcao,
            FlightNumber = "101",
            DistanceNm = GreatCircle.DistanceNm(departure.Latitude, departure.Longitude, arrival.Latitude, arrival.Longitude),
            BaseFare = fare,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);
        await ctx.Db.SaveChangesAsync();
        return route;
    }

    // -----------------------------------------------------------------------------------
    // Fare workbench
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task RoutePricing_QuotesTheSameFiguresTheProjectorProduces()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var route = await SeedRouteAsync(ctx, "EGGD", "EGPH", fare: 90m);

        var result = await PlanningEndpoints.RoutePricingAsync(
            route.Id, fare: null, aircraftTypeId: null, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var value = ValueOf(result);
        Assert.True(GetProp<bool>(value, "priceable"));
        Assert.Equal(90m, GetProp<decimal>(value, "currentFare"));

        // Independently recomputed through the shared projector - the endpoint must be a thin
        // wrapper over it, never its own arithmetic.
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        var departure = await ctx.Db.Airports.FirstAsync(a => a.Icao == "EGGD");
        var arrival = await ctx.Db.Airports.FirstAsync(a => a.Icao == "EGPH");
        var expectedPlan = SectorProjector.Plan(
            economyConfig, ctx.Airline.StrategyProfile, ctx.Airline.ReputationScore, departure, arrival, ctx.AircraftType,
            route.DistanceNm, GetProp<DateTimeOffset>(value, "pricedAtUtc"), worldSeed: 1);
        var expected = SectorProjector.AtFare(economyConfig, ctx.Airline.StrategyProfile, expectedPlan, 90m);

        Assert.Equal(expectedPlan.ReferenceFare, GetProp<decimal>(value, "referenceFare"));
        Assert.Equal(expectedPlan.MarketDemandPax, GetProp<int>(value, "marketDemandPax"));

        var atFare = GetProp<object>(value, "atFare")!;
        Assert.Equal(90m, GetProp<decimal>(atFare, "fare"));
        Assert.Equal(expected.PaxBooked, GetProp<int>(atFare, "paxBooked"));
        Assert.Equal(Math.Round(expected.Revenue, 2), GetProp<decimal>(atFare, "revenue"));
        Assert.Equal(Math.Round(expected.TotalCost, 2), GetProp<decimal>(atFare, "cost"));
        Assert.Equal(Math.Round(expected.NetProfit, 2), GetProp<decimal>(atFare, "profit"));
    }

    [Fact]
    public async Task RoutePricing_CurveCoversTheWholeBand_AndPeaksInsideIt()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx, "EGGD", "EGPH", fare: 90m);

        var result = await PlanningEndpoints.RoutePricingAsync(
            route.Id, fare: null, aircraftTypeId: null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var value = ValueOf(result);
        var curve = ListOf(value, "curve");
        Assert.Equal(31, curve.Count);

        var fares = curve.Select(p => GetProp<decimal>(p, "fare")).ToList();
        Assert.Equal(fares.OrderBy(f => f), fares);

        var passengers = curve.Select(p => GetProp<int>(p, "paxBooked")).ToList();
        for (var i = 1; i < passengers.Count; i++)
        {
            Assert.True(passengers[i] <= passengers[i - 1], "Raising the fare must never sell more seats.");
        }

        var best = GetProp<decimal>(value, "bestSampledProfitFare");
        Assert.True(best > fares.First() && best < fares.Last(), $"The best sampled fare ({best}) sat on the edge of the band.");
    }

    /// <summary>
    /// Every recommendation needs a reason a player can disagree with. The verdict is returned as
    /// facts rather than a finished sentence because it quotes money, and money is formatted only at
    /// the point of display - so what must hold here is that the facts are all present and that
    /// nothing in the payload is a bare, unformattable currency string.
    /// </summary>
    [Fact]
    public async Task RoutePricing_AlwaysExplainsItself_AndLeavesMoneyForTheClientToFormat()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx, "EGGD", "EGPH", fare: 90m);

        var result = await PlanningEndpoints.RoutePricingAsync(
            route.Id, fare: null, aircraftTypeId: null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var value = ValueOf(result);
        var verdict = GetProp<object>(value, "verdict")!;

        Assert.Contains(GetProp<string>(verdict, "kind"), new[] { "NobodyBooks", "AlreadyBest", "CouldEarnMore" });
        Assert.True(GetProp<int>(verdict, "paxBooked") >= 0);
        Assert.Contains(GetProp<string>(verdict, "pricedRelativeToSuggestion"), new[] { "above", "below", "exactly at" });
        Assert.False(string.IsNullOrWhiteSpace(GetProp<string>(GetProp<object>(value, "assumedAircraft")!, "basis")));
    }

    /// <summary>
    /// The verdict must describe the fare being CONSIDERED, not the one currently saved. Quoting the
    /// saved fare made the sentence contradict the figures next to it the moment the player typed a
    /// different number into the box.
    /// </summary>
    [Fact]
    public async Task RoutePricing_VerdictDescribesTheFareBeingConsidered_NotTheSavedOne()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var route = await SeedRouteAsync(ctx, "EGGD", "EGPH", fare: 90m);

        var atSaved = ValueOf(await PlanningEndpoints.RoutePricingAsync(
            route.Id, fare: null, aircraftTypeId: null, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None));
        var savedProfit = GetProp<decimal>(GetProp<object>(atSaved, "verdict")!, "profit");

        // A fare far above the saved one books fewer passengers, so its verdict must quote a
        // different profit.
        var atCandidate = ValueOf(await PlanningEndpoints.RoutePricingAsync(
            route.Id, fare: 250m, aircraftTypeId: null, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None));
        var candidateVerdict = GetProp<object>(atCandidate, "verdict")!;

        Assert.NotEqual(savedProfit, GetProp<decimal>(candidateVerdict, "profit"));
        Assert.Equal(GetProp<int>(GetProp<object>(atCandidate, "atFare")!, "paxBooked"), GetProp<int>(candidateVerdict, "paxBooked"));
        Assert.Equal("above", GetProp<string>(candidateVerdict, "pricedRelativeToSuggestion"));
    }

    /// <summary>
    /// Money is stored in one base unit and formatted only for display, so no sentence composed on
    /// the server may contain a currency figure - the client cannot un-format it into the player's
    /// chosen currency. This catches a reason string quietly regaining a hardcoded number.
    /// </summary>
    [Fact]
    public async Task Opportunities_ReasonsCarryNoMoney_SoTheClientCanFormatIt()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var result = await PlanningEndpoints.OpportunitiesAsync(
            limit: null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var opportunities = ListOf(ValueOf(result), "opportunities");
        Assert.NotEmpty(opportunities);

        foreach (var opportunity in opportunities)
        {
            var reason = GetProp<string>(opportunity, "reason")!;
            var fare = GetProp<decimal>(opportunity, "suggestedFare");
            var profit = GetProp<decimal>(opportunity, "profitPerSector");

            Assert.DoesNotContain(fare.ToString("F2"), reason, StringComparison.Ordinal);
            Assert.DoesNotContain(profit.ToString("F2"), reason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The fare band the input offers must be the band the save actually enforces, or the player
    /// discovers the limit by being refused - see RouteEndpoints.MinimumFareFor.
    /// </summary>
    [Fact]
    public async Task RoutePricing_ReportsTheSameFareBandTheSaveEnforces()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var route = await SeedRouteAsync(ctx, "EGGD", "EGPH", fare: 90m);

        var result = await PlanningEndpoints.RoutePricingAsync(
            route.Id, fare: null, aircraftTypeId: null, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var value = ValueOf(result);
        var band = GetProp<object>(value, "fareBand")!;
        var referenceFare = GetProp<decimal>(value, "referenceFare");
        var minimum = GetProp<decimal>(band, "minimum");
        var maximum = GetProp<decimal>(band, "maximum");

        Assert.Equal(RouteEndpoints.MinimumFareFor(referenceFare), minimum);
        Assert.Equal(RouteEndpoints.MaximumFareFor(referenceFare), maximum);

        // Just inside the band saves; just outside is refused, with the band quoted back.
        var inside = await RouteEndpoints.UpdateAsync(
            route.Id, new UpdateRouteRequest(null, maximum, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(inside));

        var outside = await RouteEndpoints.UpdateAsync(
            route.Id, new UpdateRouteRequest(null, maximum + 1m, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(outside));
    }

    [Fact]
    public async Task RoutePricing_ForAnotherAirlinesRoute_IsNotFound()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var result = await PlanningEndpoints.RoutePricingAsync(
            Guid.NewGuid(), fare: null, aircraftTypeId: null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(result));
    }

    // -----------------------------------------------------------------------------------
    // Opportunity finder
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task Opportunities_SuggestsPairsFromABase_WithAReasonAndRealFigures()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var result = await PlanningEndpoints.OpportunitiesAsync(
            limit: null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var value = ValueOf(result);
        var bases = ListOf(value, "bases").Cast<string>().ToList();
        Assert.Contains("EGGD", bases);

        var opportunities = ListOf(value, "opportunities");
        Assert.NotEmpty(opportunities);

        foreach (var opportunity in opportunities)
        {
            Assert.Equal("EGGD", GetProp<string>(opportunity, "departureIcao"));
            Assert.False(string.IsNullOrWhiteSpace(GetProp<string>(opportunity, "reason")));
            Assert.True(GetProp<double>(opportunity, "distanceNm") >= SectorCapability.MinimumSuggestedSectorNm);
            Assert.True(GetProp<int>(opportunity, "expectedPassengers") > 0);
            // Revenue less every cost line the ledger would post - a suggestion that quotes revenue
            // alone is not a decision.
            Assert.Equal(
                Math.Round(GetProp<decimal>(opportunity, "revenuePerSector") - GetProp<decimal>(opportunity, "costPerSector"), 2),
                GetProp<decimal>(opportunity, "profitPerSector"));
        }

        var profits = opportunities.Select(o => GetProp<decimal>(o, "profitPerSector")).ToList();
        Assert.Equal(profits.OrderByDescending(p => p), profits);
    }

    [Fact]
    public async Task Opportunities_NeverSuggestsACityPairTheAirlineAlreadyFlies()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedRouteAsync(ctx, "EGGD", "EGPH", fare: 90m);

        var result = await PlanningEndpoints.OpportunitiesAsync(
            limit: null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var opportunities = ListOf(ValueOf(result), "opportunities");
        Assert.DoesNotContain(opportunities, o =>
            GetProp<string>(o, "arrivalIcao") == "EGPH" && GetProp<string>(o, "departureIcao") == "EGGD");
    }

    /// <summary>
    /// A pair nothing owned can fly is stated as such, not silently dropped - the same spirit as
    /// route creation's own refusals, which name the aircraft and the one action that changes the
    /// answer rather than leaving a dead end.
    /// </summary>
    [Fact]
    public async Task Opportunities_ReportsPairsBeyondTheFleet_RatherThanHidingThem()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        // Replace the fleet with something that can barely leave the airfield, so every candidate
        // pair is beyond it.
        var shortLegged = new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = "AT42", Family = "ATR", Manufacturer = "ATR", Name = "ATR 42-600",
            PaxCapacity = 48, RangeNm = 120, CruiseTasKts = 300, FuelBurnKgPerHour = 600,
            MtowTonnes = 18.6, MinRunwayFt = 3600, ServiceCeilingFt = 25000,
            PurchasePrice = 20_000_000m, MonthlyLeaseRate = 95_000m, MatchPatterns = "[]",
        };
        ctx.Db.AircraftTypes.Add(shortLegged);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        aircraft.AircraftTypeId = shortLegged.Id;
        await ctx.Db.SaveChangesAsync();

        var result = await PlanningEndpoints.OpportunitiesAsync(
            limit: null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var value = ValueOf(result);
        Assert.Empty(ListOf(value, "opportunities"));

        var blocked = ListOf(value, "blocked");
        Assert.NotEmpty(blocked);
        foreach (var entry in blocked)
        {
            var reason = GetProp<string>(entry, "reason")!;
            Assert.Contains("ATR 42-600", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Opportunities_AreDeterministic_TwoIdenticalCallsAgree()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();

        var first = ListOf(ValueOf(await PlanningEndpoints.OpportunitiesAsync(
            limit: 5, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None)), "opportunities");
        var second = ListOf(ValueOf(await PlanningEndpoints.OpportunitiesAsync(
            limit: 5, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None)), "opportunities");

        Assert.Equal(
            first.Select(o => GetProp<string>(o, "arrivalIcao")),
            second.Select(o => GetProp<string>(o, "arrivalIcao")));
    }

    [Fact]
    public async Task Opportunities_WithNoAirline_ReturnsAnEmptyListRatherThanFailing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Db.Airlines.Remove(ctx.Airline);
        await ctx.Db.SaveChangesAsync();

        var result = await PlanningEndpoints.OpportunitiesAsync(
            limit: null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Empty(ListOf(ValueOf(result), "opportunities"));
    }

    // -----------------------------------------------------------------------------------
    // Fleet planner
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// A planner that always finds a reason to spend money is not advice. The seeded fleet's one
    /// aircraft is reserved to the player, so it is deliberately NOT counted as idle - that
    /// reservation is a setting the app chose on the player's behalf.
    /// </summary>
    [Fact]
    public async Task FleetAdvice_WithAnIdleAircraft_LeadsWithRosteringItRatherThanBuying()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        aircraft.ReservedForPlayer = false;
        await ctx.Db.SaveChangesAsync();

        var result = await PlanningEndpoints.FleetAdviceAsync(
            ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var value = ValueOf(result);
        Assert.Equal(1, GetProp<int>(value, "idleAircraftCount"));
        Assert.Contains("Rostering", GetProp<string>(value, "headline")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FleetAdvice_ReservedAircraftIsNotCountedIdle()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var result = await PlanningEndpoints.FleetAdviceAsync(
            ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(0, GetProp<int>(ValueOf(result), "idleAircraftCount"));
    }

    /// <summary>
    /// A route nothing owned can reach is the clearest possible case for buying something, and the
    /// suggestion has to be priced with the sanctioned pricing paths - never
    /// AircraftType.PurchasePrice or MonthlyLeaseRate read straight off the catalogue row.
    /// </summary>
    [Fact]
    public async Task FleetAdvice_SuggestsAnAircraftForARouteNothingOwnedCanFly_PricedFromTheEconomyConfig()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();

        // A widebody nobody owns, and a route far beyond the seeded A320's legs.
        var widebody = new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = "A332", Family = "A330", Manufacturer = "Airbus", Name = "Airbus A330-200",
            PaxCapacity = 260, RangeNm = 6100, CruiseTasKts = 470, FuelBurnKgPerHour = 5600,
            // 7,900 ft rather than the catalogue's real 8,200: the fixture's EGGD has an 8,000 ft
            // runway, and the point of this test is the RANGE gap, not a runway one. (The endpoint
            // handles the runway case correctly - a widebody needing 8,200 ft genuinely cannot use
            // this airport, and is correctly not credited with unlocking a route out of it.)
            MtowTonnes = 242.0, MinRunwayFt = 7900, ServiceCeilingFt = 41000,
            PurchasePrice = 250_000_000m, MonthlyLeaseRate = 999_999m, MatchPatterns = "[]",
        };
        ctx.Db.AircraftTypes.Add(widebody);
        ctx.Db.Airports.Add(new Airport
        {
            Icao = "KJFK", Iata = "JFK", Name = "John F Kennedy International", Municipality = "New York",
            Country = "United States", Latitude = 40.6413, Longitude = -73.7781, ElevationFt = 13,
            SizeCategory = AirportSizeCategory.Large, HasScheduledService = true, LongestRunwayFt = 14511,
        });
        await ctx.Db.SaveChangesAsync();
        await SeedRouteAsync(ctx, "EGGD", "KJFK", fare: 400m);

        var result = await PlanningEndpoints.FleetAdviceAsync(ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var value = ValueOf(result);

        Assert.NotEmpty(ListOf(value, "unflyableRoutes"));

        var suggestions = ListOf(value, "suggestions");
        Assert.NotEmpty(suggestions);

        var suggested = suggestions.FirstOrDefault(s => GetProp<string>(s, "icaoType") == "A332");
        Assert.NotNull(suggested);
        Assert.True(GetProp<int>(suggested!, "unlocksRouteCount") >= 1);
        Assert.False(string.IsNullOrWhiteSpace(GetProp<string>(suggested!, "reason")));

        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        Assert.Equal(economyConfig.PurchasePriceFor(widebody), GetProp<decimal>(suggested!, "purchasePrice"));
        Assert.Equal(economyConfig.LeaseRateFor("A332"), GetProp<decimal?>(suggested!, "monthlyLease"));
        // Never the catalogue row's own columns - those are shared across playstyles and must not
        // price anything (see EconomyConfig.LeaseRates' doc).
        Assert.NotEqual(widebody.MonthlyLeaseRate, GetProp<decimal?>(suggested!, "monthlyLease"));
    }

    [Fact]
    public async Task FleetAdvice_WithNoAirline_IsNotFound()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Db.Airlines.Remove(ctx.Airline);
        await ctx.Db.SaveChangesAsync();

        var result = await PlanningEndpoints.FleetAdviceAsync(
            ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(result));
    }
}

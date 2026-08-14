using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;
using Route = FSOps.Core.Entities.Route;

namespace FSOps.Server.Endpoints;

/// <summary>
/// The decision surfaces: what a fare change would do, where the airline should fly next, and what
/// it should buy next. Read-only throughout - nothing here writes a row or moves any money; setting
/// a fare is still <c>PUT /routes/{id}</c>, buying an aircraft is still the Fleet endpoints.
///
/// <para><b>Every figure comes from <see cref="SectorProjector"/></b>, which is
/// <see cref="FlightEconomicsCalculator"/>, which is what
/// <see cref="FlightEconomicsPoster"/> posts to the ledger. Nothing in this file does arithmetic on
/// money of its own; it chooses which sectors to ask about, ranks the answers, and explains them.
/// See <see cref="SectorProjector"/>'s class doc for exactly how far "the prediction matches the
/// ledger" goes and where it stops (fuel on a hand-flown sector).</para>
///
/// <para><b>Deterministic.</b> Every ordering is fully tie-broken (by ICAO, by type name) so the
/// same airline and the same instant always produce the same list in the same order. The only
/// input that moves on its own is the clock, which the demand model has always keyed season and
/// day-of-week off.</para>
/// </summary>
public static class PlanningEndpoints
{
    /// <summary>
    /// How many airports the opportunity finder will treat as somewhere it could fly FROM. Home,
    /// then wherever aircraft actually are, then wherever the airline already flies - see
    /// <see cref="ResolveBases"/>. Capped because every base multiplies the candidate sweep,
    /// and an airline with thirty routes does not want thirty bases' worth of suggestions: the point
    /// is a short, legible list, not an exhaustive one.
    /// </summary>
    private const int MaxBases = 6;

    /// <summary>Candidate destinations kept per base before the full economics are run. Ranked by
    /// the passenger pool the demand model gives the pair, which is cheap to evaluate and is the
    /// dominant term in what a sector earns.</summary>
    private const int ShortlistPerBase = 40;

    /// <summary>Candidate destinations kept per base from BEYOND what the fleet can reach - the
    /// "you would need a different aircraft for this" pool. Small: a handful of these is
    /// informative, a list of them would just be a catalogue of everywhere the airline cannot go.</summary>
    private const int BeyondReachShortlistPerBase = 6;

    /// <summary>Total pairs within the fleet's reach that the full projection runs against, across
    /// every base.</summary>
    private const int MaxProjectedPairs = 160;

    /// <summary>The same cap for out-of-reach pairs, kept separately - see FindOpportunitiesAsync.</summary>
    private const int MaxProjectedBeyondReachPairs = 12;

    /// <summary>How many "you cannot fly this yet" entries the opportunity list carries. A few, so a
    /// player learns what an aircraft would open up; not many, so the list stays about what they CAN
    /// do.</summary>
    private const int MaxBlockedOpportunities = 3;

    private const int DefaultOpportunityLimit = 8;
    private const int MaxOpportunityLimit = 25;

    /// <summary>How many acquisition suggestions the fleet planner returns.</summary>
    private const int MaxFleetSuggestions = 4;

    public static void MapPlanningEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/routes/{id:guid}/pricing", RoutePricingAsync);
        group.MapGet("/planning/opportunities", OpportunitiesAsync);
        group.MapGet("/planning/fleet-advice", FleetAdviceAsync);
    }

    // ---------------------------------------------------------------------------------------
    // 1. Fare workbench - "what does charging this do?"
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// What a saved route earns at a given fare, what it earns at every other fare worth
    /// considering, and where the app itself would price it. The answer to "am I over- or
    /// under-pricing this?", which the player previously had no way to ask at all: a fare could be
    /// chosen once at creation and then never revisited.
    /// </summary>
    internal static async Task<IResult> RoutePricingAsync(
        Guid id,
        decimal? fare,
        Guid? aircraftTypeId,
        FsOpsDbContext db,
        ICurrentUser currentUser,
        EconomyConfigCatalog economyConfigCatalog,
        CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound();
        }

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.AirlineId == airline.Id, ct);
        if (route is null)
        {
            return Results.NotFound();
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        var departure = await db.Airports.Include(a => a.Runways).FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct);
        var arrival = await db.Airports.Include(a => a.Runways).FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct);
        if (departure is null || arrival is null || route.DistanceNm <= 0)
        {
            // World data can't resolve one end, or this is a degenerate row. There is no honest
            // figure to give, so give none rather than a guessed one.
            return Results.Ok(new
            {
                routeId = route.Id,
                route.DepartureIcao,
                route.ArrivalIcao,
                route.FlightNumber,
                route.DistanceNm,
                currentFare = route.BaseFare,
                priceable = false,
                reason = "This route's airports aren't in the world data, so its economics can't be priced.",
            });
        }

        var fleet = await LoadFleetAsync(db, airline.Id, ct);
        var ownedTypes = ToPlanningTypes(fleet);
        var typesById = fleet.Select(f => f.Type).DistinctBy(t => t.Id).ToDictionary(t => t.Id);

        var (assumedType, basis) = await ResolveAssumedTypeAsync(db, airline, route, departure, arrival, fleet, aircraftTypeId, ct);
        if (assumedType is null)
        {
            return Results.Ok(new
            {
                routeId = route.Id,
                route.DepartureIcao,
                route.ArrivalIcao,
                route.FlightNumber,
                route.DistanceNm,
                currentFare = route.BaseFare,
                priceable = false,
                reason = "There is no aircraft type available to price this route with.",
            });
        }

        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
        var pricedAtUtc = DateTimeOffset.UtcNow;

        var plan = SectorProjector.Plan(
            economyConfig, airline.StrategyProfile, airline.ReputationScore, departure, arrival, assumedType,
            route.DistanceNm, pricedAtUtc, worldSeed);

        var candidateFare = fare is decimal requested && requested > 0 ? requested : route.BaseFare;
        var atCandidate = SectorProjector.AtFare(economyConfig, airline.StrategyProfile, plan, candidateFare);
        var atCurrent = SectorProjector.AtFare(economyConfig, airline.StrategyProfile, plan, route.BaseFare);
        var atReference = SectorProjector.AtFare(economyConfig, airline.StrategyProfile, plan, plan.ReferenceFare);
        var curve = FareCurveCalculator.Calculate(economyConfig, airline.StrategyProfile, plan);

        var typeOptions = ownedTypes
            .OrderByDescending(t => t.Seats)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t =>
            {
                var problem = SectorCapability.Assess(t, departure, arrival, route.DistanceNm);
                return new
                {
                    aircraftTypeId = t.AircraftTypeId,
                    typeName = t.Name,
                    icaoType = t.IcaoType,
                    seats = t.Seats,
                    ownedCount = t.OwnedCount,
                    canOperate = problem == SectorCapabilityProblem.None,
                    blockedBy = problem == SectorCapabilityProblem.None ? null : problem.ToString(),
                };
            })
            .ToList();

        return Results.Ok(new
        {
            routeId = route.Id,
            route.DepartureIcao,
            departureName = departure.Name,
            route.ArrivalIcao,
            arrivalName = arrival.Name,
            route.FlightNumber,
            route.DistanceNm,
            priceable = true,
            currentFare = route.BaseFare,
            referenceFare = plan.ReferenceFare,
            revenueMaximizingFare = curve.RevenueMaximizingFare,
            bestSampledProfitFare = curve.BestProfitPoint.Fare,
            marketDemandPax = plan.MarketDemandPax,
            fuelPricePerKg = plan.FuelPricePerKg,
            pricedAtUtc,
            // The allowed band, straight from the same constants the fare is validated against, so
            // the input can bound itself rather than the player discovering the limit by being
            // refused - see RouteEndpoints.FareBandFor.
            fareBand = new
            {
                minimum = RouteEndpoints.MinimumFareFor(plan.ReferenceFare),
                maximum = RouteEndpoints.MaximumFareFor(plan.ReferenceFare),
            },
            assumedAircraft = new
            {
                aircraftTypeId = assumedType.Id,
                typeName = assumedType.Name,
                icaoType = assumedType.IcaoType,
                seats = assumedType.PaxCapacity,
                basis,
                canOperate = ownedTypes.Count == 0 ||
                    SectorCapability.Assess(ToPlanningType(assumedType, ownedTypes), departure, arrival, route.DistanceNm) == SectorCapabilityProblem.None,
            },
            aircraftOptions = typeOptions,
            atFare = PricePoint(atCandidate),
            atCurrentFare = PricePoint(atCurrent),
            atReferenceFare = PricePoint(atReference),
            curve = curve.Points.Select(p => new
            {
                fare = p.Fare,
                multipleOfReferenceFare = p.MultipleOfReferenceFare,
                paxBooked = p.PaxBooked,
                loadFactorPercent = Math.Round(p.LoadFactor * 100, 1),
                revenue = Math.Round(p.Revenue, 2),
                cost = Math.Round(p.TotalCost, 2),
                profit = Math.Round(p.NetProfit, 2),
            }),
            // Deliberately about the fare being CONSIDERED, not the one currently saved: the player
            // is looking at a number in a box and wants to know about that number. Quoting the saved
            // fare here made the sentence contradict the tiles beside it the moment they typed.
            verdict = PricingVerdict(candidateFare, plan.ReferenceFare, curve, atCandidate, typesById.Count),
        });
    }

    private static object PricePoint(SectorProjection projection) => new
    {
        fare = projection.Fare,
        paxBooked = projection.PaxBooked,
        seats = projection.Plan.Seats,
        loadFactorPercent = Math.Round(projection.LoadFactor * 100, 1),
        revenue = Math.Round(projection.Revenue, 2),
        cost = Math.Round(projection.TotalCost, 2),
        profit = Math.Round(projection.NetProfit, 2),
    };

    /// <summary>
    /// One sentence a player can disagree with. Always says which way the current fare sits relative
    /// to the fare that earns most, and never claims a precision the curve does not have - the
    /// comparison is against a sampled grid, and it says so when it matters.
    /// </summary>
    /// <summary>
    /// The verdict, as facts rather than as a finished sentence: the money in it (a better fare, the
    /// profit it would add) has to be rendered by the client's own currency formatter, so composing
    /// the whole sentence here would mean printing a bare number in whatever currency the server
    /// happened to assume. The client writes one sentence from these fields - see
    /// FarePricingDialog.
    /// </summary>
    private static object PricingVerdict(
        decimal fare, decimal referenceFare, FareCurve curve, SectorProjection atFare, int ownedTypeCount)
    {
        var best = curve.BestProfitPoint;
        var gap = Math.Round(best.NetProfit - atFare.NetProfit, 2, MidpointRounding.AwayFromZero);

        var kind = atFare.PaxBooked == 0
            ? "NobodyBooks"
            : gap <= 0m ? "AlreadyBest" : "CouldEarnMore";

        return new
        {
            kind,
            paxBooked = atFare.PaxBooked,
            loadFactorPercent = Math.Round(atFare.LoadFactor * 100, 1),
            profit = Math.Round(atFare.NetProfit, 2),
            // Null unless a better fare actually exists among the sampled points.
            betterFare = kind == "CouldEarnMore" ? best.Fare : (decimal?)null,
            betterFarePaxBooked = kind == "CouldEarnMore" ? best.PaxBooked : (int?)null,
            extraProfit = kind == "CouldEarnMore" ? gap : (decimal?)null,
            pricedRelativeToSuggestion = fare > referenceFare ? "above" : fare < referenceFare ? "below" : "exactly at",
            // Worth saying only when the airline has more than one type and the figures therefore
            // depend on which one was assumed.
            aircraftDependent = ownedTypeCount > 1,
        };
    }

    /// <summary>
    /// Which aircraft the figures should assume, and why - stated to the player rather than hidden,
    /// because a sector's economics genuinely differ by airframe (seats feed bookings, MTOW feeds
    /// the fee lines, block time feeds crew, maintenance and fuel). Preference order is "whatever is
    /// most likely to actually fly it": an explicit choice, then whatever is rostered on this route,
    /// then something reserved to the player that can operate it, then anything owned that can, then
    /// the fleet's default, then the catalogue's - matching RouteEndpoints' own fallback so the two
    /// never disagree about a fleetless airline.
    /// </summary>
    private static async Task<(AircraftType? Type, string Basis)> ResolveAssumedTypeAsync(
        FsOpsDbContext db,
        Airline airline,
        Route route,
        Airport departure,
        Airport arrival,
        IReadOnlyList<(FleetAircraft Aircraft, AircraftType Type)> fleet,
        Guid? requestedTypeId,
        CancellationToken ct)
    {
        if (requestedTypeId is Guid requested)
        {
            var chosen = await db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == requested, ct);
            if (chosen is not null)
            {
                return (chosen, "You picked this aircraft.");
            }
        }

        var rosteredFleetIds = await db.PilotScheduleEntries
            .Where(e => e.RouteId == route.Id)
            .Select(e => e.FleetAircraftId)
            .Distinct()
            .ToListAsync(ct);

        var rostered = fleet
            .Where(f => rosteredFleetIds.Contains(f.Aircraft.Id))
            .OrderBy(f => f.Aircraft.Registration, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Type)
            .FirstOrDefault();
        if (rostered is not null)
        {
            return (rostered, "This is the aircraft already rostered on this route.");
        }

        var planningTypes = ToPlanningTypes(fleet);

        var reserved = fleet
            .Where(f => f.Aircraft.ReservedForPlayer)
            .OrderBy(f => f.Aircraft.Registration, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Type)
            .FirstOrDefault(t => SectorCapability.CanOperate(ToPlanningType(t, planningTypes), departure, arrival, route.DistanceNm));
        if (reserved is not null)
        {
            return (reserved, "This is the aircraft reserved to you that can fly this route.");
        }

        var capable = planningTypes
            .Where(t => SectorCapability.CanOperate(t, departure, arrival, route.DistanceNm))
            .OrderByDescending(t => t.Seats)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (capable is not null)
        {
            return (fleet.First(f => f.Type.Id == capable.AircraftTypeId).Type, "The largest aircraft you own that can fly this route.");
        }

        var fleetDefault = fleet
            .OrderBy(f => f.Type.IcaoType, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Type)
            .FirstOrDefault();
        if (fleetDefault is not null)
        {
            return (fleetDefault, "Nothing you own can currently fly this route - these figures assume your fleet's default type.");
        }

        var catalogueDefault = await db.AircraftTypes.OrderBy(t => t.IcaoType).FirstOrDefaultAsync(ct);
        return (catalogueDefault, "You have no aircraft yet - these figures assume a catalogue default.");
    }

    // ---------------------------------------------------------------------------------------
    // 2. Opportunity finder - "where should I fly next?"
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Ranked city pairs worth opening, priced with the aircraft that would actually fly them. The
    /// demand model could already score any pair; this is the planning tool that exposes it, instead
    /// of leaving the player to guess-and-check by creating routes and watching what happens.
    /// </summary>
    internal static async Task<IResult> OpportunitiesAsync(
        int? limit, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { bases = Array.Empty<string>(), opportunities = Array.Empty<object>(), blocked = Array.Empty<object>() });
        }

        var take = Math.Clamp(limit ?? DefaultOpportunityLimit, 1, MaxOpportunityLimit);
        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
        var pricedAtUtc = DateTimeOffset.UtcNow;

        var fleet = await LoadFleetAsync(db, airline.Id, ct);
        var ownedTypes = ToPlanningTypes(fleet);

        var routes = await db.Routes.Where(r => r.AirlineId == airline.Id).ToListAsync(ct);
        var served = new HashSet<string>(routes.Select(r => UnorderedPairKey(r.DepartureIcao, r.ArrivalIcao)), StringComparer.OrdinalIgnoreCase);

        var found = await FindOpportunitiesAsync(
            db, airline, economyConfig, fleet, ownedTypes, routes, served, pricedAtUtc, worldSeed, ct);

        return Results.Ok(new
        {
            bases = found.BaseIcaos,
            fleetTypeCount = ownedTypes.Count,
            opportunities = found.Flyable.Take(take).Select(o => new
            {
                departureIcao = o.Departure.Icao,
                departureName = o.Departure.Name,
                arrivalIcao = o.Arrival.Icao,
                arrivalName = o.Arrival.Name,
                arrivalMunicipality = o.Arrival.Municipality,
                arrivalCountry = o.Arrival.Country,
                distanceNm = Math.Round(o.Projection.Plan.DistanceNm, 1),
                blockMinutes = o.Projection.Plan.BlockMinutes,
                suggestedFare = o.Projection.Fare,
                marketDemandPax = o.Projection.Plan.MarketDemandPax,
                expectedPassengers = o.Projection.PaxBooked,
                seats = o.Projection.Plan.Seats,
                loadFactorPercent = Math.Round(o.Projection.LoadFactor * 100, 1),
                revenuePerSector = Math.Round(o.Projection.Revenue, 2),
                costPerSector = Math.Round(o.Projection.TotalCost, 2),
                profitPerSector = Math.Round(o.Projection.NetProfit, 2),
                aircraftTypeName = o.Type.Name,
                reason = o.Reason,
            }),
            blocked = found.Blocked.Take(MaxBlockedOpportunities).Select(b => new
            {
                departureIcao = b.Departure.Icao,
                arrivalIcao = b.Arrival.Icao,
                arrivalName = b.Arrival.Name,
                arrivalCountry = b.Arrival.Country,
                distanceNm = Math.Round(b.DistanceNm, 1),
                marketDemandPax = b.MarketDemandPax,
                reason = b.Reason,
            }),
        });
    }

    private sealed record FlyableOpportunity(Airport Departure, Airport Arrival, PlanningAircraftType Type, SectorProjection Projection, string Reason);

    private sealed record BlockedOpportunity(Airport Departure, Airport Arrival, double DistanceNm, int MarketDemandPax, string Reason);

    private sealed record OpportunityResult(
        IReadOnlyList<string> BaseIcaos,
        IReadOnlyList<FlyableOpportunity> Flyable,
        IReadOnlyList<BlockedOpportunity> Blocked);

    /// <summary>
    /// The sweep itself, shared by the opportunity list and the fleet planner (which needs the
    /// blocked entries to say what an aircraft would unlock). Three passes, cheapest first: a
    /// distance filter over a lightweight airport list, a demand ranking to shortlist, then the full
    /// economics on the survivors - so the expensive step runs on roughly a hundred pairs rather
    /// than on every airport in the world.
    /// </summary>
    private static async Task<OpportunityResult> FindOpportunitiesAsync(
        FsOpsDbContext db,
        Airline airline,
        EconomyConfig economyConfig,
        IReadOnlyList<(FleetAircraft Aircraft, AircraftType Type)> fleet,
        IReadOnlyList<PlanningAircraftType> ownedTypes,
        IReadOnlyList<Route> routes,
        HashSet<string> servedPairs,
        DateTimeOffset pricedAtUtc,
        int worldSeed,
        CancellationToken ct)
    {
        var baseIcaos = ResolveBases(airline, fleet, routes);
        if (baseIcaos.Count == 0)
        {
            return new OpportunityResult(baseIcaos, Array.Empty<FlyableOpportunity>(), Array.Empty<BlockedOpportunity>());
        }

        var baseAirports = await db.Airports
            .Where(a => baseIcaos.Contains(a.Icao))
            .ToDictionaryAsync(a => a.Icao, ct);

        // Large and medium airports with scheduled service only. Deliberately not every airfield on
        // record: the demand model gives a spoke-to-spoke pair a catchment of 0.6 x 0.6, so a list
        // built from small strips would be a list of routes that lose money, dressed up as advice.
        var candidates = await db.Airports
            .Where(a => a.HasScheduledService &&
                        (a.SizeCategory == AirportSizeCategory.Large || a.SizeCategory == AirportSizeCategory.Medium))
            .Select(a => new CandidateAirport(a.Icao, a.Latitude, a.Longitude, a.SizeCategory, a.LongestRunwayFt))
            .ToListAsync(ct);

        // The furthest anything owned could plan to. With no fleet at all there is nothing to
        // suggest against, so the whole sweep is skipped rather than guessed at.
        var maxOperationalRangeNm = ownedTypes.Count == 0
            ? 0
            : ownedTypes.Max(t => RouteRangeAssessor.OperationalRangeNm(t.RangeNm));

        var shortlist = new List<(string BaseIcao, CandidateAirport Candidate, double DistanceNm, int Demand)>();

        foreach (var baseIcao in baseIcaos)
        {
            if (!baseAirports.TryGetValue(baseIcao, out var baseAirport))
            {
                continue;
            }

            var withinReach = new List<(string BaseIcao, CandidateAirport Candidate, double DistanceNm, int Demand)>();
            var beyondReach = new List<(string BaseIcao, CandidateAirport Candidate, double DistanceNm, int Demand)>();

            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Icao, baseIcao, StringComparison.OrdinalIgnoreCase) ||
                    servedPairs.Contains(UnorderedPairKey(baseIcao, candidate.Icao)))
                {
                    continue;
                }

                var distanceNm = GreatCircle.DistanceNm(
                    baseAirport.Latitude, baseAirport.Longitude, candidate.Latitude, candidate.Longitude);

                if (distanceNm < SectorCapability.MinimumSuggestedSectorNm)
                {
                    continue;
                }

                // Keep pairs just beyond the fleet's reach too - those are the "you would need a
                // different aircraft" entries, and hiding them is exactly the dead end route
                // creation used to be. Anything twice the fleet's range away is not a near miss and
                // is dropped, or every suggestion list would end with the same handful of
                // intercontinental pairs.
                if (maxOperationalRangeNm > 0 && distanceNm > maxOperationalRangeNm * 2)
                {
                    continue;
                }

                var demand = DemandCalculator.AvailablePassengers(
                    economyConfig.Demand, baseAirport.SizeCategory, candidate.SizeCategory, distanceNm, pricedAtUtc, airline.ReputationScore);

                var entry = (baseIcao, candidate, distanceNm, demand);
                if (maxOperationalRangeNm > 0 && distanceNm > maxOperationalRangeNm)
                {
                    beyondReach.Add(entry);
                }
                else
                {
                    withinReach.Add(entry);
                }
            }

            // Shortlisted SEPARATELY, and this is not a tidiness choice. The demand model gives a
            // large-to-large pair the same passenger pool anywhere in the 300-2,500 nm sweet spot,
            // so one combined ranking fills every slot with sweet-spot pairs the fleet can already
            // reach - and the "you'd need a different aircraft for this" list, which is the whole
            // point of showing pairs the airline cannot fly, silently comes back empty forever. Ranking
            // the out-of-reach ones in their own pool guarantees the best of them are actually seen.
            IEnumerable<(string BaseIcao, CandidateAirport Candidate, double DistanceNm, int Demand)> Best(
                List<(string BaseIcao, CandidateAirport Candidate, double DistanceNm, int Demand)> pool, int take) => pool
                    .OrderByDescending(x => x.Demand)
                    .ThenBy(x => x.DistanceNm)
                    .ThenBy(x => x.Candidate.Icao, StringComparer.Ordinal)
                    .Take(take);

            shortlist.AddRange(Best(withinReach, ShortlistPerBase));
            shortlist.AddRange(Best(beyondReach, BeyondReachShortlistPerBase));
        }

        // One entry per unordered city pair - a pair reachable from two different bases is one
        // opportunity, not two. The within/beyond split survives this step for the same reason it
        // existed in the first place: a single global cap here would crowd the out-of-reach pairs
        // straight back out again.
        var dedupedByPair = shortlist
            .GroupBy(x => UnorderedPairKey(x.BaseIcao, x.Candidate.Icao), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Demand).ThenBy(x => x.BaseIcao, StringComparer.Ordinal).First())
            .OrderByDescending(x => x.Demand)
            .ThenBy(x => x.DistanceNm)
            .ThenBy(x => x.Candidate.Icao, StringComparer.Ordinal)
            .ToList();

        var deduped = dedupedByPair
            .Where(x => maxOperationalRangeNm <= 0 || x.DistanceNm <= maxOperationalRangeNm)
            .Take(MaxProjectedPairs)
            .Concat(dedupedByPair
                .Where(x => maxOperationalRangeNm > 0 && x.DistanceNm > maxOperationalRangeNm)
                .Take(MaxProjectedBeyondReachPairs))
            .ToList();

        // Runways are only loaded now, for the survivors - the runway check needs the actual rows
        // and loading them for every airport in the world to answer a question about a hundred pairs
        // would be absurd.
        var neededIcaos = deduped.Select(x => x.Candidate.Icao).Concat(deduped.Select(x => x.BaseIcao)).Distinct().ToList();
        var fullAirports = await db.Airports
            .Include(a => a.Runways)
            .Where(a => neededIcaos.Contains(a.Icao))
            .ToDictionaryAsync(a => a.Icao, ct);

        var flyable = new List<FlyableOpportunity>();
        var blocked = new List<BlockedOpportunity>();

        foreach (var entry in deduped)
        {
            if (!fullAirports.TryGetValue(entry.BaseIcao, out var departure) ||
                !fullAirports.TryGetValue(entry.Candidate.Icao, out var arrival))
            {
                continue;
            }

            SectorProjection? bestProjection = null;
            PlanningAircraftType? bestType = null;

            foreach (var type in ownedTypes.OrderByDescending(t => t.Seats).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!SectorCapability.CanOperate(type, departure, arrival, entry.DistanceNm))
                {
                    continue;
                }

                var aircraftType = fleet.First(f => f.Type.Id == type.AircraftTypeId).Type;
                var projection = SectorProjector.Project(
                    economyConfig, airline.StrategyProfile, airline.ReputationScore, departure, arrival, aircraftType,
                    entry.DistanceNm, fare: null, pricedAtUtc, worldSeed);

                if (bestProjection is null || projection.NetProfit > bestProjection.NetProfit)
                {
                    bestProjection = projection;
                    bestType = type;
                }
            }

            if (bestProjection is null || bestType is null)
            {
                var why = SectorCapability.ExplainWhyNoneCanOperate(ownedTypes, departure, arrival, entry.DistanceNm);
                blocked.Add(new BlockedOpportunity(departure, arrival, entry.DistanceNm, entry.Demand,
                    $"{arrival.Icao} would carry about {entry.Demand} passengers a day from {departure.Icao}, but {why}"));
                continue;
            }

            flyable.Add(new FlyableOpportunity(departure, arrival, bestType, bestProjection,
                OpportunityReason(departure, arrival, bestType, bestProjection)));
        }

        return new OpportunityResult(
            baseIcaos,
            flyable
                .OrderByDescending(o => o.Projection.NetProfit)
                .ThenBy(o => o.Arrival.Icao, StringComparer.Ordinal)
                .ToList(),
            blocked
                .OrderByDescending(b => b.MarketDemandPax)
                .ThenBy(b => b.Arrival.Icao, StringComparer.Ordinal)
                .ToList());
    }

    private sealed record CandidateAirport(string Icao, double Latitude, double Longitude, AirportSizeCategory SizeCategory, int LongestRunwayFt);

    /// <summary>
    /// One sentence a player can disagree with.
    ///
    /// <para><b>Deliberately contains no money.</b> Currency is a user setting and money is stored in
    /// one base unit, formatted only at the point of display - a server-composed sentence has no way
    /// to know whether the reader is looking at pounds or yen, so the fare and the profit are
    /// returned as fields beside this and rendered by the client's own formatter. This sentence
    /// carries the reasoning; the columns carry the figures.</para>
    ///
    /// <para>It also does NOT repeat the distance: the row beside it already shows that, and quoting
    /// it twice through two different roundings produced "302 nm" in the column and "303 nm" in the
    /// sentence, which reads as a typo rather than as the same number.</para>
    /// </summary>
    private static string OpportunityReason(Airport departure, Airport arrival, PlanningAircraftType type, SectorProjection projection)
    {
        var fill = projection.LoadFactor * 100;
        var seatLimited = projection.Plan.MarketDemandPax > projection.Plan.Seats;

        var opener = seatLimited
            ? $"{departure.Icao}-{arrival.Icao} has more demand ({projection.Plan.MarketDemandPax} a day) than the {type.Name} has seats"
            : $"{departure.Icao}-{arrival.Icao} draws about {projection.Plan.MarketDemandPax} passengers a day";

        return $"{opener}, so a sector fills to {fill:F0}% at the suggested fare.";
    }

    /// <summary>
    /// Where the airline could plausibly start a sector FROM: its home airport first, then anywhere
    /// it actually has an aircraft parked, then anywhere it already flies. Ordered by how real each
    /// is as a base - somewhere an aircraft is sitting is a better starting point than somewhere the
    /// airline merely lands - and fully tie-broken by ICAO so the list never reshuffles between two
    /// identical requests.
    /// </summary>
    private static IReadOnlyList<string> ResolveBases(
        Airline airline, IReadOnlyList<(FleetAircraft Aircraft, AircraftType Type)> fleet, IReadOnlyList<Route> routes)
    {
        var ordered = new List<string>();

        void Add(string? icao)
        {
            if (string.IsNullOrWhiteSpace(icao)) return;
            var upper = icao.Trim().ToUpperInvariant();
            if (!ordered.Contains(upper, StringComparer.OrdinalIgnoreCase)) ordered.Add(upper);
        }

        Add(airline.HomeAirportIcao);

        foreach (var icao in fleet
            .Where(f => !string.IsNullOrWhiteSpace(f.Aircraft.LocationIcao))
            .GroupBy(f => f.Aircraft.LocationIcao.ToUpperInvariant())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key))
        {
            Add(icao);
        }

        foreach (var icao in routes
            .SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao })
            .GroupBy(i => i.ToUpperInvariant())
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key))
        {
            Add(icao);
        }

        return ordered.Take(MaxBases).ToList();
    }

    // ---------------------------------------------------------------------------------------
    // 3. Fleet planner - "what should I buy next?"
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// What the fleet is actually doing, and what acquiring an aircraft would change. Deliberately
    /// willing to answer "nothing - you already have aircraft sitting idle": a planner that always
    /// finds a reason to spend money is not advice.
    /// </summary>
    internal static async Task<IResult> FleetAdviceAsync(
        FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound();
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
        var pricedAtUtc = DateTimeOffset.UtcNow;

        var fleet = await LoadFleetAsync(db, airline.Id, ct);
        var ownedTypes = ToPlanningTypes(fleet);
        var routes = await db.Routes.Where(r => r.AirlineId == airline.Id && r.IsActive).ToListAsync(ct);
        var servedPairs = new HashSet<string>(
            (await db.Routes.Where(r => r.AirlineId == airline.Id).Select(r => new { r.DepartureIcao, r.ArrivalIcao }).ToListAsync(ct))
                .Select(r => UnorderedPairKey(r.DepartureIcao, r.ArrivalIcao)),
            StringComparer.OrdinalIgnoreCase);

        // Weekly sectors per airframe, straight off the saved schedules - the only record of what an
        // aircraft is actually committed to doing.
        var scheduledSectorsByFleetId = (await db.PilotScheduleEntries.Select(e => new { e.FleetAircraftId }).ToListAsync(ct))
            .GroupBy(e => e.FleetAircraftId)
            .ToDictionary(g => g.Key, g => g.Count());

        var routeIcaos = routes.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        var routeAirports = await db.Airports.Include(a => a.Runways).Where(a => routeIcaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

        var opportunities = await FindOpportunitiesAsync(
            db, airline, economyConfig, fleet, ownedTypes, routes, servedPairs, pricedAtUtc, worldSeed, ct);

        var catalogue = await db.AircraftTypes.ToListAsync(ct);
        var cashBalance = (await db.LedgerTransactions.Where(t => t.AirlineId == airline.Id).Select(t => t.Amount).ToListAsync(ct)).Sum();

        var utilisation = fleet
            .Select(f => new
            {
                fleetAircraftId = f.Aircraft.Id,
                registration = f.Aircraft.Registration,
                typeName = f.Type.Name,
                seats = f.Type.PaxCapacity,
                locationIcao = f.Aircraft.LocationIcao,
                status = f.Aircraft.Status.ToString(),
                reservedForPlayer = f.Aircraft.ReservedForPlayer,
                scheduledSectorsPerWeek = scheduledSectorsByFleetId.GetValueOrDefault(f.Aircraft.Id, 0),
            })
            .OrderBy(a => a.scheduledSectorsPerWeek)
            .ThenBy(a => a.registration, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // "Idle" excludes an aircraft reserved to the player: it has no schedule BECAUSE it is being
        // kept free for them to fly, which is the whole point of the reservation flag, and calling
        // that idle would be telling the player off for a setting the app chose on their behalf.
        var idle = utilisation.Where(a => a.scheduledSectorsPerWeek == 0 && !a.reservedForPlayer).ToList();

        // Routes the airline has but nothing it owns can operate, and routes where the market is
        // bigger than the aircraft. Both are money already on the table.
        var unflyableRoutes = new List<object>();
        var seatCappedRoutes = new List<(Route Route, int Demand, int Seats, string TypeName)>();

        foreach (var route in routes.OrderBy(r => r.DepartureIcao, StringComparer.Ordinal).ThenBy(r => r.ArrivalIcao, StringComparer.Ordinal))
        {
            if (!routeAirports.TryGetValue(route.DepartureIcao, out var dep) ||
                !routeAirports.TryGetValue(route.ArrivalIcao, out var arr) ||
                route.DistanceNm <= 0)
            {
                continue;
            }

            var capable = ownedTypes.Where(t => SectorCapability.CanOperate(t, dep, arr, route.DistanceNm)).ToList();
            if (capable.Count == 0)
            {
                unflyableRoutes.Add(new
                {
                    routeId = route.Id,
                    route.DepartureIcao,
                    route.ArrivalIcao,
                    route.DistanceNm,
                    reason = SectorCapability.ExplainWhyNoneCanOperate(ownedTypes, dep, arr, route.DistanceNm),
                });
                continue;
            }

            var largest = capable.OrderByDescending(t => t.Seats).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).First();
            var demand = DemandCalculator.AvailablePassengers(
                economyConfig.Demand, dep.SizeCategory, arr.SizeCategory, route.DistanceNm, pricedAtUtc, airline.ReputationScore);

            if (demand > largest.Seats)
            {
                seatCappedRoutes.Add((route, demand, largest.Seats, largest.Name));
            }
        }

        var suggestions = BuildFleetSuggestions(
            economyConfig, airline, catalogue, ownedTypes, routes, routeAirports, opportunities.Blocked,
            seatCappedRoutes, cashBalance, pricedAtUtc, worldSeed);

        return Results.Ok(new
        {
            cashBalance,
            fleetSize = fleet.Count,
            idleAircraftCount = idle.Count,
            headline = FleetHeadline(fleet.Count, idle.Count, unflyableRoutes.Count, seatCappedRoutes.Count, suggestions.Count),
            utilisation,
            unflyableRoutes,
            seatCappedRoutes = seatCappedRoutes.Select(x => new
            {
                routeId = x.Route.Id,
                x.Route.DepartureIcao,
                x.Route.ArrivalIcao,
                marketDemandPax = x.Demand,
                seats = x.Seats,
                typeName = x.TypeName,
                turnedAwayPerSector = x.Demand - x.Seats,
            }),
            suggestions,
        });
    }

    private static string FleetHeadline(int fleetSize, int idleCount, int unflyableCount, int seatCappedCount, int suggestionCount)
    {
        if (fleetSize == 0)
        {
            return "You have no aircraft yet - lease or buy one from the Fleet page before anything else.";
        }

        if (idleCount > 0)
        {
            var plural = idleCount == 1 ? "aircraft has" : "aircraft have";
            return $"{idleCount} {plural} nothing scheduled. Rostering what you already own earns more than buying another airframe.";
        }

        if (unflyableCount > 0)
        {
            return $"{unflyableCount} of your routes can't be flown by anything you own - that is the gap worth closing first.";
        }

        if (seatCappedCount > 0)
        {
            return $"{seatCappedCount} of your routes turn passengers away for want of seats. A bigger aircraft is the obvious upgrade.";
        }

        return suggestionCount > 0
            ? "Everything you own is working. More capacity is a growth decision now, not a fix."
            : "Everything you own is working, and nothing in the catalogue would open up a route you can't already fly.";
    }

    /// <summary>
    /// What acquiring each catalogue type would change, ranked. Every figure is real: the price comes
    /// from <see cref="EconomyConfig.PurchasePriceFor"/> and <see cref="EconomyConfig.LeaseRateFor"/>
    /// (the only sanctioned pricing paths), and the profit figure is the same
    /// <see cref="SectorProjector"/> projection every other surface uses, run on the best route or
    /// opportunity that type could actually serve.
    /// </summary>
    private static List<object> BuildFleetSuggestions(
        EconomyConfig economyConfig,
        Airline airline,
        IReadOnlyList<AircraftType> catalogue,
        IReadOnlyList<PlanningAircraftType> ownedTypes,
        IReadOnlyList<Route> routes,
        IReadOnlyDictionary<string, Airport> routeAirports,
        IReadOnlyList<BlockedOpportunity> blockedOpportunities,
        IReadOnlyList<(Route Route, int Demand, int Seats, string TypeName)> seatCappedRoutes,
        decimal cashBalance,
        DateTimeOffset pricedAtUtc,
        int worldSeed)
    {
        var ownedTypeIds = ownedTypes.Select(t => t.AircraftTypeId).ToHashSet();
        var scored = new List<(int Unlocks, int ExtraSeats, decimal BestProfit, decimal Price, object Dto)>();

        foreach (var type in catalogue.OrderBy(t => t.IcaoType, StringComparer.Ordinal))
        {
            var planningType = new PlanningAircraftType(
                type.Id, type.IcaoType, type.Name, type.PaxCapacity, type.RangeNm, type.MinRunwayFt, type.MtowTonnes, OwnedCount: 0);

            // Existing routes nothing owned can fly, that this type could.
            var unlockedRoutes = routes
                .Where(r => r.DistanceNm > 0 &&
                            routeAirports.ContainsKey(r.DepartureIcao) && routeAirports.ContainsKey(r.ArrivalIcao))
                .Where(r => !ownedTypes.Any(t => SectorCapability.CanOperate(t, routeAirports[r.DepartureIcao], routeAirports[r.ArrivalIcao], r.DistanceNm)))
                .Where(r => SectorCapability.CanOperate(planningType, routeAirports[r.DepartureIcao], routeAirports[r.ArrivalIcao], r.DistanceNm))
                .ToList();

            var unlockedOpportunities = blockedOpportunities
                .Where(b => SectorCapability.CanOperate(planningType, b.Departure, b.Arrival, b.DistanceNm))
                .ToList();

            // Seats this type would add on routes whose market already exceeds the biggest aircraft
            // available for them - capped at the demand, since seats beyond the market earn nothing.
            var extraSeats = seatCappedRoutes
                .Sum(x => Math.Max(0, Math.Min(x.Demand, planningType.Seats) - x.Seats));

            var unlocks = unlockedRoutes.Count + unlockedOpportunities.Count;
            if (unlocks == 0 && extraSeats == 0)
            {
                continue;
            }

            // The single best sector this type would make possible, priced properly. Existing routes
            // keep their own fare; a new opportunity is priced at the suggested fare, since there is
            // no route yet to have a fare of its own.
            SectorProjection? bestProjection = null;
            string? bestSectorLabel = null;

            foreach (var route in unlockedRoutes)
            {
                var projection = SectorProjector.Project(
                    economyConfig, airline.StrategyProfile, airline.ReputationScore,
                    routeAirports[route.DepartureIcao], routeAirports[route.ArrivalIcao], type,
                    route.DistanceNm, route.BaseFare, pricedAtUtc, worldSeed);

                if (bestProjection is null || projection.NetProfit > bestProjection.NetProfit)
                {
                    bestProjection = projection;
                    bestSectorLabel = $"{route.DepartureIcao}-{route.ArrivalIcao}";
                }
            }

            foreach (var opportunity in unlockedOpportunities)
            {
                var projection = SectorProjector.Project(
                    economyConfig, airline.StrategyProfile, airline.ReputationScore,
                    opportunity.Departure, opportunity.Arrival, type,
                    opportunity.DistanceNm, fare: null, pricedAtUtc, worldSeed);

                if (bestProjection is null || projection.NetProfit > bestProjection.NetProfit)
                {
                    bestProjection = projection;
                    bestSectorLabel = $"{opportunity.Departure.Icao}-{opportunity.Arrival.Icao}";
                }
            }

            foreach (var capped in seatCappedRoutes)
            {
                if (planningType.Seats <= capped.Seats ||
                    !routeAirports.TryGetValue(capped.Route.DepartureIcao, out var dep) ||
                    !routeAirports.TryGetValue(capped.Route.ArrivalIcao, out var arr) ||
                    !SectorCapability.CanOperate(planningType, dep, arr, capped.Route.DistanceNm))
                {
                    continue;
                }

                var projection = SectorProjector.Project(
                    economyConfig, airline.StrategyProfile, airline.ReputationScore, dep, arr, type,
                    capped.Route.DistanceNm, capped.Route.BaseFare, pricedAtUtc, worldSeed);

                if (bestProjection is null || projection.NetProfit > bestProjection.NetProfit)
                {
                    bestProjection = projection;
                    bestSectorLabel = $"{capped.Route.DepartureIcao}-{capped.Route.ArrivalIcao}";
                }
            }

            if (bestProjection is null)
            {
                continue;
            }

            var purchasePrice = economyConfig.PurchasePriceFor(type);
            decimal? monthlyLease = null;
            try
            {
                monthlyLease = economyConfig.LeaseRateFor(type.IcaoType);
            }
            catch (InvalidOperationException)
            {
                // A catalogue type with no configured lease rate can still be bought - saying
                // nothing about leasing is right, refusing to suggest the aircraft is not.
            }

            var leaseDeposit = monthlyLease is decimal rate
                ? Math.Round(rate * (decimal)economyConfig.AirlineStartup.LeaseDepositMonths, 2, MidpointRounding.AwayFromZero)
                : (decimal?)null;

            var dto = new
            {
                aircraftTypeId = type.Id,
                typeName = type.Name,
                icaoType = type.IcaoType,
                seats = type.PaxCapacity,
                rangeNm = type.RangeNm,
                alreadyOwned = ownedTypeIds.Contains(type.Id),
                purchasePrice,
                monthlyLease,
                leaseDeposit,
                monthlyInsurance = economyConfig.FleetFinance.MonthlyInsurancePerAircraft,
                affordableToBuyNow = cashBalance >= purchasePrice,
                affordableToLeaseNow = leaseDeposit is decimal deposit && cashBalance >= deposit,
                unlocksRouteCount = unlockedRoutes.Count,
                unlocksOpportunityCount = unlockedOpportunities.Count,
                extraSeatsOnBusyRoutes = extraSeats,
                bestSector = bestSectorLabel,
                bestSectorProfit = Math.Round(bestProjection.NetProfit, 2),
                reason = FleetSuggestionReason(type, unlockedRoutes.Count, unlockedOpportunities.Count, extraSeats, bestSectorLabel!),
            };

            scored.Add((unlocks, extraSeats, bestProjection.NetProfit, purchasePrice, dto));
        }

        return scored
            .OrderByDescending(s => s.Unlocks)
            .ThenByDescending(s => s.ExtraSeats)
            .ThenByDescending(s => s.BestProfit)
            .ThenBy(s => s.Price)
            .Take(MaxFleetSuggestions)
            .Select(s => s.Dto)
            .ToList();
    }

    /// <summary>
    /// Why this aircraft, in one sentence - and with no money in it, for the same reason
    /// <see cref="OpportunityReason"/> has none: prices and profits are returned as fields and
    /// rendered by the client's currency formatter.
    /// </summary>
    private static string FleetSuggestionReason(
        AircraftType type, int unlockedRoutes, int unlockedOpportunities, int extraSeats, string bestSector)
    {
        var parts = new List<string>();
        if (unlockedRoutes > 0)
        {
            parts.Add(unlockedRoutes == 1 ? "a route you already have but can't fly" : $"{unlockedRoutes} routes you already have but can't fly");
        }

        if (unlockedOpportunities > 0)
        {
            parts.Add(unlockedOpportunities == 1 ? "one city pair currently out of reach" : $"{unlockedOpportunities} city pairs currently out of reach");
        }

        if (extraSeats > 0)
        {
            parts.Add($"{extraSeats} more seats a sector on routes already turning passengers away");
        }

        var opened = parts.Count switch
        {
            0 => "more capacity",
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
        };

        return $"The {type.Name} ({type.PaxCapacity} seats, {type.RangeNm:N0} nm) opens {opened} - " +
               $"its best single sector would be {bestSector}.";
    }

    // ---------------------------------------------------------------------------------------
    // Shared plumbing
    // ---------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<(FleetAircraft Aircraft, AircraftType Type)>> LoadFleetAsync(
        FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var fleet = await db.FleetAircraft.Where(f => f.AirlineId == airlineId).ToListAsync(ct);
        if (fleet.Count == 0)
        {
            return Array.Empty<(FleetAircraft, AircraftType)>();
        }

        var typeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        return fleet
            .Where(f => typesById.ContainsKey(f.AircraftTypeId))
            .OrderBy(f => f.Registration, StringComparer.OrdinalIgnoreCase)
            .Select(f => (f, typesById[f.AircraftTypeId]))
            .ToList();
    }

    private static IReadOnlyList<PlanningAircraftType> ToPlanningTypes(IReadOnlyList<(FleetAircraft Aircraft, AircraftType Type)> fleet) =>
        fleet
            .GroupBy(f => f.Type.Id)
            .Select(g =>
            {
                var type = g.First().Type;
                return new PlanningAircraftType(
                    type.Id, type.IcaoType, type.Name, type.PaxCapacity, type.RangeNm, type.MinRunwayFt, type.MtowTonnes, g.Count());
            })
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static PlanningAircraftType ToPlanningType(AircraftType type, IReadOnlyList<PlanningAircraftType> owned) =>
        owned.FirstOrDefault(t => t.AircraftTypeId == type.Id) ??
        new PlanningAircraftType(type.Id, type.IcaoType, type.Name, type.PaxCapacity, type.RangeNm, type.MinRunwayFt, type.MtowTonnes, OwnedCount: 0);

    /// <summary>A city pair regardless of direction - EGGD/EGPH and EGPH/EGGD are one pair, since a
    /// route is always created as a there-and-back pair anyway.</summary>
    private static string UnorderedPairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
}

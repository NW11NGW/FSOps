using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Core.SimAircraft;

namespace FSOps.Core.Contracts;

/// <summary>Everything generation needs, and nothing it does not. Pure data - no database, no clock.</summary>
/// <param name="WorldSeed">The world seed, exactly as the fuel-price walk uses it.</param>
/// <param name="AirlineId">Whose board this is. Two airlines in the same world see different jobs.</param>
/// <param name="Bucket">Which board period - see <see cref="ContractBoardKey"/>.</param>
/// <param name="GeneratedUtc">The moment the board came into being. Stamps offer and deadline dates.</param>
/// <param name="Origins">
/// Airports the airline already touches. <b>Contracts start where the player's network reaches and
/// end anywhere</b> - that asymmetry is what makes a transatlantic ferry possible while keeping the
/// board grounded in the network they actually built.
/// </param>
/// <param name="Candidates">
/// Every airport a job may end at or route through. The whole world is fair game here; that is the
/// point.
/// </param>
/// <param name="Aircraft">
/// Only the aircraft the player can actually load - see
/// <see cref="ContractAircraftAvailabilityResolver"/>. A contract naming an aeroplane that is not in
/// their hangar is worse than no contract at all, so this list is a hard gate rather than a
/// preference.
/// </param>
public sealed record ContractBoardRequest(
    int WorldSeed,
    Guid AirlineId,
    long Bucket,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<ContractAirport> Origins,
    IReadOnlyList<ContractAirport> Candidates,
    IReadOnlyList<ContractAircraft> Aircraft);

/// <summary>One generated job, before it becomes a <see cref="Contract"/> row.</summary>
public sealed record GeneratedContract(
    int Slot,
    ContractKind Kind,
    string OperatorName,
    ContractAircraft Aircraft,
    string LoadDescription,
    int PayloadKg,
    int PaxCount,
    decimal Fee,
    double TotalDistanceNm,
    int TotalPlannedBlockMinutes,
    DateTimeOffset OfferedUtc,
    DateTimeOffset DeadlineUtc,
    IReadOnlyList<ContractLegPlan> Legs,
    IReadOnlyList<decimal> FeeShares);

/// <summary>
/// Why a board is smaller than it should be, in terms the player can act on.
///
/// <para><b>A thin board must say so.</b> Silently producing three jobs where there should be eight
/// looks exactly like a broken feature, and the player has no way to tell the difference between
/// "there is not much on today" and "FSOps cannot find anything you can fly". Both of the real causes
/// - too few aircraft ticked, or an airline that barely touches anywhere yet - are one click from
/// being fixed, so both are worth naming.</para>
/// </summary>
/// <param name="Message">Null when the board came out full. Otherwise the sentence to show.</param>
public sealed record ContractBoardLimitation(
    int AvailableAircraftCount,
    int OriginCount,
    int Requested,
    int Generated,
    string? Message);

/// <summary>The board, and an honest account of itself.</summary>
public sealed record ContractBoard(
    long Bucket,
    IReadOnlyList<GeneratedContract> Contracts,
    ContractBoardLimitation Limitation);

/// <summary>
/// Which board period a moment falls in. The bucket is both the refresh schedule and the generation
/// seed, so "the board refreshes every day" and "the board is deterministic" are one mechanism rather
/// than two that could drift apart.
/// </summary>
public static class ContractBoardKey
{
    public static long BucketFor(DateTimeOffset utc, int refreshHours)
    {
        var hours = Math.Max(1, refreshHours);
        var elapsedHours = (long)Math.Floor((utc.UtcDateTime - DateTime.UnixEpoch).TotalHours);
        return elapsedHours / hours;
    }

    /// <summary>When the given bucket started - what a contract generated in it is stamped as offered at.</summary>
    public static DateTimeOffset StartOf(long bucket, int refreshHours) =>
        new(DateTime.UnixEpoch.AddHours(bucket * Math.Max(1, refreshHours)), TimeSpan.Zero);
}

/// <summary>
/// Builds the board of jobs other operators are offering.
///
/// <para><b>Random in outcome, deterministic in mechanism.</b> Every choice here is drawn from
/// <see cref="ContractRandom"/>, seeded from the world seed, the airline and the board bucket -
/// exactly as the rest of the economy is seeded. The same airline, in the same state, in the same
/// period, gets the same board every time it looks. A board that answered differently on every
/// refresh could not be tested, and would feel arbitrary to play: the player would learn to keep
/// reloading until something good appeared, which is not browsing a board, it is pulling a lever.
/// </para>
///
/// <para><b>The scale is randomised, not just the endpoints.</b> A band is drawn first and the
/// destination is then found to fit it, so a board carries a genuine spread - a forty-minute domestic
/// hop next to a multi-leg ocean crossing - rather than eight variations on the same size of job. The
/// fee follows the work, so the spread means something.</para>
///
/// <para><b>Nothing reaches the board that cannot be flown.</b> Aircraft come only from what the
/// player can actually load, and every leg is checked against that aircraft's range before the job
/// exists (see <see cref="ContractLegChainBuilder"/>). A slot that cannot be filled honestly is left
/// empty and reported, never filled with something plausible-looking.</para>
///
/// <para>Pure: no clock, no database, no I/O. The caller supplies the moment and the airports.</para>
/// </summary>
public static class ContractGenerator
{
    /// <summary>
    /// How many times a single slot may re-draw before being left empty. Bounded so an awkward
    /// combination (a short-legged aircraft, a remote origin) costs a fixed amount of work rather
    /// than searching until it finds something.
    /// </summary>
    private const int AttemptsPerSlot = 8;

    /// <summary>
    /// Runway minimums by category, in feet. A per-type figure would be better and
    /// <see cref="ContractAircraft"/> does not carry one; adding a field would mean revisiting every
    /// entry in the catalogue and getting forty numbers right, for a check whose only job is to stop
    /// something obviously silly - a 737 job into a 2,000 ft grass strip. Category level is honest
    /// about the precision it has, and errs long: offering slightly fewer airports is a far cheaper
    /// mistake than offering one the aircraft cannot use.
    /// </summary>
    private static int MinimumRunwayFt(ContractAircraftCategory category) => category switch
    {
        ContractAircraftCategory.LightSingle => 1_800,
        ContractAircraftCategory.LightTwin => 2_500,
        ContractAircraftCategory.UtilityTurboprop => 2_500,
        ContractAircraftCategory.BusinessJet => 4_000,
        ContractAircraftCategory.RegionalAirliner => 4_500,
        ContractAircraftCategory.Narrowbody => 6_000,
        ContractAircraftCategory.Widebody => 8_000,
        _ => 4_000,
    };

    public static ContractBoard Generate(ContractConfig config, ContractBoardRequest request)
    {
        var requested = Math.Max(0, config.BoardSize);
        var flyable = request.Aircraft.Where(a => a.RangeNm > 0 && a.CruiseTasKts > 0).ToList();
        var origins = request.Origins.ToList();

        if (requested == 0 || flyable.Count == 0 || origins.Count == 0)
        {
            return new ContractBoard(
                request.Bucket,
                Array.Empty<GeneratedContract>(),
                Describe(flyable.Count, origins.Count, requested, generated: 0));
        }

        // Distances from each origin, computed once. Origins are few and candidates are many, so
        // doing this per attempt instead would repeat the same scan a dozen times per board.
        var reachFromOrigin = new Dictionary<string, List<(ContractAirport Airport, double DistanceNm)>>(StringComparer.OrdinalIgnoreCase);

        var contracts = new List<GeneratedContract>(requested);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offeredUtc = ContractBoardKey.StartOf(request.Bucket, config.BoardRefreshHours);
        var deadlineUtc = offeredUtc.AddDays(Math.Max(1, config.DeadlineDays));

        for (var slot = 0; slot < requested; slot++)
        {
            var rng = ContractRandom.For(request.WorldSeed, request.AirlineId, request.Bucket, $"slot-{slot}");
            var generated = TryFillSlot(config, request, slot, flyable, origins, reachFromOrigin, used, offeredUtc, deadlineUtc, ref rng);
            if (generated is not null)
            {
                contracts.Add(generated);
            }
        }

        return new ContractBoard(request.Bucket, contracts, Describe(flyable.Count, origins.Count, requested, contracts.Count));
    }

    private static GeneratedContract? TryFillSlot(
        ContractConfig config,
        ContractBoardRequest request,
        int slot,
        IReadOnlyList<ContractAircraft> flyable,
        IReadOnlyList<ContractAirport> origins,
        Dictionary<string, List<(ContractAirport Airport, double DistanceNm)>> reachFromOrigin,
        HashSet<string> used,
        DateTimeOffset offeredUtc,
        DateTimeOffset deadlineUtc,
        ref ContractRandom rng)
    {
        for (var attempt = 0; attempt < AttemptsPerSlot; attempt++)
        {
            var kind = PickKind(ref rng);
            var aircraft = PickAircraft(flyable, kind, ref rng);
            if (aircraft is null)
            {
                continue;
            }

            var band = rng.PickWeighted(config.ScaleBands, b => b.Weight);
            if (band is null)
            {
                continue;
            }

            var origin = rng.Pick(origins);
            if (origin is null)
            {
                continue;
            }

            var targetNm = rng.NextDouble(band.MinDistanceNm, band.MaxDistanceNm);
            var minRunwayFt = MinimumRunwayFt(aircraft.Category);

            var reach = ReachFrom(origin, request.Candidates, reachFromOrigin);
            var tolerance = Math.Max(0.05, config.DestinationDistanceTolerance);

            var destinations = reach
                .Where(x => x.Airport.LongestRunwayFt >= minRunwayFt)
                .Where(x => x.DistanceNm >= targetNm * (1.0 - tolerance) && x.DistanceNm <= targetNm * (1.0 + tolerance))
                .Select(x => x.Airport)
                .ToList();

            if (destinations.Count == 0)
            {
                continue;
            }

            var destination = rng.PickWeighted(destinations, a => DestinationWeight(kind, a));
            if (destination is null)
            {
                continue;
            }

            var key = $"{origin.Icao}>{destination.Icao}:{aircraft.TypeDesignator}";
            if (!used.Add(key))
            {
                continue;
            }

            var legs = ContractLegChainBuilder.Build(
                origin,
                destination,
                RouteRangeAssessor.OperationalRangeNm(aircraft.RangeNm),
                minRunwayFt,
                aircraft.CruiseTasKts,
                request.Candidates);

            if (legs is null || legs.Count == 0)
            {
                // The pair could not be joined up in this aeroplane. Release the key so a later slot
                // may try the same endpoints in something with the legs for it.
                used.Remove(key);
                continue;
            }

            var (payloadKg, paxCount, loadDescription) = DescribeLoad(kind, aircraft, ref rng);
            var totalDistanceNm = legs.Sum(l => l.DistanceNm);
            var totalBlockMinutes = legs.Sum(l => l.PlannedBlockMinutes);

            var fee = ContractPayCalculator.CalculateFee(
                config, kind, aircraft, totalDistanceNm, legs.Count, payloadKg, paxCount);
            var shares = ContractPayCalculator.AllocateFeeShares(fee, legs.Select(l => l.PlannedBlockMinutes).ToList());

            return new GeneratedContract(
                slot,
                kind,
                OperatorNames.Pick(kind, ref rng),
                aircraft,
                loadDescription,
                payloadKg,
                paxCount,
                fee,
                totalDistanceNm,
                totalBlockMinutes,
                offeredUtc,
                deadlineUtc,
                legs,
                shares);
        }

        return null;
    }

    private static List<(ContractAirport Airport, double DistanceNm)> ReachFrom(
        ContractAirport origin,
        IReadOnlyList<ContractAirport> candidates,
        Dictionary<string, List<(ContractAirport Airport, double DistanceNm)>> cache)
    {
        if (cache.TryGetValue(origin.Icao, out var cached))
        {
            return cached;
        }

        var reach = candidates
            .Where(a => !string.Equals(a.Icao, origin.Icao, StringComparison.OrdinalIgnoreCase))
            .Select(a => (Airport: a, DistanceNm: GreatCircle.DistanceNm(origin.Latitude, origin.Longitude, a.Latitude, a.Longitude)))
            // Ordered so the candidate list handed to the weighted pick is itself deterministic,
            // independent of whatever order the caller loaded airports in.
            .OrderBy(x => x.Airport.Icao, StringComparer.Ordinal)
            .ToList();

        cache[origin.Icao] = reach;
        return reach;
    }

    /// <summary>
    /// Ferry is the most common because it is the most distinctive thing on offer and the reason the
    /// feature exists; the other two exist so the board is not all one shape.
    /// </summary>
    private static ContractKind PickKind(ref ContractRandom rng)
    {
        var roll = rng.NextUnit();
        return roll switch
        {
            < 0.45 => ContractKind.Ferry,
            < 0.75 => ContractKind.Cargo,
            _ => ContractKind.Charter,
        };
    }

    /// <summary>
    /// Which aeroplanes make sense for a kind. A charter needs seats; cargo needs somewhere to put
    /// the freight. A ferry can be anything, because moving an aeroplane is a job regardless of what
    /// it normally carries - and that is exactly what makes "transatlantic in a Cessna" reachable.
    /// </summary>
    private static ContractAircraft? PickAircraft(IReadOnlyList<ContractAircraft> flyable, ContractKind kind, ref ContractRandom rng)
    {
        var eligible = kind switch
        {
            ContractKind.Charter => flyable.Where(a => a.Seats > 0).ToList(),
            ContractKind.Cargo => flyable.Where(a => a.PayloadKg > 0).ToList(),
            _ => flyable.ToList(),
        };

        return rng.Pick(eligible);
    }

    private static double DestinationWeight(ContractKind kind, ContractAirport airport) => kind switch
    {
        // Passengers are going somewhere people go.
        ContractKind.Charter => airport.SizeCategory switch
        {
            AirportSizeCategory.Large => 3.0,
            AirportSizeCategory.Medium => 2.5,
            AirportSizeCategory.Small => 1.0,
            _ => 0.0,
        },
        // Freight is as likely to be going somewhere awkward as somewhere convenient - often more so,
        // which is half of why cargo jobs are interesting to fly.
        ContractKind.Cargo => airport.SizeCategory switch
        {
            AirportSizeCategory.Large => 1.5,
            AirportSizeCategory.Medium => 2.0,
            AirportSizeCategory.Small => 1.5,
            _ => 0.0,
        },
        // A ferry ends wherever the buyer is, which is genuinely anywhere.
        _ => airport.SizeCategory switch
        {
            AirportSizeCategory.Large => 1.5,
            AirportSizeCategory.Medium => 2.0,
            AirportSizeCategory.Small => 1.8,
            _ => 0.0,
        },
    };

    private static (int PayloadKg, int PaxCount, string Description) DescribeLoad(
        ContractKind kind, ContractAircraft aircraft, ref ContractRandom rng)
    {
        switch (kind)
        {
            case ContractKind.Cargo:
            {
                // Never the full useful load: that figure is everything-but-fuel, and a job that
                // filled it would leave nothing to fly on. Sixty to ninety-five per cent is a load
                // that fits with tanks in it.
                var payloadKg = (int)Math.Round(aircraft.PayloadKg * rng.NextDouble(0.60, 0.95));
                payloadKg = Math.Max(1, payloadKg);
                return (payloadKg, 0, $"{payloadKg:N0} kg of {rng.Pick(CargoTypes) ?? "general freight"}");
            }

            case ContractKind.Charter:
            {
                var paxCount = Math.Max(1, (int)Math.Round(aircraft.Seats * rng.NextDouble(0.45, 1.0)));
                var word = paxCount == 1 ? "passenger" : "passengers";
                return (0, paxCount, $"{paxCount} {word}");
            }

            default:
                // A ferry carries nothing. The aeroplane IS the cargo, which is the whole idea.
                return (0, 0, $"Positioning flight - {aircraft.Name}, empty");
        }
    }

    private static readonly string[] CargoTypes =
    [
        "general freight", "machine parts", "medical supplies", "mail and parcels", "perishables",
        "aircraft spares", "electronics", "laboratory samples", "oilfield equipment", "fresh seafood",
    ];

    private static ContractBoardLimitation Describe(int aircraftCount, int originCount, int requested, int generated)
    {
        string? message = null;

        if (originCount == 0)
        {
            message = "Your airline does not fly anywhere yet. Contracts start from airports you already touch, " +
                      "so add a route - or move an aircraft somewhere - and jobs will start appearing.";
        }
        else if (aircraftCount == 0)
        {
            message = "No aircraft are marked as available for contract work. Open Settings, tell FSOps which " +
                      "edition of the simulator you have, and tick anything else you own - the board fills from " +
                      "that list and nothing else.";
        }
        else if (generated < requested)
        {
            var aircraftWord = aircraftCount == 1 ? "aircraft is" : "aircraft are";
            message = $"Only {generated} of {requested} jobs could be offered. {aircraftCount} {aircraftWord} " +
                      "available for contract work, and every leg of every job has to be within range of the " +
                      "aircraft it names - so a short list, or an airline that only touches a few airports, " +
                      "makes for a thinner board. Ticking more aircraft in Settings is the quickest fix.";
        }

        return new ContractBoardLimitation(aircraftCount, originCount, requested, generated, message);
    }
}

/// <summary>
/// The other businesses. Pure flavour - nothing keys off a name - but a job from "Northgate Freight"
/// reads as a job and a job from "Operator 4" reads as a placeholder, and the whole feature is about
/// the flying feeling like something.
/// </summary>
internal static class OperatorNames
{
    private static readonly string[] Prefixes =
    [
        "Northgate", "Meridian", "Kestrel", "Harbour", "Ardent", "Fairwind", "Blackwater", "Summit",
        "Loganair", "Caledon", "Westbrook", "Pinehurst", "Corvus", "Redpoint", "Stonebridge", "Halcyon",
    ];

    private static readonly string[] FerrySuffixes =
    [
        "Aircraft Sales", "Aviation Group", "Aircraft Leasing", "Aero Trading", "Aircraft Brokers",
    ];

    private static readonly string[] CargoSuffixes =
    [
        "Freight", "Air Cargo", "Logistics", "Air Freight", "Express Parcels",
    ];

    private static readonly string[] CharterSuffixes =
    [
        "Executive Travel", "Air Charter", "Private Aviation", "Jet Services", "Charter Group",
    ];

    public static string Pick(ContractKind kind, ref ContractRandom rng)
    {
        var prefix = rng.Pick(Prefixes) ?? "Northgate";
        var suffixes = kind switch
        {
            ContractKind.Cargo => CargoSuffixes,
            ContractKind.Charter => CharterSuffixes,
            _ => FerrySuffixes,
        };

        return $"{prefix} {rng.Pick(suffixes) ?? suffixes[0]}";
    }
}

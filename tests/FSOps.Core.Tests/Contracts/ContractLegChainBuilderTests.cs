using FSOps.Core.Contracts;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Contracts;

/// <summary>
/// The chain builder, and the property everything else rests on: <b>every leg it returns is within
/// range of the aircraft the contract names</b>.
///
/// <para>The North Atlantic fixture below is the real one - Bristol, Wick, Reykjavik, Narsarsuaq,
/// Goose Bay, and airports either side - because the interesting claim is not that a greedy walk
/// works on made-up geography, it is that it produces the crossing the plan describes out of the
/// actual airports the bundled world data has.</para>
/// </summary>
public class ContractLegChainBuilderTests
{
    private static ContractAirport Airport(string icao, double lat, double lon, int runwayFt = 6_000,
        AirportSizeCategory size = AirportSizeCategory.Medium) =>
        new(icao, icao, icao, "XX", lat, lon, runwayFt, size);

    /// <summary>Real positions, to a tenth of a degree - enough for a great-circle distance to be honest.</summary>
    private static readonly ContractAirport Bristol = Airport("EGGD", 51.383, -2.719, 8_000);
    private static readonly ContractAirport Wick = Airport("EGPC", 58.459, -3.093, 5_900, AirportSizeCategory.Small);
    private static readonly ContractAirport Stornoway = Airport("EGPO", 58.216, -6.331, 7_200, AirportSizeCategory.Small);
    private static readonly ContractAirport Vagar = Airport("EKVG", 62.064, -7.277, 5_910, AirportSizeCategory.Small);
    private static readonly ContractAirport Reykjavik = Airport("BIRK", 64.130, -21.941, 6_120);
    private static readonly ContractAirport Keflavik = Airport("BIKF", 63.985, -22.605, 10_000);
    private static readonly ContractAirport Kulusuk = Airport("BGKK", 65.574, -37.123, 3_900, AirportSizeCategory.Small);
    private static readonly ContractAirport Narsarsuaq = Airport("BGBW", 61.160, -45.426, 6_004, AirportSizeCategory.Small);
    private static readonly ContractAirport Nuuk = Airport("BGGH", 64.191, -51.678, 3_117, AirportSizeCategory.Small);
    private static readonly ContractAirport Iqaluit = Airport("CYFB", 63.756, -68.556, 8_605, AirportSizeCategory.Small);
    private static readonly ContractAirport Kuujjuaq = Airport("CYVP", 58.096, -68.427, 6_000, AirportSizeCategory.Small);
    private static readonly ContractAirport GooseBay = Airport("CYYR", 53.319, -60.426, 11_051);
    private static readonly ContractAirport Gander = Airport("CYQX", 48.937, -54.568, 10_500);
    private static readonly ContractAirport Halifax = Airport("CYHZ", 44.881, -63.509, 10_500);
    private static readonly ContractAirport NewYork = Airport("KJFK", 40.640, -73.779, 14_511, AirportSizeCategory.Large);

    private static IReadOnlyList<ContractAirport> NorthAtlantic() => new[]
    {
        Bristol, Wick, Stornoway, Vagar, Reykjavik, Keflavik, Kulusuk, Narsarsuaq, Nuuk, Iqaluit, Kuujjuaq,
        GooseBay, Gander, Halifax, NewYork,
    };

    /// <summary>
    /// <b>The one that matters.</b> A Cessna 172 - 640 nm published, 544 nm once derated - asked to
    /// take an aeroplane from Bristol to New York. Nothing in the builder knows what a ferry is or
    /// that the North Atlantic exists; it simply cannot hop further than 544 nm, and the only places
    /// to stop are the ones that are there. The expedition falls out of the arithmetic.
    ///
    /// <para><b>Worth knowing before reading the assertions:</b> this does NOT come out as the
    /// shorthand chain the plan uses as an illustration (Bristol, Wick, Reykjavik, Narsarsuaq, Goose
    /// Bay, New York). Narsarsuaq to Goose Bay is 675 nm - well beyond a 172 - so the route the
    /// arithmetic finds runs up Greenland's west coast through Nuuk and across Baffin Island via
    /// Iqaluit and Kuujjuaq, which is the way light aircraft have actually made this crossing. The
    /// plan's version is a good illustration and a bad flight plan, and the generator refusing to
    /// offer an impossible hop is precisely the promise being tested here.</para>
    /// </summary>
    [Fact]
    public void ALightSingleCrossingTheAtlantic_BecomesManyShortLegsByItself()
    {
        var legs = ContractLegChainBuilder.Build(
            Bristol, NewYork,
            RouteRangeAssessor.OperationalRangeNm(640),
            minRunwayFt: 1_800,
            cruiseTasKts: 122,
            NorthAtlantic());

        Assert.NotNull(legs);
        Assert.True(legs!.Count >= 5, $"Expected a real chain of stops, got {legs.Count} leg(s).");

        Assert.Equal("EGGD", legs[0].Departure.Icao);
        Assert.Equal("KJFK", legs[^1].Arrival.Icao);

        // Every hop inside the aeroplane's actual reach - the promise the board makes.
        Assert.All(legs, l => Assert.True(
            l.DistanceNm <= RouteRangeAssessor.OperationalRangeNm(640),
            $"{l.Departure.Icao}-{l.Arrival.Icao} is {l.DistanceNm:F0} nm, beyond a 172's 544 nm operational range."));

        // Contiguous: each leg starts where the last one ended, so the aeroplane is always where the
        // next sector expects to find it.
        for (var i = 1; i < legs.Count; i++)
        {
            Assert.Equal(legs[i - 1].Arrival.Icao, legs[i].Departure.Icao);
        }

        Assert.Equal(Enumerable.Range(1, legs.Count), legs.Select(l => l.Sequence));

        // And it really does island-hop, rather than arriving by magic: the chain has to pass through
        // Greenland, which is the only way across at this range.
        var chain = string.Join(" > ", legs.Select(l => l.Departure.Icao).Append(legs[^1].Arrival.Icao));
        Assert.True(
            legs.Any(l => l.Arrival.Icao.StartsWith("BG", StringComparison.Ordinal)),
            $"The crossing never touched Greenland: {chain}");
        Assert.True(
            legs.Any(l => l.Arrival.Icao.StartsWith("BI", StringComparison.Ordinal)),
            $"The crossing never touched Iceland: {chain}");
    }

    /// <summary>
    /// The mirror case, and the reason leg count is never decided by the kind of job: the same city
    /// pair in something with the legs for it is one sector.
    /// </summary>
    [Fact]
    public void TheSameCityPairInANarrowbody_IsASingleLeg()
    {
        var legs = ContractLegChainBuilder.Build(
            Bristol, NewYork,
            RouteRangeAssessor.OperationalRangeNm(4_000),
            minRunwayFt: 6_000,
            cruiseTasKts: 450,
            NorthAtlantic());

        Assert.NotNull(legs);
        var leg = Assert.Single(legs!);
        Assert.Equal("EGGD", leg.Departure.Icao);
        Assert.Equal("KJFK", leg.Arrival.Icao);
        Assert.True(leg.PlannedBlockMinutes > 300, "A transatlantic sector should be a long block time.");
    }

    /// <summary>
    /// A journey that cannot be made in this aeroplane through these airports returns null - and null
    /// is an ordinary answer, not an error. The generator's response is simply not to offer that job,
    /// which is the honest alternative to putting an unflyable leg on the board.
    /// </summary>
    [Fact]
    public void AnUnreachableDestination_ReturnsNullRatherThanAnImpossibleLeg()
    {
        // Bristol to New York with only the two endpoints available and a 300 nm aeroplane.
        var legs = ContractLegChainBuilder.Build(
            Bristol, NewYork,
            RouteRangeAssessor.OperationalRangeNm(300),
            minRunwayFt: 1_800,
            cruiseTasKts: 120,
            new[] { Bristol, NewYork });

        Assert.Null(legs);
    }

    /// <summary>
    /// Runway suitability filters the intermediate stops. A jet needing 6,000 ft must not be routed
    /// through a 3,900 ft strip just because it is conveniently placed.
    /// </summary>
    [Fact]
    public void IntermediateStops_RespectTheAircraftsRunwayRequirement()
    {
        var legs = ContractLegChainBuilder.Build(
            Bristol, NewYork,
            RouteRangeAssessor.OperationalRangeNm(1_800),
            minRunwayFt: 6_000,
            cruiseTasKts: 400,
            NorthAtlantic());

        Assert.NotNull(legs);
        Assert.All(legs!, l => Assert.True(
            l.Arrival.LongestRunwayFt >= 6_000 || l.Arrival.Icao == "KJFK",
            $"Routed through {l.Arrival.Icao}, which has only {l.Arrival.LongestRunwayFt} ft."));

        Assert.DoesNotContain(legs!, l => l.Arrival.Icao == "BGKK");
    }

    /// <summary>
    /// Deterministic: the same request always produces the same chain, whatever order the caller
    /// happened to hand the airports over in. A board that reshuffled its stops between two reads of
    /// the same job would be unusable, and untestable.
    /// </summary>
    [Fact]
    public void TheSameRequest_AlwaysProducesTheSameChain_WhateverOrderTheAirportsArriveIn()
    {
        var forwards = NorthAtlantic();
        var backwards = NorthAtlantic().Reverse().ToList();

        var first = ContractLegChainBuilder.Build(Bristol, NewYork, RouteRangeAssessor.OperationalRangeNm(640), 1_800, 122, forwards);
        var second = ContractLegChainBuilder.Build(Bristol, NewYork, RouteRangeAssessor.OperationalRangeNm(640), 1_800, 122, backwards);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(
            first!.Select(l => $"{l.Departure.Icao}-{l.Arrival.Icao}"),
            second!.Select(l => $"{l.Departure.Icao}-{l.Arrival.Icao}"));
    }

    [Fact]
    public void AJourneyToItsOwnOrigin_IsRefused()
    {
        Assert.Null(ContractLegChainBuilder.Build(Bristol, Bristol, 500, 1_800, 120, NorthAtlantic()));
    }

    /// <summary>Block time comes from the same estimator routes use, so a contract leg and an
    /// identical route leg can never claim different durations.</summary>
    [Fact]
    public void LegBlockTime_MatchesTheEstimatorRoutesUse()
    {
        var legs = ContractLegChainBuilder.Build(Bristol, Reykjavik, RouteRangeAssessor.OperationalRangeNm(4_000), 5_000, 450, NorthAtlantic());

        var leg = Assert.Single(legs!);
        Assert.Equal(BlockTimeEstimator.Estimate(leg.DistanceNm, 450).TotalMinutes, leg.PlannedBlockMinutes);
    }
}

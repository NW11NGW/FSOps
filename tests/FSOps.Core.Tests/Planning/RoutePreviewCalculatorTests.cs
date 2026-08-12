using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

public class RoutePreviewCalculatorTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();

    // Bristol (GB) <-> JFK (US): genuinely international, and far enough apart that it's never
    // mistaken for the short-hop case below.
    private static readonly Airport Bristol = new()
    {
        Icao = "EGGD", Name = "Bristol Airport", Country = "GB",
        Latitude = 51.3827, Longitude = -2.7191, LongestRunwayFt = 8000,
    };

    private static readonly Airport Jfk = new()
    {
        Icao = "KJFK", Name = "John F Kennedy International", Country = "US",
        Latitude = 40.6413, Longitude = -73.7781, LongestRunwayFt = 14511,
    };

    // A same-country airport ~90 nm north of Bristol (real Bristol<->Edinburgh is over 300 nm,
    // well past the short-hop threshold, so this is a synthetic pair rather than a real one) -
    // domestic and comfortably under RoutePreviewCalculator's 200 nm short-hop threshold.
    private static readonly Airport NearbyDomestic = new()
    {
        Icao = "EGXX", Name = "Nearby Test Airfield", Country = "GB",
        Latitude = 52.8827, Longitude = -2.7191, LongestRunwayFt = 6000,
    };

    private static readonly AircraftType A320 = new()
    {
        Id = Guid.NewGuid(), IcaoType = "A320", Family = "A320", Name = "A320neo",
        RangeNm = 3400, CruiseTasKts = 450, FuelBurnKgPerHour = 2400,
        MinRunwayFt = 5500, ServiceCeilingFt = 39000,
    };

    [Fact]
    public void Calculate_Balanced_InternationalSector_RaisesNoSuitabilityWarning()
    {
        var result = RoutePreviewCalculator.Calculate(Config, Bristol, Jfk, A320, AirlineStrategyProfile.Balanced);

        Assert.DoesNotContain(result.Validation.Warnings, w => w.Contains("strategy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Calculate_Balanced_ShortDomesticHop_RaisesNoSuitabilityWarning()
    {
        var result = RoutePreviewCalculator.Calculate(Config, Bristol, NearbyDomestic, A320, AirlineStrategyProfile.Balanced);

        Assert.DoesNotContain(result.Validation.Warnings, w => w.Contains("strategy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Calculate_Domestic_InternationalSector_RaisesSuitabilityWarning()
    {
        var result = RoutePreviewCalculator.Calculate(Config, Bristol, Jfk, A320, AirlineStrategyProfile.Domestic);

        Assert.Contains(result.Validation.Warnings, w => w.Contains("Domestic strategy", StringComparison.Ordinal));
    }

    [Fact]
    public void Calculate_International_ShortDomesticHop_RaisesSuitabilityWarning()
    {
        var result = RoutePreviewCalculator.Calculate(Config, Bristol, NearbyDomestic, A320, AirlineStrategyProfile.International);

        Assert.Contains(result.Validation.Warnings, w => w.Contains("International strategy", StringComparison.Ordinal));
    }

    /// <summary>Every profile that isn't Domestic or International raises neither directional
    /// advisory - derived from Enum.GetValues so a new profile is covered automatically, per the
    /// plan's rule that profile-iterating tests must never hardcode the list.</summary>
    [Theory]
    [MemberData(nameof(NonDirectionalProfiles))]
    public void AdvisoryRulesFor_NonDirectionalProfiles_WarnOnNothing(AirlineStrategyProfile profile)
    {
        var rules = RoutePreviewCalculator.AdvisoryRulesFor(profile);

        Assert.False(rules.WarnsOnInternationalSector);
        Assert.False(rules.WarnsOnShortDomesticHop);
    }

    public static IEnumerable<object[]> NonDirectionalProfiles() =>
        Enum.GetValues<AirlineStrategyProfile>()
            .Where(p => p is not AirlineStrategyProfile.Domestic and not AirlineStrategyProfile.International)
            .Select(p => new object[] { p });

    [Fact]
    public void Calculate_EveryProfile_StillRaisesRangeWarning_WhenBeyondOperationalRange()
    {
        // Range and runway warnings are physical limits, not strategy preferences - they must
        // fire for every profile, Balanced included. A hand-rolled aircraft with a tiny range
        // makes Bristol->JFK exceed it regardless of strategy.
        var shortRangeAircraft = new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = "TEST", Family = "TEST", Name = "Short-range test type",
            RangeNm = 500, CruiseTasKts = 450, FuelBurnKgPerHour = 2400,
            MinRunwayFt = 5500, ServiceCeilingFt = 39000,
        };

        // The verdict is now about the FLEET rather than about whichever type these figures happened
        // to be planned with (see RouteRangeAssessor), so the fleet is what makes the sector
        // unflyable here: one short-legged airframe and nothing else.
        var fleet = new[] { new RangeCandidateAircraft("G-TINY", shortRangeAircraft.Name, shortRangeAircraft.RangeNm, ReservedForPlayer: true) };

        foreach (var profile in Enum.GetValues<AirlineStrategyProfile>())
        {
            var result = RoutePreviewCalculator.Calculate(Config, Bristol, Jfk, shortRangeAircraft, profile, fleet);
            Assert.False(result.Validation.WithinRange);
            Assert.Equal(RouteRangeVerdict.BeyondFleet, result.Validation.Range.Verdict);
            Assert.True(result.Validation.Range.Blocking);
            Assert.Contains(result.Validation.Warnings, w => w.Contains("beyond every aircraft in your fleet", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Calculate_WithNoFleetPassed_SaysNothingAboutRange()
    {
        // The callers that only want block time or fuel out of this (the Fly screen's per-aircraft
        // estimate, the scheduler's block-minutes cache) pass no fleet, and must never be handed a
        // range verdict derived from one arbitrary type - that misattribution is the whole defect.
        var shortRangeAircraft = new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = "TEST", Family = "TEST", Name = "Short-range test type",
            RangeNm = 500, CruiseTasKts = 450, FuelBurnKgPerHour = 2400,
            MinRunwayFt = 5500, ServiceCeilingFt = 39000,
        };

        var result = RoutePreviewCalculator.Calculate(Config, Bristol, Jfk, shortRangeAircraft, AirlineStrategyProfile.Balanced);

        Assert.Equal(RouteRangeVerdict.NotAssessed, result.Validation.Range.Verdict);
        Assert.False(result.Validation.Range.Blocking);
        Assert.DoesNotContain(result.Validation.Warnings, w => w.Contains("range", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Calculate_WithAnEmptyFleet_SaysWhatTheFiguresAssume_AndDoesNotBlock()
    {
        var result = RoutePreviewCalculator.Calculate(
            Config, Bristol, Jfk, A320, AirlineStrategyProfile.Balanced, Array.Empty<RangeCandidateAircraft>());

        Assert.False(result.Validation.Range.Blocking);
        Assert.Contains(result.Validation.Warnings, w => w.Contains("you have no aircraft yet", StringComparison.OrdinalIgnoreCase));
        // Never phrased as a limit of the airline's, because there is no fleet for it to be one of.
        Assert.DoesNotContain(result.Validation.Warnings, w => w.Contains("beyond every aircraft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Calculate_WhenAReservedAircraftCanFlyIt_WarnsAboutNothing()
    {
        var fleet = new[]
        {
            new RangeCandidateAircraft("G-SHRT", "Short-range test type", RangeNm: 500, ReservedForPlayer: false),
            new RangeCandidateAircraft("G-LONG", "Boeing 787-9 Dreamliner", RangeNm: 7635, ReservedForPlayer: true),
        };

        var result = RoutePreviewCalculator.Calculate(Config, Bristol, Jfk, A320, AirlineStrategyProfile.Balanced, fleet);

        Assert.Equal(RouteRangeVerdict.ReservedCanFly, result.Validation.Range.Verdict);
        Assert.DoesNotContain(result.Validation.Warnings, w => w.Contains("range", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The invariant this class exists to pin down permanently: the fare a player is offered at
    /// route creation (<c>RoutePreviewResult.SuggestedFare</c>, what <c>RouteEndpoints.BuildRouteAsync</c>
    /// defaults a new route's <c>BaseFare</c> to) must EQUAL the reference fare the demand engine
    /// anchors <c>FareDemandModel</c>'s elasticity curve to (<see cref="ReferenceFareCalculator"/>).
    /// Before the fuel-honesty fix these were two separate hardcoded formulas (this one and the
    /// now-deleted <c>FareEstimator</c>) that happened to agree only because nobody had changed
    /// either one. If they ever diverge
    /// again, a player would be offered one fare while the engine scores it against a different
    /// one, silently distorting demand and elasticity. Derived from Enum.GetValues so a new
    /// strategy profile is covered automatically, per the plan's rule against hardcoded profile
    /// lists.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void SuggestedFare_EqualsReferenceFare_ForEveryStrategyProfile(AirlineStrategyProfile profile)
    {
        // Two real, differently-shaped sectors (a genuine international long-haul and a short
        // domestic hop) so this can't pass by coincidence on a single distance band.
        var longHaul = RoutePreviewCalculator.Calculate(Config, Bristol, Jfk, A320, profile);
        var shortHop = RoutePreviewCalculator.Calculate(Config, Bristol, NearbyDomestic, A320, profile);

        Assert.Equal(ReferenceFareCalculator.Calculate(Config, profile, longHaul.DistanceNm), longHaul.SuggestedFare);
        Assert.Equal(ReferenceFareCalculator.Calculate(Config, profile, shortHop.DistanceNm), shortHop.SuggestedFare);
    }

    /// <summary>The same-airport/zero-distance edge case both formulas have always fallen back to
    /// the configured minimum fare for, rather than throwing - PreviewAsync must never throw.</summary>
    [Fact]
    public void SuggestedFare_SameAirport_FallsBackToMinimumFare_WithoutThrowing()
    {
        var result = RoutePreviewCalculator.Calculate(Config, Bristol, Bristol, A320, AirlineStrategyProfile.Domestic);

        Assert.Equal(Config.ReferenceFare.MinimumFare, result.SuggestedFare);
    }

    public static IEnumerable<object[]> AllProfiles() =>
        Enum.GetValues<AirlineStrategyProfile>().Select(p => new object[] { p });
}

using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

/// <summary>
/// J28 - "can this aircraft use this airport" needs to check both length and surface, the same way
/// range is checked against the whole fleet rather than a single arbitrarily-chosen type. Mirrors
/// RouteRangeAssessorTests' structure and coverage intent.
/// </summary>
public class RunwaySuitabilityAssessorTests
{
    private static Runway MakeRunway(int lengthFt, string surface = "ASP", bool closed = false) => new()
    {
        Id = Guid.NewGuid(), AirportIcao = "TEST", Designator = "09/27", LengthFt = lengthFt, Surface = surface, IsClosed = closed,
    };

    private static Airport MakeAirport(string icao, int longestRunwayFt, params Runway[] runways) => new()
    {
        Icao = icao, Name = icao, LongestRunwayFt = longestRunwayFt, Runways = runways.ToList(),
    };

    private static RunwayCandidateAircraft Narrowbody(string registration = "G-NARO", bool reserved = false) =>
        new(registration, "Airbus A320", MinRunwayFt: 5500, MtowTonnes: 78, ReservedForPlayer: reserved);

    private static RunwayCandidateAircraft Widebody(string registration = "G-WIDE", bool reserved = false) =>
        new(registration, "Boeing 787-9 Dreamliner", MinRunwayFt: 9000, MtowTonnes: 254, ReservedForPlayer: reserved);

    // ---- IsHeavy ---------------------------------------------------------------------------

    [Theory]
    [InlineData(135.9, false)]
    [InlineData(136.0, true)]
    [InlineData(136.1, true)]
    public void IsHeavy_UsesTheIcaoWakeTurbulenceThreshold_WithAnInclusiveBoundary(double mtowTonnes, bool expected)
    {
        Assert.Equal(expected, RunwaySuitabilityAssessor.IsHeavy(mtowTonnes));
    }

    // ---- AssessAirport: length -------------------------------------------------------------

    [Fact]
    public void AssessAirport_RunwayLongEnough_IsNone()
    {
        var airport = MakeAirport("EGGD", 8000, MakeRunway(8000));

        Assert.Equal(RunwaySuitabilityProblem.None, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 5500, mtowTonnes: 78));
    }

    [Fact]
    public void AssessAirport_NoRunwayLongEnough_IsTooShort()
    {
        var airport = MakeAirport("EGXS", 3000, MakeRunway(3000));

        Assert.Equal(RunwaySuitabilityProblem.TooShort, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 5500, mtowTonnes: 78));
    }

    [Fact]
    public void AssessAirport_ClosedRunway_IsNeverCounted_EvenIfLongEnough()
    {
        var airport = MakeAirport("EGXC", 8000, MakeRunway(8000, closed: true), MakeRunway(3000));

        Assert.Equal(RunwaySuitabilityProblem.TooShort, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 5500, mtowTonnes: 78));
    }

    [Fact]
    public void AssessAirport_NoRunwaysLoaded_FallsBackToLongestRunwayFt()
    {
        // Airport.Runways defaults to an empty list - simulates a caller that queried without
        // Include(Runways), or an airport with genuinely no runway rows on record.
        var airport = new Airport { Icao = "EGXF", Name = "Fallback Field", LongestRunwayFt = 8000 };

        Assert.Equal(RunwaySuitabilityProblem.None, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 5500, mtowTonnes: 78));
        Assert.Equal(RunwaySuitabilityProblem.TooShort, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 9000, mtowTonnes: 78));
    }

    [Fact]
    public void AssessAirport_NoRunwaysLoaded_NeverBlocksOnSurface_EvenForAHeavyAircraft()
    {
        // The safety fallback: with no per-runway data, there is nothing to classify as soft, so a
        // missing Include(Runways) must never turn into an unearned surface block.
        var airport = new Airport { Icao = "EGXF", Name = "Fallback Field", LongestRunwayFt = 12000 };

        Assert.Equal(RunwaySuitabilityProblem.None, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 9000, mtowTonnes: 300));
    }

    // ---- AssessAirport: surface --------------------------------------------------------------

    [Fact]
    public void AssessAirport_HeavyAircraft_GrassRunway_IsSoftSurface_RegardlessOfLength()
    {
        var airport = MakeAirport("EGXG", 10000, MakeRunway(10000, surface: "GRASS"));

        Assert.Equal(RunwaySuitabilityProblem.SoftSurface, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 6000, mtowTonnes: 250));
    }

    [Fact]
    public void AssessAirport_LightAircraft_GrassRunway_IsNone()
    {
        var airport = MakeAirport("EGXG", 10000, MakeRunway(10000, surface: "GRASS"));

        Assert.Equal(RunwaySuitabilityProblem.None, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 3000, mtowTonnes: 20));
    }

    [Fact]
    public void AssessAirport_HeavyAircraft_OneOfSeveralRunwaysIsPaved_IsNone()
    {
        // A long grass runway AND a shorter paved one, both long enough for this aircraft - the
        // paved one is what it would actually use.
        var airport = MakeAirport("EGXM", 10000, MakeRunway(10000, surface: "GRASS"), MakeRunway(7000, surface: "ASP"));

        Assert.Equal(RunwaySuitabilityProblem.None, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 6000, mtowTonnes: 250));
    }

    [Fact]
    public void AssessAirport_HeavyAircraft_PavedRunwayTooShort_GrassRunwayLongEnough_IsSoftSurface()
    {
        // The paved runway can't be used (too short) and the only long-enough one is grass - a
        // heavy aircraft has nothing it can use here, which must read as SoftSurface, not TooShort,
        // since length alone would wrongly say this airport works.
        var airport = MakeAirport("EGXP", 10000, MakeRunway(10000, surface: "GRASS"), MakeRunway(4000, surface: "ASP"));

        Assert.Equal(RunwaySuitabilityProblem.SoftSurface, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 6000, mtowTonnes: 250));
    }

    [Fact]
    public void AssessAirport_HeavyAircraft_OnlyRunwayHasAnAmbiguousCompositeSurface_IsNone()
    {
        // "ASP-GRS" most likely means asphalt with a grass verge - a heavy aircraft must not be
        // refused an airport whose only runway happens to be recorded this way. This is the
        // composite-surface fix flowing all the way through to the airport-level verdict, not just
        // RunwaySurfaceClassifier.IsSoft in isolation.
        var airport = MakeAirport("EGXA", 8000, MakeRunway(8000, surface: "ASP-GRS"));

        Assert.Equal(RunwaySuitabilityProblem.None, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 6000, mtowTonnes: 250));
    }

    [Fact]
    public void AssessAirport_HeavyAircraft_OnlyRunwayIsUnambiguouslySoft_IsSoftSurface()
    {
        // The contrast case: nothing hard named anywhere, so this one genuinely is soft.
        var airport = MakeAirport("EGXA", 8000, MakeRunway(8000, surface: "TURF"));

        Assert.Equal(RunwaySuitabilityProblem.SoftSurface, RunwaySuitabilityAssessor.AssessAirport(airport, minRunwayFt: 6000, mtowTonnes: 250));
    }

    // ---- AssessRoute -------------------------------------------------------------------------

    [Fact]
    public void AssessRoute_ChecksDepartureBeforeArrival()
    {
        var departure = MakeAirport("EGXS", 3000, MakeRunway(3000));
        var arrival = MakeAirport("EGGD", 8000, MakeRunway(8000));

        var problem = RunwaySuitabilityAssessor.AssessRoute(departure, arrival, minRunwayFt: 5500, mtowTonnes: 78, out var blockingAirport);

        Assert.Equal(RunwaySuitabilityProblem.TooShort, problem);
        Assert.Equal("EGXS", blockingAirport.Icao);
    }

    [Fact]
    public void AssessRoute_DepartureFine_ArrivalTooShort_NamesArrival()
    {
        var departure = MakeAirport("EGGD", 8000, MakeRunway(8000));
        var arrival = MakeAirport("EGXS", 3000, MakeRunway(3000));

        var problem = RunwaySuitabilityAssessor.AssessRoute(departure, arrival, minRunwayFt: 5500, mtowTonnes: 78, out var blockingAirport);

        Assert.Equal(RunwaySuitabilityProblem.TooShort, problem);
        Assert.Equal("EGXS", blockingAirport.Icao);
    }

    [Fact]
    public void AssessRoute_BothEndsFine_IsNone()
    {
        var departure = MakeAirport("EGGD", 8000, MakeRunway(8000));
        var arrival = MakeAirport("EGPH", 8500, MakeRunway(8500));

        var problem = RunwaySuitabilityAssessor.AssessRoute(departure, arrival, minRunwayFt: 5500, mtowTonnes: 78, out _);

        Assert.Equal(RunwaySuitabilityProblem.None, problem);
    }

    // ---- Assess (fleet-wide, guidance) -----------------------------------------------------

    [Fact]
    public void Assess_ReservedAircraftCanUseBothEnds_SaysNothingAtAll()
    {
        // Long enough for the widebody (needs 9,000 ft) as well as the narrowbody.
        var departure = MakeAirport("EGGD", 9500, MakeRunway(9500));
        var arrival = MakeAirport("EGPH", 9500, MakeRunway(9500));

        var result = RunwaySuitabilityAssessor.Assess(departure, arrival, new[] { Narrowbody(), Widebody(reserved: true) });

        Assert.Equal(RunwaySuitabilityVerdict.ReservedCanUse, result.Verdict);
        Assert.False(result.Blocking);
        Assert.Null(result.Message);
        Assert.Equal("G-WIDE", result.AircraftRegistration);
    }

    [Fact]
    public void Assess_OnlyAnUnreservedFleetAircraftFits_DoesNotBlock_AndNamesTheAircraftToReserve()
    {
        var departure = MakeAirport("EGGD", 9500, MakeRunway(9500));
        var arrival = MakeAirport("EGPH", 9500, MakeRunway(9500));

        var result = RunwaySuitabilityAssessor.Assess(departure, arrival, new[] { Widebody(reserved: false) });

        Assert.Equal(RunwaySuitabilityVerdict.RequiresReservation, result.Verdict);
        Assert.False(result.Blocking);
        Assert.Equal("G-WIDE", result.AircraftRegistration);
        Assert.NotNull(result.Message);
        Assert.Contains("G-WIDE", result.Message);
        Assert.Contains("reserve it", result.Message);
        Assert.Contains("virtual pilot", result.Message);
    }

    [Fact]
    public void Assess_NothingInTheFleetFits_Blocks()
    {
        var departure = MakeAirport("EGXS", 3000, MakeRunway(3000));
        var arrival = MakeAirport("EGGD", 8000, MakeRunway(8000));

        var result = RunwaySuitabilityAssessor.Assess(departure, arrival, new[] { Narrowbody(reserved: true) });

        Assert.Equal(RunwaySuitabilityVerdict.BeyondFleet, result.Verdict);
        Assert.True(result.Blocking);
        Assert.NotNull(result.Message);
        Assert.Contains("too short for anything in your fleet", result.Message);
        Assert.Contains("G-NARO", result.Message);
    }

    [Fact]
    public void Assess_NoFleet_IsNotAssessed()
    {
        var departure = MakeAirport("EGGD", 8000, MakeRunway(8000));
        var arrival = MakeAirport("EGPH", 8500, MakeRunway(8500));

        var result = RunwaySuitabilityAssessor.Assess(departure, arrival, Array.Empty<RunwayCandidateAircraft>());

        Assert.Equal(RunwaySuitabilityVerdict.NotAssessed, result.Verdict);
        Assert.False(result.Blocking);
    }

    [Fact]
    public void Assess_IsDeterministic_RegardlessOfFleetOrdering()
    {
        var departure = MakeAirport("EGGD", 8000, MakeRunway(8000));
        var arrival = MakeAirport("EGPH", 8500, MakeRunway(8500));
        var fleet = new[] { Narrowbody("G-BBBB"), Narrowbody("G-AAAA"), Widebody("G-CCCC") };

        var forwards = RunwaySuitabilityAssessor.Assess(departure, arrival, fleet);
        var backwards = RunwaySuitabilityAssessor.Assess(departure, arrival, fleet.Reverse().ToArray());

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void Assess_PrefersTheMostRunwayTolerantAircraft_LightBeforeHeavy_ThenShortestMinimum()
    {
        // Long enough for both candidates (Widebody needs 9,000 ft) so this is genuinely a
        // preference question, not one candidate being disqualified outright.
        var departure = MakeAirport("EGGD", 9500, MakeRunway(9500));
        var arrival = MakeAirport("EGPH", 9500, MakeRunway(9500));
        var fleet = new[] { Widebody("G-HEAVY"), Narrowbody("G-LIGHT") };

        var result = RunwaySuitabilityAssessor.Assess(departure, arrival, fleet);

        // Both fit, but the narrowbody (light, shorter minimum) is the more "tolerant" choice.
        Assert.Equal("G-LIGHT", result.AircraftRegistration);
    }
}

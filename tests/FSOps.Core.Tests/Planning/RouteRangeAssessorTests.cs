using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

/// <summary>
/// The three answers a player can act on, pinned to exact behaviour. The defect being closed here
/// (J23) is that route creation used to refuse a long sector in the name of one arbitrarily-chosen
/// aircraft type - "beyond the Airbus A320's practical operating range" - when the airline owned a
/// 787 that could fly it comfortably. Only "nothing you own can do this" may ever block.
/// </summary>
public class RouteRangeAssessorTests
{
    private static RangeCandidateAircraft Narrowbody(string registration = "G-NARO", bool reserved = false) =>
        new(registration, "Airbus A320", RangeNm: 3300, ReservedForPlayer: reserved);

    private static RangeCandidateAircraft Widebody(string registration = "G-WIDE", bool reserved = false) =>
        new(registration, "Boeing 787-9 Dreamliner", RangeNm: 7635, ReservedForPlayer: reserved);

    [Fact]
    public void Assess_ReservedAircraftHasTheRange_SaysNothingAtAll()
    {
        var result = RouteRangeAssessor.Assess(3761, new[] { Narrowbody(), Widebody(reserved: true) });

        Assert.Equal(RouteRangeVerdict.ReservedCanFly, result.Verdict);
        Assert.False(result.Blocking);
        Assert.Null(result.Message);
        Assert.Equal("G-WIDE", result.AircraftRegistration);
    }

    [Fact]
    public void Assess_OnlyAnUnreservedFleetAircraftHasTheRange_DoesNotBlock_AndNamesTheAircraftToReserve()
    {
        // The exact case the player hit: the route is genuinely flyable by this airline, it just
        // isn't flyable by the player TODAY. That is guidance, never a refusal.
        var result = RouteRangeAssessor.Assess(3761, new[] { Narrowbody(reserved: true), Widebody() });

        Assert.Equal(RouteRangeVerdict.RequiresReservation, result.Verdict);
        Assert.False(result.Blocking);
        Assert.Equal("G-WIDE", result.AircraftRegistration);
        Assert.NotNull(result.Message);
        Assert.Contains("G-WIDE", result.Message);
        Assert.Contains("Boeing 787-9 Dreamliner", result.Message);
        Assert.Contains("reserve it", result.Message);
        // Both ways out are named - reserving it, or leaving it for a virtual pilot.
        Assert.Contains("virtual pilot", result.Message);
    }

    [Fact]
    public void Assess_NothingInTheFleetHasTheRange_Blocks_AndPointsAtAcquiringOne()
    {
        var result = RouteRangeAssessor.Assess(3761, new[] { Narrowbody(reserved: true), Narrowbody("G-OTHR") });

        Assert.Equal(RouteRangeVerdict.BeyondFleet, result.Verdict);
        Assert.True(result.Blocking);
        Assert.NotNull(result.Message);
        Assert.Contains("beyond every aircraft in your fleet", result.Message);
        Assert.Contains("2805 nm", result.Message);
        Assert.Contains("Fleet page", result.Message);
    }

    [Fact]
    public void Assess_NeverImpliesOneAircraftIsTheWholeAirline()
    {
        // The literal sentence the player was shown, and the shape of wording that must never come
        // back: a possessive singular type name standing in for the airline's whole capability.
        foreach (var fleet in new[]
                 {
                     new[] { Narrowbody(reserved: true) },
                     new[] { Narrowbody(), Widebody() },
                     new[] { Narrowbody(), Narrowbody("G-OTHR") },
                 })
        {
            var message = RouteRangeAssessor.Assess(3761, fleet).Message;
            if (message is null)
            {
                continue;
            }

            Assert.DoesNotContain("Airbus A320's", message);
            Assert.DoesNotContain("Boeing 787-9 Dreamliner's", message);
        }
    }

    [Fact]
    public void Assess_NoFleet_OrNoDistance_IsNotAssessed()
    {
        Assert.Equal(RouteRangeVerdict.NotAssessed, RouteRangeAssessor.Assess(3761, Array.Empty<RangeCandidateAircraft>()).Verdict);
        Assert.Equal(RouteRangeVerdict.NotAssessed, RouteRangeAssessor.Assess(0, new[] { Narrowbody() }).Verdict);
        Assert.False(RouteRangeAssessor.Assess(3761, Array.Empty<RangeCandidateAircraft>()).Blocking);
    }

    [Fact]
    public void Assess_IsDeterministic_RegardlessOfFleetOrdering()
    {
        var fleet = new[] { Narrowbody("G-BBBB"), Narrowbody("G-AAAA"), Widebody("G-CCCC") };

        var forwards = RouteRangeAssessor.Assess(3761, fleet);
        // Enumerable.Reverse, not fleet.Reverse(): on an array the latter binds to the Span
        // extension, which reverses in place and returns void.
        var backwards = RouteRangeAssessor.Assess(3761, Enumerable.Reverse(fleet).ToArray());

        Assert.Equal(forwards, backwards);
        Assert.Equal("G-CCCC", forwards.AircraftRegistration);
    }

    [Fact]
    public void Assess_TwoEquallyCapableAircraft_NamesTheSameOneEveryTime()
    {
        var fleet = new[] { Widebody("G-ZZZZ"), Widebody("G-AAAA") };

        Assert.Equal("G-AAAA", RouteRangeAssessor.Assess(3761, fleet).AircraftRegistration);
    }

    [Theory]
    // Derated at 0.85: a 3,300 nm aircraft plans to 2,805 nm. The boundary is inclusive.
    [InlineData(2804.9, true)]
    [InlineData(2805.0, true)]
    [InlineData(2805.1, false)]
    public void CanReach_UsesTheDeratedRange_WithAnInclusiveBoundary(double distanceNm, bool expected)
    {
        Assert.Equal(expected, RouteRangeAssessor.CanReach(3300, distanceNm));
    }

    [Fact]
    public void OperationalRangeFactor_IsUnchangedAtPointEightFive()
    {
        // Existing, long-standing behaviour - this test exists so a change to it is a deliberate act
        // rather than a side effect of something else.
        Assert.Equal(0.85, RouteRangeAssessor.OperationalRangeFactor);
        Assert.Equal(RouteRangeAssessor.OperationalRangeFactor, RoutePreviewCalculator.OperationalRangeFactor);
    }
}

using System.Text.Json;
using FSOps.Core.Entities;
using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

/// <summary>
/// Covers <see cref="FlownTrackBuilder"/> - the reader that turns a flight's append-only
/// PositionSnapshot rows into the path it actually flew. The awkward cases are the point of these
/// tests: a flight with no track at all is the NORMAL case for every virtual-pilot sector and every
/// flight that predates position recording, and it must return an empty result rather than throw,
/// invent a point, or be indistinguishable from a parse failure.
/// </summary>
public class FlownTrackBuilderTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    private static FlightEvent Snapshot(int secondsIn, double lat, double lon, double? altMslFt = 12000, string phase = "Cruise")
    {
        var payload = JsonSerializer.Serialize(new { lat, lon, altMslFt, altAglFt = 11500.0, iasKt = 280.0, gsKt = 300.0, vsFpm = 0.0, headingTrue = 10.0, fuelKg = 4200.0, phase });
        return new FlightEvent { Id = Guid.NewGuid(), FlightId = Guid.Empty, Utc = Base.AddSeconds(secondsIn), Type = FlightEventType.PositionSnapshot, PayloadJson = payload };
    }

    private static FlightEvent Raw(int secondsIn, string payloadJson, FlightEventType type = FlightEventType.PositionSnapshot) =>
        new() { Id = Guid.NewGuid(), FlightId = Guid.Empty, Utc = Base.AddSeconds(secondsIn), Type = type, PayloadJson = payloadJson };

    /// <summary>Bristol, the stand the real sector below actually departed from.</summary>
    private static readonly TrackAnchor Eggd = new(51.38526, -2.71770);

    /// <summary>Edinburgh, where it landed.</summary>
    private static readonly TrackAnchor Egph = new(55.95000, -3.37250);

    /// <summary>
    /// The simulator's uninitialised position: what SimConnect reports before it has a real aircraft
    /// state to put in the packet. Roughly 0.0N 90.0E, the Bay of Bengal, 5,505 nm from Bristol.
    /// These are the exact values recorded, not rounded ones.
    /// </summary>
    private const double UninitialisedLat = -2.1556893808986427E-07;
    private const double UninitialisedLon = 90.00032277330374;

    [Fact]
    public void NoEventsAtAll_ReturnsAnEmptyTrack_NotAFailure()
    {
        // Every virtual-pilot flight lands here: no simulator was ever attached, so no snapshot was
        // ever written. So does every flight flown before position recording existed. This has to be
        // an ordinary empty answer the UI can explain, not an exception and not a fabricated point.
        var track = FlownTrackBuilder.Build([]);

        Assert.Empty(track.Points);
        Assert.Equal(0, track.RecordedPointCount);
        Assert.False(track.Thinned);
    }

    [Fact]
    public void IgnoresEventsThatAreNotPositionSnapshots()
    {
        var track = FlownTrackBuilder.Build([
            Raw(0, "{\"fromPhase\":\"TaxiOut\",\"toPhase\":\"Climb\"}", FlightEventType.PhaseChange),
            Raw(10, "{\"LatitudeDeg\":51.4,\"LongitudeDeg\":-2.7}", FlightEventType.Touchdown),
        ]);

        Assert.Empty(track.Points);
    }

    [Fact]
    public void SinglePoint_IsReturnedAsOnePoint()
    {
        // One position is a position, not a path. It is still worth showing - the caller draws a
        // marker and says so - but nothing here may invent a second point to draw a line to.
        var track = FlownTrackBuilder.Build([Snapshot(0, 51.3827, -2.7191)]);

        var point = Assert.Single(track.Points);
        Assert.Equal(51.3827, point.Latitude, precision: 6);
        Assert.Equal(-2.7191, point.Longitude, precision: 6);
        Assert.Equal(1, track.RecordedPointCount);
    }

    [Fact]
    public void OrdersByTime_RegardlessOfTheOrderRowsArriveIn()
    {
        var track = FlownTrackBuilder.Build([
            Snapshot(30, 53.0, -3.0),
            Snapshot(0, 51.0, -2.0),
            Snapshot(15, 52.0, -2.5),
        ]);

        Assert.Equal([51.0, 52.0, 53.0], track.Points.Select(p => p.Latitude));
    }

    [Fact]
    public void ReadsAltitudeGroundSpeedAndPhase_AndTreatsAMissingFieldAsUnknownRatherThanZero()
    {
        // A row written by an earlier version can legitimately carry fewer keys. A missing altitude
        // must read as "not recorded" - reporting 0 ft would put the aircraft on the ground.
        var track = FlownTrackBuilder.Build([Raw(0, "{\"lat\":51.4,\"lon\":-2.7}")]);

        var point = Assert.Single(track.Points);
        Assert.Null(point.AltitudeMslFt);
        Assert.Null(point.GroundSpeedKt);
        Assert.Null(point.Phase);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"lat\":51.4}")]
    [InlineData("{\"lat\":\"51.4\",\"lon\":\"-2.7\"}")]
    [InlineData("{\"lat\":123.0,\"lon\":-2.7}")]
    [InlineData("{\"lat\":51.4,\"lon\":999.0}")]
    public void UnreadableOrImpossiblePayloads_AreSkipped_NeverThrown(string payloadJson)
    {
        // These rows are append-only history and cannot be repaired. One bad row must cost exactly
        // itself, never the whole track.
        var track = FlownTrackBuilder.Build([Snapshot(0, 51.0, -2.0), Raw(15, payloadJson), Snapshot(30, 52.0, -2.5)]);

        Assert.Equal(2, track.Points.Count);
        Assert.Equal([51.0, 52.0], track.Points.Select(p => p.Latitude));
    }

    [Fact]
    public void LongitudesAreReturnedExactlyAsRecorded_IncludingAcrossTheAntimeridian()
    {
        // Normalising or unwrapping here would corrupt the record. Splitting a path that crosses the
        // antimeridian is the renderer's job (lib/geo splitAntimeridian), and it needs the raw
        // values to detect the crossing at all.
        var track = FlownTrackBuilder.Build([
            Snapshot(0, 51.0, 179.5),
            Snapshot(15, 51.1, -179.6),
        ]);

        Assert.Equal([179.5, -179.6], track.Points.Select(p => p.Longitude));
    }

    [Fact]
    public void TrackWithinTheCap_IsReturnedWhole_AndNotMarkedThinned()
    {
        var events = Enumerable.Range(0, 50).Select(i => Snapshot(i * 15, 51 + i * 0.01, -2.0)).ToList();

        var track = FlownTrackBuilder.Build(events, maxPoints: 50);

        Assert.Equal(50, track.Points.Count);
        Assert.Equal(50, track.RecordedPointCount);
        Assert.False(track.Thinned);
    }

    [Fact]
    public void TrackOverTheCap_IsThinned_ButKeepsTheFirstAndLastPointsAndReportsTheRealTotal()
    {
        // Thinning is a rendering concession. It must never move where the track began or ended, and
        // it must never let the reduced figure pass itself off as the whole track: a player looking
        // at "showing 10 of 1,000" knows what they are seeing.
        var events = Enumerable.Range(0, 1000).Select(i => Snapshot(i * 15, 51 + i * 0.001, -2.0)).ToList();

        var track = FlownTrackBuilder.Build(events, maxPoints: 10);

        Assert.True(track.Thinned);
        Assert.Equal(1000, track.RecordedPointCount);
        Assert.True(track.Points.Count <= 10);
        Assert.Equal(51.0, track.Points[0].Latitude, precision: 6);
        Assert.Equal(51 + 999 * 0.001, track.Points[^1].Latitude, precision: 6);
    }

    [Fact]
    public void Thinning_NeverEmitsTheSamePointTwice_AndKeepsTimeOrder()
    {
        var events = Enumerable.Range(0, 137).Select(i => Snapshot(i * 15, 51 + i * 0.01, -2.0)).ToList();

        var track = FlownTrackBuilder.Build(events, maxPoints: 20);

        Assert.Equal(track.Points.Select(p => p.Utc).Distinct().Count(), track.Points.Count);
        Assert.Equal(track.Points.OrderBy(p => p.Utc).Select(p => p.Utc), track.Points.Select(p => p.Utc));
    }

    /// <summary>
    /// The real thing. These are the first three PositionSnapshot rows of the player's EGGD-EGPH
    /// sector of 2026-08-13, exactly as they sit in FlightEvent: the sim's uninitialised position
    /// twice, fifteen seconds apart, and then the aircraft on its stand at Bristol. Drawing the
    /// first two put the departure marker in the Indian Ocean and stretched the map from
    /// Scandinavia to the coast of Africa.
    /// </summary>
    [Fact]
    public void RealEggdToEgph_TheUninitialisedOpeningFixes_AreNotDrawn()
    {
        var events = new List<FlightEvent>
        {
            Snapshot(0, UninitialisedLat, UninitialisedLon, altMslFt: 227.8, phase: "Preflight"),
            Snapshot(15, UninitialisedLat, UninitialisedLon, altMslFt: 227.8, phase: "Preflight"),
            Snapshot(30, 51.38534252774989, -2.7070546666672604, altMslFt: 613.9, phase: "Preflight"),
            Snapshot(45, 51.38526993250003, -2.7175892082898767, altMslFt: 613.9, phase: "TaxiOut"),
            Snapshot(60, 52.5, -2.9, altMslFt: 18000, phase: "Climb"),
            Snapshot(75, 55.94836445882101, -3.3665372600875436, altMslFt: 800, phase: "Approach"),
        };

        var track = FlownTrackBuilder.Build(events, Eggd);

        // The two bad rows are gone from what is drawn...
        Assert.Equal(2, track.DiscardedLeadingPointCount);
        Assert.Equal(4, track.Points.Count);
        Assert.DoesNotContain(track.Points, p => Math.Abs(p.Longitude - UninitialisedLon) < 1.0);

        // ...the track now begins where the aircraft actually was, at Bristol...
        Assert.Equal(51.38534252774989, track.Points[0].Latitude, precision: 9);
        Assert.Equal(-2.7070546666672604, track.Points[0].Longitude, precision: 9);

        // ...and nothing that was recorded has been hidden: the honest total still says six.
        Assert.Equal(6, track.RecordedPointCount);
        Assert.False(track.Thinned);
    }

    /// <summary>
    /// The same rows with no anchor available - a deleted route, or a departure ICAO the airport
    /// table does not carry. The weaker fallback still catches it, because the step out of the Bay
    /// of Bengal is physically impossible and it happens inside the opening window.
    /// </summary>
    [Fact]
    public void RealEggdToEgph_WithNoAnchorAtAll_TheFallbackStillDropsTheOpeningJunk()
    {
        var events = new List<FlightEvent>
        {
            Snapshot(0, UninitialisedLat, UninitialisedLon, phase: "Preflight"),
            Snapshot(15, UninitialisedLat, UninitialisedLon, phase: "Preflight"),
            Snapshot(30, 51.38534252774989, -2.7070546666672604, phase: "Preflight"),
            Snapshot(45, 51.38526993250003, -2.7175892082898767, phase: "TaxiOut"),
        };

        var track = FlownTrackBuilder.Build(events);

        Assert.Equal(2, track.DiscardedLeadingPointCount);
        Assert.Equal(4, track.RecordedPointCount);
        Assert.All(track.Points, p => Assert.True(Math.Abs(p.Longitude) < 10.0));
    }

    [Fact]
    public void ATrackThatIsJunkFromBeginningToEnd_IsReturnedWhole()
    {
        // Nothing here can prove these are junk - the aircraft is never near Bristol, so there is no
        // believable point to anchor on and no impossible step to separate a prefix at. Returning
        // the track as recorded is the honest answer; deleting a player's entire recorded track on a
        // guess is not.
        var events = Enumerable.Range(0, 6)
            .Select(i => Snapshot(i * 15, UninitialisedLat, UninitialisedLon, phase: "Preflight"))
            .ToList();

        var track = FlownTrackBuilder.Build(events, Eggd);

        Assert.Equal(0, track.DiscardedLeadingPointCount);
        Assert.Equal(6, track.Points.Count);
        Assert.Equal(6, track.RecordedPointCount);
    }

    [Fact]
    public void ASingleRecordedPoint_IsNeverDiscarded_EvenIfItIsTheBadFix()
    {
        // There is no second reading to judge it against, and a flight whose only recorded position
        // is a bad one still has to show what it has rather than nothing at all.
        var track = FlownTrackBuilder.Build([Snapshot(0, UninitialisedLat, UninitialisedLon)], Eggd);

        Assert.Equal(0, track.DiscardedLeadingPointCount);
        Assert.Single(track.Points);
    }

    [Fact]
    public void AnAircraftThatStartsALittleAwayFromTheGate_KeepsEveryPoint()
    {
        // Roughly 25 nm north of Bristol - a recording that began on the initial climb rather than
        // on stand. Well inside the anchor radius, so there is no prefix to consider at all.
        var events = new List<FlightEvent>
        {
            Snapshot(0, 51.80, -2.72, altMslFt: 4000, phase: "Climb"),
            Snapshot(15, 51.90, -2.75, altMslFt: 6000, phase: "Climb"),
            Snapshot(30, 52.00, -2.80, altMslFt: 8000, phase: "Climb"),
        };

        var track = FlownTrackBuilder.Build(events, Eggd);

        Assert.Equal(0, track.DiscardedLeadingPointCount);
        Assert.Equal(3, track.Points.Count);
    }

    [Fact]
    public void AnAircraftThatStartsWellOutsideTheRadiusAndFliesIn_KeepsEveryPoint()
    {
        // 300 nm out and inbound - the aircraft only crosses the anchor radius part-way through, so
        // there IS a prefix. It is kept, because the step into the radius happens at an ordinary
        // airliner speed: the data itself says the prefix belongs to this flight.
        var events = Enumerable.Range(0, 12)
            .Select(i => Snapshot(i * 300, 56.0 - i * 0.4, -2.72, altMslFt: 30000, phase: "Cruise"))
            .ToList();

        var track = FlownTrackBuilder.Build(events, Eggd);

        Assert.Equal(0, track.DiscardedLeadingPointCount);
        Assert.Equal(12, track.Points.Count);
    }

    [Fact]
    public void AGenuineMidFlightJump_SurvivesUntouched()
    {
        // This is the whole point of only ever looking at the LEADING run. A teleport in the middle
        // of a sector is evidence - it is what FlightIntegrityMonitor records as PositionJumpDetected
        // and what stops the sector being paid - and a track that quietly straightened it out would
        // be hiding the very thing the integrity system exists to preserve.
        var events = new List<FlightEvent>
        {
            Snapshot(0, 51.38526, -2.71770, phase: "Preflight"),
            Snapshot(15, 51.40, -2.75, phase: "TaxiOut"),
            Snapshot(30, 51.50, -2.80, phase: "Climb"),
            Snapshot(45, UninitialisedLat, UninitialisedLon, phase: "Cruise"),
            Snapshot(60, 55.94836445882101, -3.3665372600875436, phase: "Approach"),
        };

        var track = FlownTrackBuilder.Build(events, Eggd);

        Assert.Equal(0, track.DiscardedLeadingPointCount);
        Assert.Equal(5, track.Points.Count);
        Assert.Contains(track.Points, p => Math.Abs(p.Longitude - UninitialisedLon) < 1e-6);
    }

    [Fact]
    public void AJumpAfterTheOpeningWindow_IsNotTouchedByTheUnanchoredFallback()
    {
        // With no anchor the fallback can only look at the opening MaxUnanchoredLeadingDiscard
        // points. A jump later than that is out of its reach by construction, so it can never be
        // mistaken for a bad opening fix and removed.
        var events = new List<FlightEvent>();
        for (var i = 0; i < 20; i++)
        {
            var isJump = i == FlownTrackBuilder.MaxUnanchoredLeadingDiscard + 3;
            events.Add(isJump
                ? Snapshot(i * 15, UninitialisedLat, UninitialisedLon)
                : Snapshot(i * 15, 51 + i * 0.01, -2.0));
        }

        var track = FlownTrackBuilder.Build(events);

        Assert.Equal(0, track.DiscardedLeadingPointCount);
        Assert.Equal(20, track.Points.Count);
    }

    [Fact]
    public void AnAcceleratedSector_IsNotMistakenForATeleport()
    {
        // At 8x, fifteen seconds of wall clock is two minutes of flying - about 16 nm at 480 kt,
        // which reads as 3,840 kt if the rate is ignored and would trip the impossible-step test.
        // The rate normalisation is why this prefix is kept.
        var events = Enumerable.Range(0, 10)
            .Select(i => Snapshot(i * 15, 56.0 - i * 0.27, -2.72, altMslFt: 30000, phase: "Cruise"))
            .ToList();

        var track = FlownTrackBuilder.Build(events, Eggd, maxSimulationRate: 8.0);

        Assert.Equal(0, track.DiscardedLeadingPointCount);
        Assert.Equal(10, track.Points.Count);
    }

    [Fact]
    public void DiscardedPointsStillCountTowardsTheRecordedTotal_EvenWhenTheRestIsThinned()
    {
        // The disclosure has to hold in combination: recordedPointCount is what the flight has,
        // discardedLeadingPointCount is what was not drawable, and Points is what was drawn.
        var events = new List<FlightEvent>
        {
            Snapshot(0, UninitialisedLat, UninitialisedLon),
            Snapshot(15, UninitialisedLat, UninitialisedLon),
        };
        events.AddRange(Enumerable.Range(0, 100).Select(i => Snapshot(30 + i * 15, 51.38 + i * 0.01, -2.71)));

        var track = FlownTrackBuilder.Build(events, Eggd, maxPoints: 10);

        Assert.Equal(102, track.RecordedPointCount);
        Assert.Equal(2, track.DiscardedLeadingPointCount);
        Assert.True(track.Thinned);
        Assert.True(track.Points.Count <= 10);
        Assert.Equal(51.38, track.Points[0].Latitude, precision: 6);
    }

    [Fact]
    public void TheArrivalEndOfATrackIsNeverTrimmed()
    {
        // Only the leading run is ever in scope. A junk sample at the very END - the sim being shut
        // down mid-stream - stays exactly where it is.
        var events = new List<FlightEvent>
        {
            Snapshot(0, Eggd.LatitudeDeg, Eggd.LongitudeDeg, phase: "Preflight"),
            Snapshot(15, 53.0, -3.0, phase: "Cruise"),
            Snapshot(30, Egph.LatitudeDeg, Egph.LongitudeDeg, phase: "TaxiIn"),
            Snapshot(45, UninitialisedLat, UninitialisedLon, phase: "Shutdown"),
        };

        var track = FlownTrackBuilder.Build(events, Eggd);

        Assert.Equal(0, track.DiscardedLeadingPointCount);
        Assert.Equal(4, track.Points.Count);
        Assert.Equal(UninitialisedLon, track.Points[^1].Longitude, precision: 6);
    }
}

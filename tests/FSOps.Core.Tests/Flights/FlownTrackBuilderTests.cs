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
}

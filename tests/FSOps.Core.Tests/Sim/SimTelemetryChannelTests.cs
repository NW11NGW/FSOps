using FSOps.Sim;

namespace FSOps.Core.Tests.Sim;

public class SimTelemetryChannelTests
{
    [Fact]
    public void Create_WhenWrittenBeyondCapacity_DropsOldestAndKeepsNewestInOrder()
    {
        var channel = SimTelemetryChannel.Create();
        var totalWritten = SimTelemetryChannel.Capacity + 5;

        for (var i = 0; i < totalWritten; i++)
        {
            var wrote = channel.Writer.TryWrite(Sample(i));
            Assert.True(wrote, $"write #{i} should never block or fail - the channel drops the oldest item instead");
        }

        var received = new List<int>();
        while (channel.Reader.TryRead(out var sample))
        {
            received.Add((int)sample.LatitudeDeg);
        }

        Assert.Equal(SimTelemetryChannel.Capacity, received.Count);
        // The oldest 5 writes (indices 0-4) must have been evicted; what remains is contiguous
        // and in original write order.
        Assert.Equal(5, received[0]);
        Assert.Equal(totalWritten - 1, received[^1]);
        for (var i = 1; i < received.Count; i++)
        {
            Assert.Equal(received[i - 1] + 1, received[i]);
        }
    }

    [Fact]
    public void Create_ReaderNeverBlocksWriter_EvenWhenNooneIsReading()
    {
        var channel = SimTelemetryChannel.Create();

        // A slow (or absent) consumer must never make the producer block - this is the whole
        // point of drop-oldest over the default backpressure-by-blocking behaviour.
        for (var i = 0; i < SimTelemetryChannel.Capacity * 3; i++)
        {
            Assert.True(channel.Writer.TryWrite(Sample(i)));
        }
    }

    private static TelemetrySample Sample(int index) => new(
        TimestampUtc: DateTimeOffset.UtcNow,
        LatitudeDeg: index,
        LongitudeDeg: 0,
        AltitudeMslFt: 0,
        AltitudeAglFt: 0,
        IndicatedAirspeedKt: 0,
        GroundSpeedKt: 0,
        VerticalSpeedFpm: 0,
        TrueHeadingDeg: 0,
        MagneticHeadingDeg: 0,
        OnGround: false,
        EngineRunning: false,
        ParkingBrakeSet: false,
        GForce: 1,
        TouchdownNormalVelocityFps: 0,
        TotalFuelKg: 0,
        AircraftTitle: "test",
        AtcModel: "test",
        AtcType: "test",
        SimulationRate: 1.0,
        IsSlewActive: false);
}

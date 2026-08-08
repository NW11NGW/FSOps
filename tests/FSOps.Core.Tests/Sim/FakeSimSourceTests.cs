using FSOps.Sim;
using FSOps.Sim.Fake;

namespace FSOps.Core.Tests.Sim;

public class FakeSimSourceTests
{
    private static string ReplayPath => Path.Combine(AppContext.BaseDirectory, "Fake", "Replays", "egkk-lebl.json");

    // A large time-compression factor plus a short sample interval lets these tests exercise the
    // entire ~93 minute scripted flight in a fraction of a second of real wall-clock time - the
    // generous collection windows used below give this plenty of headroom even when the test
    // process is under load from other tests running in parallel.
    private static FakeSimSourceOptions FastOptions(bool loop = false) => new()
    {
        ReplayFilePath = ReplayPath,
        TimeCompressionFactor = 100_000,
        SampleInterval = TimeSpan.FromMilliseconds(5),
        Loop = loop,
    };

    [Fact]
    public async Task StartAsync_TransitionsThroughConnectingThenConnected()
    {
        await using var source = new FakeSimSource(FastOptions());
        var states = new List<SimConnectionState>();
        source.ConnectionStateChanged += (_, s) => states.Add(s);

        Assert.Equal(SimConnectionState.Disconnected, source.ConnectionState);

        await source.StartAsync(CancellationToken.None);

        Assert.Equal(SimConnectionState.Connected, source.ConnectionState);
        Assert.Equal(new[] { SimConnectionState.Connecting, SimConnectionState.Connected }, states);

        await source.StopAsync(CancellationToken.None);
        Assert.Equal(SimConnectionState.Disconnected, source.ConnectionState);
    }

    [Fact]
    public async Task StartAsync_PopulatesCurrentAircraftFromScript()
    {
        await using var source = new FakeSimSource(FastOptions());

        await source.StartAsync(CancellationToken.None);

        Assert.NotNull(source.CurrentAircraft);
        Assert.Equal("Airbus A320neo Asobo", source.CurrentAircraft!.Title);
        Assert.Equal("A20N", source.CurrentAircraft.AtcModel);
        Assert.Equal("Airbus", source.CurrentAircraft.AtcType);

        await source.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Replay_ProducesSamplesInNonDecreasingSimTimeOrder_AndReachesExpectedStates()
    {
        await using var source = new FakeSimSource(FastOptions());
        await source.StartAsync(CancellationToken.None);

        var samples = await CollectAsync(source, TimeSpan.FromSeconds(6));

        await source.StopAsync(CancellationToken.None);

        Assert.NotEmpty(samples);

        // Timestamps are wall-clock (DateTimeOffset.UtcNow at sample time), so they must be
        // monotonically non-decreasing regardless of how fast sim-time itself is moving.
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].TimestampUtc >= samples[i - 1].TimestampUtc);
        }

        // At this compression factor each real-time tick can skip over a large span of sim-time,
        // so narrow features (like the few-seconds-wide touchdown spike) are not reliably hit -
        // that is already covered deterministically in FakeFlightInterpolatorTests. What this
        // integration test can reliably assert is that the replay actually moved: it starts on
        // the ground and reaches an airborne, climbed-out state before the window closes.
        Assert.Contains(samples, s => s.OnGround && s.ParkingBrakeSet);
        Assert.Contains(samples, s => !s.OnGround && s.AltitudeAglFt > 1000);
    }

    [Fact]
    public async Task Replay_WithLoopEnabled_RestartsFromTheBeginning()
    {
        await using var source = new FakeSimSource(FastOptions(loop: true));
        await source.StartAsync(CancellationToken.None);

        // Long enough, at this compression factor, to complete the ~93 minute script multiple
        // times over - if looping did not work, altitude would simply climb once and then hold.
        var samples = await CollectAsync(source, TimeSpan.FromSeconds(6));

        await source.StopAsync(CancellationToken.None);

        var climbCount = CountRisingEdges(samples, s => s.AltitudeAglFt > 20000);
        Assert.True(climbCount >= 2, $"expected the replay to climb past 20000ft more than once when looping, saw {climbCount} times");
    }

    private static int CountRisingEdges(IReadOnlyList<TelemetrySample> samples, Func<TelemetrySample, bool> predicate)
    {
        var count = 0;
        var wasAbove = false;
        foreach (var sample in samples)
        {
            var isAbove = predicate(sample);
            if (isAbove && !wasAbove)
            {
                count++;
            }
            wasAbove = isAbove;
        }
        return count;
    }

    private static async Task<List<TelemetrySample>> CollectAsync(ISimSource source, TimeSpan duration)
    {
        var samples = new List<TelemetrySample>();
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            while (source.Telemetry.TryRead(out var sample))
            {
                samples.Add(sample);
            }

            await Task.Delay(5);
        }

        return samples;
    }
}

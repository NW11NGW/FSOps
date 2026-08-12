using System.Text.Json;
using FSOps.Core.Flights;
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

    /// <summary>
    /// K29. Before <see cref="FakeSimSource"/> reported its true combined simulation rate, an
    /// accelerated dev/test replay's <see cref="TelemetrySample.SimulationRate"/> stayed at
    /// whatever the fixture's own keyframes said (1.0 for a real-time-recorded fixture like this
    /// one) even though <see cref="FakeSimSourceOptions.TimeCompressionFactor"/> was compressing
    /// wall-clock time by 100,000x. <see cref="FlightIntegrityMonitor"/> trusts the reported rate
    /// to normalise its impossible-ground-speed check, so fed that lie it saw ~93 minutes of real
    /// flight distance covered in a couple of real seconds with no rate to explain it, and flagged
    /// a position jump - making every accelerated replay (the project's whole test/dev strategy)
    /// unpayable. This is the fix proven end to end: run the actual accelerated source, feed its
    /// real emitted samples (real wall-clock timestamps, not synthetic ones) straight into the
    /// monitor exactly as <c>FlightLifecycleService</c> does, and the sector must come out payable.
    /// </summary>
    [Fact]
    public async Task Replay_AtHighCompression_ReportsTrueCombinedRate_SoTheSectorStaysPayable()
    {
        await using var source = new FakeSimSource(FastOptions());
        await source.StartAsync(CancellationToken.None);

        var samples = await CollectAsync(source, TimeSpan.FromSeconds(6));

        await source.StopAsync(CancellationToken.None);

        Assert.NotEmpty(samples);

        var monitor = new FlightIntegrityMonitor();
        foreach (var sample in samples)
        {
            monitor.Observe(ToFlightSample(sample));
        }

        // FastOptions() compresses by 100,000x and this fixture's own keyframes all report 1.0,
        // so every sample's reported rate should reflect the compression factor exactly.
        Assert.True(monitor.ElevatedSimRateDetected);
        Assert.Equal(100_000.0, monitor.MaxSimulationRateObserved);
        // The whole point: an accelerated replay must not be mistaken for a teleport.
        Assert.False(monitor.PositionJumpDetected);
        Assert.False(monitor.SlewDetected);
        Assert.False(monitor.SectorInvalidForPayment);
    }

    /// <summary>
    /// K29's other half, first part. At normal (unaccelerated) speed the pipeline must not explain a
    /// genuine position jump away: it must report a simulation rate of exactly 1.0, so the monitor's
    /// rate normalisation is a no-op, and the samples it emits must genuinely carry the impossible
    /// position transition rather than smoothing it out somewhere in the interpolator.
    /// <para>
    /// This asserts that and only that, because it is all a 1x run can prove in a couple of seconds
    /// of wall clock. Whether the monitor then CONDEMNS the sector is a separate question with a
    /// separate answer: a jump is only believed once there is real flight either side of it to
    /// corroborate both ends, which at 1x would take minutes of test runtime to supply. That half is
    /// asserted end to end by the test below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replay_AtNormalRate_ReportsRateOneAndEmitsTheImpossiblePositionTransition()
    {
        var scriptPath = WriteCorruptedTeleportScript();
        try
        {
            var options = new FakeSimSourceOptions
            {
                ReplayFilePath = scriptPath,
                TimeCompressionFactor = 1.0,
                SampleInterval = TimeSpan.FromMilliseconds(20),
                Loop = false,
            };

            await using var source = new FakeSimSource(options);
            await source.StartAsync(CancellationToken.None);

            // The whole corrupted script spans 1 second of sim-time at 1x, so give real wall-clock
            // time to actually play through it.
            var samples = await CollectAsync(source, TimeSpan.FromSeconds(2));

            await source.StopAsync(CancellationToken.None);

            Assert.NotEmpty(samples);
            Assert.All(samples, s => Assert.Equal(1.0, s.SimulationRate));
            Assert.All(samples, s => Assert.False(s.IsSlewActive));

            var fastestImpliedKt = 0.0;
            for (var i = 1; i < samples.Count; i++)
            {
                var elapsedHours = (samples[i].TimestampUtc - samples[i - 1].TimestampUtc).TotalHours;
                if (elapsedHours <= 0)
                {
                    continue;
                }

                var distanceNm = FSOps.Core.Planning.GreatCircle.DistanceNm(
                    samples[i - 1].LatitudeDeg, samples[i - 1].LongitudeDeg,
                    samples[i].LatitudeDeg, samples[i].LongitudeDeg);
                fastestImpliedKt = Math.Max(fastestImpliedKt, distanceNm / elapsedHours);
            }

            Assert.True(fastestImpliedKt > FlightIntegrityMonitor.ImpossibleGroundSpeedKt,
                $"expected the corrupted replay to emit an impossible transition; fastest was {fastestImpliedKt:N0} kt");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    /// <summary>
    /// K29's other half, second part, and the anti-cheat guarantee proven end to end: a genuine
    /// reposition surrounded by ordinary flight, played through the real <see cref="FakeSimSource"/>
    /// pipeline with real wall-clock timestamps exactly as <c>FlightLifecycleService</c> receives
    /// them, must still be detected and must still make the sector unpayable.
    /// <para>
    /// The script carries a hundred seconds of ordinary cruise either side of the jump, because that
    /// is what a real reposition always has around it and what the monitor now requires before it
    /// will condemn a sector - a single unsupported observation used to be enough, and that is what
    /// voided a clean two-hour flight. Compression keeps the whole thing to a few seconds of wall
    /// clock; the monitor normalises it out, and detection has to survive that normalisation, which
    /// is a strictly stronger statement than the same jump at 1x.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Replay_TeleportSurroundedByNormalFlight_IsDetectedAndMakesTheSectorUnpayable()
    {
        var scriptPath = WriteCorroboratedTeleportScript();
        try
        {
            var options = new FakeSimSourceOptions
            {
                ReplayFilePath = scriptPath,
                TimeCompressionFactor = 60.0,
                SampleInterval = TimeSpan.FromMilliseconds(20),
                Loop = false,
            };

            await using var source = new FakeSimSource(options);
            await source.StartAsync(CancellationToken.None);

            // 201 seconds of sim-time at 60x is a little over three seconds of wall clock.
            var samples = await CollectAsync(source, TimeSpan.FromSeconds(6));

            await source.StopAsync(CancellationToken.None);

            Assert.NotEmpty(samples);

            var monitor = new FlightIntegrityMonitor();
            foreach (var sample in samples)
            {
                monitor.Observe(ToFlightSample(sample));
            }

            Assert.True(monitor.PositionJumpDetected);
            Assert.True(monitor.SectorInvalidForPayment);
            // Caught on position data alone - slew was never reported, and the elevated rate is the
            // test harness compressing the run, not something the "player" did to gain anything.
            Assert.False(monitor.SlewDetected);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    private static FlightTelemetrySample ToFlightSample(TelemetrySample s) => new(
        s.TimestampUtc, s.LatitudeDeg, s.LongitudeDeg, s.AltitudeMslFt, s.AltitudeAglFt,
        s.IndicatedAirspeedKt, s.GroundSpeedKt, s.VerticalSpeedFpm, s.TrueHeadingDeg, s.MagneticHeadingDeg,
        s.OnGround, s.EngineRunning, s.ParkingBrakeSet, s.GForce, s.TouchdownNormalVelocityFps, s.TotalFuelKg,
        s.AircraftTitle, s.AtcModel, s.AtcType, s.SimulationRate, s.IsSlewActive);

    private static string WriteCorruptedTeleportScript()
    {
        var script = new FakeFlightScript
        {
            Aircraft = new FakeAircraft { Title = "Test Aircraft", AtcModel = "TEST", AtcType = "Test" },
            Keyframes = new List<FakeKeyframe>
            {
                new()
                {
                    TSeconds = 0, Phase = "Cruise", LatitudeDeg = 51.1, LongitudeDeg = -0.2,
                    AltitudeMslFt = 35000, AltitudeAglFt = 34800, IndicatedAirspeedKt = 290,
                    GroundSpeedKt = 460, TrueHeadingDeg = 150, MagneticHeadingDeg = 148,
                    OnGround = false, EngineRunning = true, GForce = 1.0, TotalFuelKg = 7000,
                    SimulationRate = 1.0,
                },
                new()
                {
                    // No slew reported, no elevated rate - position data alone must trip the
                    // backstop, same as FlightIntegrityMonitorTests'
                    // Observe_PositionJumpAtNormalSimRateWithNoSlewReported_IsStillDetected.
                    TSeconds = 1, Phase = "Cruise", LatitudeDeg = 41.3, LongitudeDeg = 2.1,
                    AltitudeMslFt = 35000, AltitudeAglFt = 34800, IndicatedAirspeedKt = 290,
                    GroundSpeedKt = 460, TrueHeadingDeg = 150, MagneticHeadingDeg = 148,
                    OnGround = false, EngineRunning = true, GForce = 1.0, TotalFuelKg = 7000,
                    SimulationRate = 1.0,
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"fsops-teleport-script-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(script, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }

    /// <summary>
    /// The same London-to-Barcelona teleport, but with a hundred seconds of ordinary 460 kt cruise
    /// on each side of it - what a real reposition mid-sector actually looks like, and what the
    /// integrity monitor needs in order to tell one from a burst of bad telemetry.
    /// </summary>
    private static string WriteCorroboratedTeleportScript()
    {
        // 460 kt for 100 seconds is ~12.8 nm, a little over a fifth of a degree of latitude.
        const double cruiseDegreesPer100Seconds = 460.0 / 3600.0 * 100.0 / 60.0;

        static FakeKeyframe Cruise(double tSeconds, double latitudeDeg, double longitudeDeg) => new()
        {
            TSeconds = tSeconds, Phase = "Cruise", LatitudeDeg = latitudeDeg, LongitudeDeg = longitudeDeg,
            AltitudeMslFt = 35000, AltitudeAglFt = 34800, IndicatedAirspeedKt = 290,
            GroundSpeedKt = 460, TrueHeadingDeg = 360, MagneticHeadingDeg = 358,
            OnGround = false, EngineRunning = true, GForce = 1.0, TotalFuelKg = 7000,
            SimulationRate = 1.0,
        };

        var script = new FakeFlightScript
        {
            Aircraft = new FakeAircraft { Title = "Test Aircraft", AtcModel = "TEST", AtcType = "Test" },
            Keyframes = new List<FakeKeyframe>
            {
                Cruise(0, 51.1, -0.2),
                Cruise(100, 51.1 + cruiseDegreesPer100Seconds, -0.2),

                // ~600 nm in a single sim-second, with no slew reported and nothing else out of the
                // ordinary - position data alone has to carry this.
                Cruise(101, 41.3, 2.1),

                Cruise(201, 41.3 + cruiseDegreesPer100Seconds, 2.1),
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"fsops-corroborated-teleport-script-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(script, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
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

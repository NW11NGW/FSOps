using System.Text.Json;
using FSOps.Core.Flights;
using FSOps.Sim;
using FSOps.Sim.Fake;

namespace FSOps.Core.Tests.Flights;

public class FlightIntegrityMonitorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FakeFlightScript LoadReplayScript()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fake", "Replays", "egkk-lebl.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FakeFlightScript>(json, JsonOptions)!;
    }

    /// <summary>Maps the sim's telemetry record onto the Core-local one, stamping the timestamp
    /// from simulated elapsed seconds - same convention as FlightPhaseStateMachineTests - with an
    /// optional simulation-rate override for the "elevated rate during a normal flight" scenario,
    /// and an optional position offset for splicing a teleport into an otherwise real flight.</summary>
    private static FlightTelemetrySample ToFlightSample(
        TelemetrySample s, double simSeconds, double simulationRateOverride = double.NaN, double latitudeOffsetDeg = 0) => new(
        Base + TimeSpan.FromSeconds(simSeconds),
        s.LatitudeDeg + latitudeOffsetDeg, s.LongitudeDeg, s.AltitudeMslFt, s.AltitudeAglFt,
        s.IndicatedAirspeedKt, s.GroundSpeedKt, s.VerticalSpeedFpm,
        s.TrueHeadingDeg, s.MagneticHeadingDeg, s.OnGround, s.EngineRunning, s.ParkingBrakeSet,
        s.GForce, s.TouchdownNormalVelocityFps, s.TotalFuelKg, s.AircraftTitle, s.AtcModel, s.AtcType,
        double.IsNaN(simulationRateOverride) ? s.SimulationRate : simulationRateOverride, s.IsSlewActive);

    /// <summary>Hand-built sample for the synthetic tests below - position and telemetry values
    /// deliberately minimal since each test only exercises one of the three signals at a time.</summary>
    private static FlightTelemetrySample Sample(
        double t, double latDeg = 0, double lonDeg = 0, double simulationRate = 1.0, bool isSlewActive = false) =>
        new(Base + TimeSpan.FromSeconds(t), latDeg, lonDeg, 5000, 4800, 250, 250, 0, 90, 90,
            false, true, false, 1.0, 0, 5000, "Test Aircraft", "TEST", "Test", simulationRate, isSlewActive);

    /// <summary>
    /// Feeds <paramref name="seconds"/> of ordinary cruise, one sample a second, tracking north from
    /// a starting position at a routine ground speed. Returns the time of the last sample fed, so a
    /// caller can continue the timeline. This is what CORROBORATION looks like: the monitor now
    /// requires real tracked movement either side of a suspected jump before it will condemn a
    /// sector, so any test about a jump has to supply the flight that surrounds it.
    /// </summary>
    private static double FlyNormally(
        FlightIntegrityMonitor monitor, double startLatDeg, double lonDeg, double fromSeconds, double seconds,
        double groundSpeedKt = 460.0)
    {
        // ~60 nm per degree of latitude.
        var degreesPerSecond = groundSpeedKt / 3600.0 / 60.0;
        var t = fromSeconds;
        for (var i = 0; i <= seconds; i++, t++)
        {
            monitor.Observe(Sample(t, latDeg: startLatDeg + degreesPerSecond * i, lonDeg: lonDeg));
        }

        return t - 1;
    }

    [Fact]
    public void Observe_SimRateAboveOne_DetectsAndRecordsTheMaximum()
    {
        var monitor = new FlightIntegrityMonitor();

        monitor.Observe(Sample(0, simulationRate: 1.0));
        monitor.Observe(Sample(1, simulationRate: 2.0));
        monitor.Observe(Sample(2, simulationRate: 4.0));
        monitor.Observe(Sample(3, simulationRate: 2.0));

        Assert.True(monitor.ElevatedSimRateDetected);
        Assert.Equal(4.0, monitor.MaxSimulationRateObserved);
        Assert.False(monitor.SlewDetected);
        Assert.False(monitor.PositionJumpDetected);
        // Elevated sim rate alone must never block payment - only slew/a position jump do.
        Assert.False(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void Observe_SlewActive_DetectsAndInvalidatesTheSectorForPayment()
    {
        var monitor = new FlightIntegrityMonitor();

        monitor.Observe(Sample(0));
        monitor.Observe(Sample(1, isSlewActive: true));
        monitor.Observe(Sample(2));

        Assert.True(monitor.SlewDetected);
        Assert.False(monitor.ElevatedSimRateDetected);
        Assert.False(monitor.PositionJumpDetected);
        Assert.True(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void Observe_PositionJumpAtNormalSimRateWithNoSlewReported_IsStillDetected()
    {
        var monitor = new FlightIntegrityMonitor();

        // London area to Barcelona area (~600 nm) in one second, at simulation rate 1.0 with slew
        // never reported active - the backstop must catch this on position data alone.
        //
        // The jump is surrounded by ordinary tracked flight because that is what a real reposition
        // always looks like, and because the monitor deliberately will not condemn a sector on a
        // single unsupported observation any more: one uninitialised opening fix in a real flight
        // did exactly that and voided a clean two-hour sector. See FlightIntegrityMonitor.Observe.
        var beforeJump = FlyNormally(monitor, startLatDeg: 51.1, lonDeg: -0.2, fromSeconds: 0, seconds: 90);
        monitor.Observe(Sample(beforeJump + 1, latDeg: 41.3, lonDeg: 2.1, simulationRate: 1.0));
        FlyNormally(monitor, startLatDeg: 41.3, lonDeg: 2.1, fromSeconds: beforeJump + 2, seconds: 90);

        Assert.True(monitor.PositionJumpDetected);
        Assert.False(monitor.SlewDetected);
        Assert.False(monitor.ElevatedSimRateDetected);
        Assert.True(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void Observe_TeleportSplicedIntoTheMiddleOfARealFlight_IsDetectedAndVoidsTheSector()
    {
        // The anti-cheat guarantee, asserted in its own right: a real recorded flight with a genuine
        // teleport spliced into the middle of it, so there is unambiguous normal flight on BOTH
        // sides of the discontinuity. Nothing about the corroboration rule may be allowed to let
        // this through.
        var script = LoadReplayScript();
        var duration = script.Keyframes[^1].TSeconds;
        var monitor = new FlightIntegrityMonitor();

        const double teleportAtSeconds = 2500;
        const double teleportDistanceDeg = 10; // ~600 nm north, in one second.

        for (double t = 0; t <= duration; t += 1)
        {
            var raw = FakeFlightInterpolator.Sample(script, t);
            monitor.Observe(ToFlightSample(raw, t, latitudeOffsetDeg: t >= teleportAtSeconds ? teleportDistanceDeg : 0));
        }

        Assert.True(monitor.PositionJumpDetected);
        Assert.True(monitor.SectorInvalidForPayment);
        // Detected purely from position - neither of the other two signals was present.
        Assert.False(monitor.SlewDetected);
        Assert.False(monitor.ElevatedSimRateDetected);
    }

    [Fact]
    public void Observe_ElevatedSimRateWithProportionallyLargerPositionDelta_DoesNotFalselyFlagAPositionJump()
    {
        var monitor = new FlightIntegrityMonitor();

        // 460 kt cruise groundspeed at 4x simulation rate: four sim-seconds' worth of distance
        // passes every wall-clock second, so the RAW (un-normalised) implied speed would be
        // ~1,840 kt - comfortably over the 1,200 kt threshold - yet this is an entirely ordinary
        // accelerated cruise, and must not be mistaken for a jump.
        const double normalCruiseGsKt = 460.0;
        const double simRate = 4.0;
        var nmCoveredPerWallClockSecond = normalCruiseGsKt / 3600.0 * simRate;
        var degreesLatitudePerSecond = nmCoveredPerWallClockSecond / 60.0; // ~60 nm per degree of latitude

        monitor.Observe(Sample(0, latDeg: 0, simulationRate: simRate));
        monitor.Observe(Sample(1, latDeg: degreesLatitudePerSecond, simulationRate: simRate));

        Assert.True(monitor.ElevatedSimRateDetected);
        Assert.Equal(simRate, monitor.MaxSimulationRateObserved);
        Assert.False(monitor.PositionJumpDetected);
        Assert.False(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void Observe_FullNormalReplay_TriggersNoIntegrityFindings()
    {
        var script = LoadReplayScript();
        var duration = script.Keyframes[^1].TSeconds;
        var monitor = new FlightIntegrityMonitor();

        for (double t = 0; t <= duration; t += 1)
        {
            var raw = FakeFlightInterpolator.Sample(script, t);
            monitor.Observe(ToFlightSample(raw, t));
        }

        Assert.False(monitor.ElevatedSimRateDetected);
        Assert.Equal(1.0, monitor.MaxSimulationRateObserved);
        Assert.False(monitor.SlewDetected);
        Assert.False(monitor.PositionJumpDetected);
        Assert.False(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void FullReplayWithElevatedSimRateThroughApproachAndLanding_StillCapturesLandingQuality()
    {
        var script = LoadReplayScript();
        var duration = script.Keyframes[^1].TSeconds;
        var machine = new FlightPhaseStateMachine();
        var integrity = new FlightIntegrityMonitor();

        for (double t = 0; t <= duration; t += 1)
        {
            var raw = FakeFlightInterpolator.Sample(script, t);
            // Elevated rate from well before the descent through to the end of the flight - the
            // scenario the requirement is about: a player speeding up final descent and touching
            // down at 4x. Position deltas are unaffected (they come straight from the fixture's
            // real-time-scale keyframes), so this alone must not read as a position jump either.
            var simulationRate = t >= 4800 ? 4.0 : 1.0;
            var sample = ToFlightSample(raw, t, simulationRate);
            machine.Advance(sample);
            integrity.Observe(sample);
        }

        Assert.True(integrity.ElevatedSimRateDetected);
        Assert.Equal(4.0, integrity.MaxSimulationRateObserved);
        Assert.False(integrity.SlewDetected);
        Assert.False(integrity.PositionJumpDetected);

        Assert.NotNull(machine.FirstTouchdown);
        Assert.Equal(FlightPhase.Shutdown, machine.CurrentPhase);
        // Same plausible range FlightPhaseStateMachineTests uses for this fixture's touchdown -
        // landing quality is scored identically regardless of the elevated sim rate.
        Assert.InRange(machine.FirstTouchdown!.Fpm!.Value, 10, 400);
    }

    [Fact]
    public void Observe_SingleGarbageOpeningFix_IsAbsorbedRatherThanVoidingTheFlight()
    {
        var monitor = new FlightIntegrityMonitor();

        // One uninitialised fix, then the aircraft's real position at the stand and a normal flight.
        monitor.Observe(Sample(0, latDeg: 0, lonDeg: 90));
        FlyNormally(monitor, startLatDeg: 51.385, lonDeg: -2.718, fromSeconds: 1, seconds: 300);

        Assert.False(monitor.PositionJumpDetected);
        Assert.False(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void Observe_SeveralConsecutiveGarbageOpeningFixes_AreAbsorbedRatherThanVoidingTheFlight()
    {
        var monitor = new FlightIntegrityMonitor();

        // The recorded evidence is 15-second position SNAPSHOTS, but the monitor sees every sample -
        // roughly 5 Hz - so the decimated record cannot say whether the bad fix lasted one frame or
        // several seconds' worth of them. A run of them corroborates itself (the aircraft "isn't
        // moving"), which is exactly the shape that could smuggle a false positive past a rule based
        // only on the neighbouring transitions. It must not.
        for (var i = 0; i < 50; i++)
        {
            monitor.Observe(Sample(i * 0.2, latDeg: 0, lonDeg: 90));
        }

        FlyNormally(monitor, startLatDeg: 51.385, lonDeg: -2.718, fromSeconds: 20, seconds: 300);

        Assert.False(monitor.PositionJumpDetected);
        Assert.False(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void Observe_BareUncorroboratedPairOfSamples_IsNotEnoughToVoidASector()
    {
        var monitor = new FlightIntegrityMonitor();

        // Two samples and nothing else. One of them is wrong, but nothing in the data says which,
        // and the monitor must fail OPEN rather than closed when it cannot tell - paying for
        // something it is unsure about is a far smaller harm than taking a sector off a pilot who
        // flew it. A real reposition is never this context-free; see the teleport tests above.
        monitor.Observe(Sample(0, latDeg: 51.1, lonDeg: -0.2));
        monitor.Observe(Sample(1, latDeg: 41.3, lonDeg: 2.1));

        Assert.False(monitor.PositionJumpDetected);
        Assert.False(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void Observe_WithAnExpectedStart_GarbageOpeningFixIsDiscardedButATeleportFromTheStandIsStillCaught()
    {
        var stand = (Lat: 51.385, Lon: -2.718);

        var glitched = new FlightIntegrityMonitor(stand);
        glitched.Observe(Sample(0, latDeg: 0, lonDeg: 90));
        FlyNormally(glitched, startLatDeg: stand.Lat, lonDeg: stand.Lon, fromSeconds: 1, seconds: 200);
        Assert.False(glitched.PositionJumpDetected);

        // Same expected start, but this time the opening fix IS the stand and the aircraft is then
        // moved 600 nm. Supplying an expected start narrows where the check has to fail open; it
        // must not hand out a blanket exemption for the opening moments.
        var teleported = new FlightIntegrityMonitor(stand);
        var beforeJump = FlyNormally(teleported, startLatDeg: stand.Lat, lonDeg: stand.Lon, fromSeconds: 0, seconds: 90);
        teleported.Observe(Sample(beforeJump + 1, latDeg: 41.3, lonDeg: 2.1));
        FlyNormally(teleported, startLatDeg: 41.3, lonDeg: 2.1, fromSeconds: beforeJump + 2, seconds: 90);

        Assert.True(teleported.PositionJumpDetected);
        Assert.True(teleported.SectorInvalidForPayment);
    }

    /// <summary>
    /// The regression fixture: the actual recorded telemetry of the first real flight ever flown
    /// with FSOps, EGGD-EGPH on 2026-08-12. It was flown cleanly - no slew, no time acceleration,
    /// no repositioning - and FSOps voided it for payment anyway, on the strength of one
    /// uninitialised opening fix that put the aircraft 5,505 nm away in the Bay of Bengal twelve
    /// minutes before it moved.
    /// <para>
    /// The fixture is the persisted 15-second PositionSnapshot stream, which is a decimation of what
    /// the live monitor actually saw (every sample, roughly 5 Hz); the garbage-to-real transition
    /// therefore happened over a SHORTER gap in life than it does here, implying an even higher
    /// speed. Reproducing the defect from the decimated record is sufficient because the flag it
    /// sets is latched: 1,310,308 kt over 15.1 s trips the old check just as surely.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observe_RealEggdToEgphFlight_ProducesNoIntegrityFindings(bool withExpectedStart)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Flights", "Fixtures", "eggd-egph-20260812-snapshots.json");
        var fixture = JsonSerializer.Deserialize<RecordedFlight>(File.ReadAllText(path), JsonOptions)!;

        // Bristol's stand, as the flight's own second (first believable) fix recorded it.
        var monitor = withExpectedStart ? new FlightIntegrityMonitor((51.385, -2.718)) : new FlightIntegrityMonitor();

        foreach (var snapshot in fixture.Snapshots)
        {
            monitor.Observe(new FlightTelemetrySample(
                snapshot.Utc, snapshot.Lat, snapshot.Lon, snapshot.AltAglFt + 100, snapshot.AltAglFt,
                snapshot.GsKt, snapshot.GsKt, snapshot.VsFpm, 0, 0,
                false, true, false, 1.0, 0, 5000, "Test Aircraft", "TEST", "Test", 1.0, false));
        }

        Assert.False(monitor.PositionJumpDetected);
        Assert.False(monitor.SlewDetected);
        Assert.False(monitor.ElevatedSimRateDetected);
        Assert.False(monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void RealEggdToEgphFixture_StillContainsTheOpeningFixThatCausedTheDefect()
    {
        // Guards the fixture itself: if this ever stops holding, the regression test above has
        // quietly stopped testing anything and would pass for the wrong reason.
        var path = Path.Combine(AppContext.BaseDirectory, "Flights", "Fixtures", "eggd-egph-20260812-snapshots.json");
        var fixture = JsonSerializer.Deserialize<RecordedFlight>(File.ReadAllText(path), JsonOptions)!;

        var first = fixture.Snapshots[0];
        var second = fixture.Snapshots[1];

        var distanceNm = FSOps.Core.Planning.GreatCircle.DistanceNm(first.Lat, first.Lon, second.Lat, second.Lon);
        var impliedKt = distanceNm / (second.Utc - first.Utc).TotalHours;

        Assert.InRange(distanceNm, 5000, 6000);
        Assert.True(impliedKt > FlightIntegrityMonitor.ImpossibleGroundSpeedKt * 1000,
            $"expected the recorded opening fix to imply an absurd speed; it implied {impliedKt:N0} kt");

        // ...and that nothing else in the whole flight comes anywhere near the threshold, which is
        // why the threshold itself was never the problem.
        for (var i = 2; i < fixture.Snapshots.Count; i++)
        {
            var from = fixture.Snapshots[i - 1];
            var to = fixture.Snapshots[i];
            var kt = FSOps.Core.Planning.GreatCircle.DistanceNm(from.Lat, from.Lon, to.Lat, to.Lon) / (to.Utc - from.Utc).TotalHours;
            Assert.InRange(kt, 0, 600);
        }
    }

    private sealed record RecordedFlight(string Source, IReadOnlyList<RecordedSnapshot> Snapshots);

    private sealed record RecordedSnapshot(
        DateTimeOffset Utc, double Lat, double Lon, double AltAglFt, double GsKt, double VsFpm, string Phase);
}

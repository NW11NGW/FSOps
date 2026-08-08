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
    /// optional simulation-rate override for the "elevated rate during a normal flight" scenario.</summary>
    private static FlightTelemetrySample ToFlightSample(TelemetrySample s, double simSeconds, double simulationRateOverride = double.NaN) => new(
        Base + TimeSpan.FromSeconds(simSeconds),
        s.LatitudeDeg, s.LongitudeDeg, s.AltitudeMslFt, s.AltitudeAglFt,
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
        monitor.Observe(Sample(0, latDeg: 51.1, lonDeg: -0.2, simulationRate: 1.0));
        monitor.Observe(Sample(1, latDeg: 41.3, lonDeg: 2.1, simulationRate: 1.0));

        Assert.True(monitor.PositionJumpDetected);
        Assert.False(monitor.SlewDetected);
        Assert.False(monitor.ElevatedSimRateDetected);
        Assert.True(monitor.SectorInvalidForPayment);
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
        Assert.InRange(machine.FirstTouchdown!.Fpm, 10, 400);
    }
}

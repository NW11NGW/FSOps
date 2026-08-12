using System.Text.Json;
using FSOps.Core.Flights;
using FSOps.Sim;
using FSOps.Sim.Fake;

namespace FSOps.Core.Tests.Flights;

public class FlightPhaseStateMachineTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FakeFlightScript LoadReplayScript()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fake", "Replays", "egkk-lebl.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FakeFlightScript>(json, JsonOptions)!;
    }

    /// <summary>
    /// Maps the sim's telemetry record onto the Core-local one, stamping the timestamp from
    /// simulated elapsed seconds rather than wall-clock "now" (which is how FakeSimSource marks
    /// its own samples) - the state machine's hysteresis is driven entirely by sample timestamps,
    /// so this is what lets a whole scripted flight be replayed deterministically and instantly.
    /// </summary>
    private static FlightTelemetrySample ToFlightSample(TelemetrySample s, double simSeconds) => new(
        Base + TimeSpan.FromSeconds(simSeconds),
        s.LatitudeDeg, s.LongitudeDeg, s.AltitudeMslFt, s.AltitudeAglFt,
        s.IndicatedAirspeedKt, s.GroundSpeedKt, s.VerticalSpeedFpm,
        s.TrueHeadingDeg, s.MagneticHeadingDeg, s.OnGround, s.EngineRunning, s.ParkingBrakeSet,
        s.GForce, s.TouchdownNormalVelocityFps, s.TotalFuelKg, s.AircraftTitle, s.AtcModel, s.AtcType,
        s.SimulationRate, s.IsSlewActive);

    /// <summary>Hand-built sample for the synthetic edge-case tests, so they need no dependency on the replay script.</summary>
    private static FlightTelemetrySample Sample(
        double t,
        double aglFt = 5000,
        double vsFpm = 0,
        double gsKt = 0,
        bool onGround = false,
        bool engineRunning = true,
        bool brake = false,
        double gForce = 1.0,
        double touchdownFps = 0,
        double headingDeg = 90) =>
        new(Base + TimeSpan.FromSeconds(t), 0, 0, aglFt + 200, aglFt, gsKt, gsKt, vsFpm, headingDeg, headingDeg,
            onGround, engineRunning, brake, gForce, touchdownFps, 5000, "Test Aircraft", "TEST", "Test", 1.0, false);

    [Fact]
    public void FullReplay_ReachesEveryPhaseInOrder_WithSaneOooiAndAPlausibleTouchdown()
    {
        var script = LoadReplayScript();
        var duration = script.Keyframes[^1].TSeconds;
        var machine = new FlightPhaseStateMachine();

        var observedPhases = new List<FlightPhase> { FlightPhase.Preflight };

        for (double t = 0; t <= duration; t += 1)
        {
            var raw = FakeFlightInterpolator.Sample(script, t);
            var result = machine.Advance(ToFlightSample(raw, t));
            if (result.PhaseChanged)
            {
                observedPhases.Add(result.Phase);
            }
        }

        var expectedOrder = new[]
        {
            FlightPhase.Preflight, FlightPhase.TaxiOut, FlightPhase.TakeoffRoll, FlightPhase.Climb,
            FlightPhase.Cruise, FlightPhase.Descent, FlightPhase.Approach, FlightPhase.Landed,
            FlightPhase.TaxiIn, FlightPhase.Shutdown,
        };
        Assert.Equal(expectedOrder, observedPhases);
        Assert.Equal(FlightPhase.Shutdown, machine.CurrentPhase);

        Assert.NotNull(machine.OutUtc);
        Assert.NotNull(machine.OffUtc);
        Assert.NotNull(machine.OnUtc);
        Assert.NotNull(machine.InUtc);
        Assert.True(machine.OutUtc < machine.OffUtc);
        Assert.True(machine.OffUtc < machine.OnUtc);
        Assert.True(machine.OnUtc <= machine.InUtc);

        Assert.NotNull(machine.FirstTouchdown);
        Assert.Equal(0, machine.BounceCount);
        // The script's exact touchdown spike is 2.3 fps (138 fpm); 1-second sampling can land a
        // touch either side of that, but it must stay in a plausible smooth-landing range.
        Assert.InRange(machine.FirstTouchdown!.Fpm!.Value, 10, 400);
    }

    [Fact]
    public void Advance_NoisyVerticalSpeedDuringClimb_DoesNotFlapIntoCruisePrematurely()
    {
        var machine = new FlightPhaseStateMachine();
        Advance(machine, Sample(0, onGround: true, gsKt: 0, brake: true));
        Advance(machine, Sample(1, onGround: true, gsKt: 5, brake: false)); // TaxiOut
        Advance(machine, Sample(2, onGround: true, gsKt: 45)); // TakeoffRoll
        Advance(machine, Sample(3, onGround: false, vsFpm: 1500)); // Climb

        // In-band VS readings interrupted by a spike back out every few seconds - never 20
        // unbroken seconds in-band, so Cruise must never trigger.
        for (var t = 4; t < 60; t++)
        {
            var vs = t % 5 == 0 ? 1200.0 : 100.0;
            var result = Advance(machine, Sample(t, vsFpm: vs));
            Assert.False(result.PhaseChanged && result.Phase == FlightPhase.Cruise, $"Cruise fired prematurely at t={t}");
        }

        Assert.Equal(FlightPhase.Climb, machine.CurrentPhase);

        // Now a genuinely clean, sustained run - Cruise should fire exactly once, after the full window.
        var sawCruise = false;
        for (var t = 60; t < 90; t++)
        {
            var result = Advance(machine, Sample(t, vsFpm: 50));
            if (result.PhaseChanged)
            {
                Assert.False(sawCruise, "Cruise fired more than once");
                Assert.Equal(FlightPhase.Cruise, result.Phase);
                sawCruise = true;
            }
        }

        Assert.True(sawCruise, "expected a sustained level-off to eventually reach Cruise");
        Assert.Equal(FlightPhase.Cruise, machine.CurrentPhase);
    }

    [Fact]
    public void Advance_BrieflyAirborneAfterTouchdown_IsTreatedAsABounceNotAGoAround()
    {
        var machine = new FlightPhaseStateMachine();
        DriveToApproach(machine);

        // First contact - a soft touch.
        Advance(machine, Sample(100, aglFt: 0, gsKt: 130, onGround: true, touchdownFps: 1.0, gForce: 1.2));
        Assert.Equal(FlightPhase.Landed, machine.CurrentPhase);

        // Bounces back into the air for 2 seconds - well under the 5s go-around threshold.
        Advance(machine, Sample(101, aglFt: 5, gsKt: 110, onGround: false));
        Advance(machine, Sample(102, aglFt: 3, gsKt: 105, onGround: false));

        // Second, harder contact.
        var second = Advance(machine, Sample(103, aglFt: 0, gsKt: 100, onGround: true, touchdownFps: 4.0, gForce: 1.9));

        Assert.False(second.IsGoAround);
        Assert.Equal(FlightPhase.Landed, machine.CurrentPhase);
        Assert.Equal(2, machine.Touchdowns.Count);
        Assert.Equal(1, machine.BounceCount);

        // First contact stays first even though the second was harder.
        Assert.Equal(60.0, machine.FirstTouchdown!.Fpm!.Value, precision: 3);
        Assert.Equal(240.0, machine.HardestTouchdown!.Fpm!.Value, precision: 3);
        Assert.NotEqual(machine.FirstTouchdown, machine.HardestTouchdown);
    }

    [Fact]
    public void Advance_SustainedAirborneAfterTouchdown_TriggersGoAround_AndTheEventualRealLandingIsScoredCleanly()
    {
        var machine = new FlightPhaseStateMachine();
        DriveToApproach(machine);

        Advance(machine, Sample(100, aglFt: 0, gsKt: 130, onGround: true, touchdownFps: 1.0, gForce: 1.2));
        Assert.Equal(FlightPhase.Landed, machine.CurrentPhase);

        FlightPhaseAdvanceResult? goAroundResult = null;
        for (var t = 101; t <= 108; t++)
        {
            var result = Advance(machine, Sample(t, aglFt: 200, vsFpm: 400, onGround: false));
            if (result.PhaseChanged)
            {
                goAroundResult = result;
                break;
            }
        }

        Assert.NotNull(goAroundResult);
        Assert.True(goAroundResult!.IsGoAround);
        Assert.Equal(FlightPhase.Climb, machine.CurrentPhase);
        Assert.Empty(machine.Touchdowns);
        Assert.Null(machine.OnUtc);

        // Fly the circuit again and land for real - the aborted attempt must not bleed into this landing.
        DriveClimbCruiseDescentApproach(machine, startAt: 110);
        Advance(machine, Sample(300, aglFt: 0, gsKt: 125, onGround: true, touchdownFps: 2.5, gForce: 1.3));

        Assert.Equal(FlightPhase.Landed, machine.CurrentPhase);
        Assert.Single(machine.Touchdowns);
        Assert.Equal(0, machine.BounceCount);
        Assert.Equal(150.0, machine.FirstTouchdown!.Fpm!.Value, precision: 3);
        Assert.NotNull(machine.OnUtc);
    }

    private static void DriveToApproach(FlightPhaseStateMachine machine)
    {
        Advance(machine, Sample(0, onGround: true, gsKt: 0, brake: true));
        Advance(machine, Sample(1, onGround: true, gsKt: 5, brake: false)); // TaxiOut
        Advance(machine, Sample(2, onGround: true, gsKt: 45)); // TakeoffRoll
        Advance(machine, Sample(3, onGround: false, vsFpm: 1500)); // Climb
        DriveClimbCruiseDescentApproach(machine, startAt: 4);
    }

    /// <summary>Assumes the machine is already in Climb (airborne) and drives it through Cruise, Descent and into Approach.</summary>
    private static void DriveClimbCruiseDescentApproach(FlightPhaseStateMachine machine, double startAt)
    {
        var t = startAt;
        for (var i = 0; i < 25; i++, t++)
        {
            Advance(machine, Sample(t, aglFt: 5000, vsFpm: 0)); // sustain level flight -> Cruise
        }

        for (var i = 0; i < 20; i++, t++)
        {
            Advance(machine, Sample(t, aglFt: 5000, vsFpm: -500)); // sustain descent -> Descent
        }

        Advance(machine, Sample(t, aglFt: 2000, vsFpm: -500)); // AGL under threshold -> Approach
    }

    private static FlightPhaseAdvanceResult Advance(FlightPhaseStateMachine machine, FlightTelemetrySample sample) => machine.Advance(sample);
}

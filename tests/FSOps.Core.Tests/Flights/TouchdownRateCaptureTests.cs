using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

/// <summary>
/// Landing rate capture, from the defect the first real flight ever flown with FSOps exposed: the
/// touchdown fired, the position and G-force were captured, and the sink rate was recorded as
/// 0 fpm on a landing two independent tools measured at about -59 fpm. PLANE TOUCHDOWN NORMAL
/// VELOCITY reads zero except in the instant the sim registers the contact, and that instant is not
/// reliably the frame on which SIM ON GROUND first goes true - which is why the G-force peak, which
/// was already being watched for over a window, came out plausible while the rate did not.
/// <para>
/// The part that made this dangerous rather than merely wrong is that a reading that was never taken
/// and a perfect greaser were indistinguishable, and the report card presented the miss with total
/// confidence. Never-measured must read as null, all the way through.
/// </para>
/// </summary>
public class TouchdownRateCaptureTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FlightTelemetrySample Sample(
        double t,
        double aglFt = 5000,
        double vsFpm = 0,
        double gsKt = 0,
        bool onGround = false,
        bool engineRunning = true,
        bool brake = false,
        double gForce = 1.0,
        double touchdownFps = 0) =>
        new(Base + TimeSpan.FromSeconds(t), 0, 0, aglFt + 200, aglFt, gsKt, gsKt, vsFpm, 90, 90,
            onGround, engineRunning, brake, gForce, touchdownFps, 5000, "Test Aircraft", "TEST", "Test", 1.0, false);

    /// <summary>Drives a fresh machine to Approach, ready for a touchdown at t = 100.</summary>
    private static FlightPhaseStateMachine ApproachingMachine(double finalApproachVsFpm = -500)
    {
        var machine = new FlightPhaseStateMachine();
        machine.Advance(Sample(0, onGround: true, gsKt: 0, brake: true));
        machine.Advance(Sample(1, onGround: true, gsKt: 5, brake: false));
        machine.Advance(Sample(2, onGround: true, gsKt: 45));
        machine.Advance(Sample(3, onGround: false, vsFpm: 1500));

        var t = 4.0;
        for (var i = 0; i < 25; i++, t++)
        {
            machine.Advance(Sample(t, aglFt: 5000, vsFpm: 0));
        }

        for (var i = 0; i < 20; i++, t++)
        {
            machine.Advance(Sample(t, aglFt: 5000, vsFpm: -500));
        }

        machine.Advance(Sample(t, aglFt: 2000, vsFpm: -500));

        // The last airborne sample before contact, which is the fallback the rate falls back to.
        machine.Advance(Sample(99, aglFt: 5, vsFpm: finalApproachVsFpm, gsKt: 135));
        return machine;
    }

    [Fact]
    public void Advance_TouchdownSimvarZeroOnTheEdgeButReportedAFrameLater_UsesTheSimsOwnFigure()
    {
        var machine = ApproachingMachine(finalApproachVsFpm: -59);

        // The on-ground edge, with the touchdown simvar still reading zero.
        machine.Advance(Sample(100, aglFt: 0, gsKt: 130, onGround: true, touchdownFps: 0, gForce: 1.016));

        // ...and the sim catching up a fraction of a second later, inside the observation window.
        machine.Advance(Sample(100.2, aglFt: 0, gsKt: 128, onGround: true, touchdownFps: 0.983, gForce: 1.035));

        Assert.NotNull(machine.FirstTouchdown);
        Assert.Equal(TouchdownRateSource.SimTouchdownRate, machine.FirstTouchdown!.FpmSource);
        Assert.Equal(59.0, machine.FirstTouchdown.Fpm!.Value, precision: 1);
        // The G peak from the same window is still picked up, exactly as before.
        Assert.Equal(1.035, machine.FirstTouchdown.GForce, precision: 3);
    }

    [Fact]
    public void Advance_TouchdownSimvarNeverReports_FallsBackToVerticalSpeedAndNeverRecordsZero()
    {
        // This is the recorded flight's exact shape: touchdown detected, G-force captured and
        // plausible, and PLANE TOUCHDOWN NORMAL VELOCITY flat zero from the edge through the whole
        // observation window. The real approach came down through -196 fpm at 34 ft AGL and flared;
        // -59 fpm is what the last airborne frame read.
        var machine = ApproachingMachine(finalApproachVsFpm: -59);

        machine.Advance(Sample(100, aglFt: 0, gsKt: 130, onGround: true, touchdownFps: 0, gForce: 1.0162));
        machine.Advance(Sample(101, aglFt: 0, gsKt: 120, onGround: true, touchdownFps: 0, gForce: 1.0349));
        machine.Advance(Sample(102, aglFt: 0, gsKt: 100, onGround: true, touchdownFps: 0, gForce: 1.01));

        Assert.NotNull(machine.FirstTouchdown);
        Assert.NotEqual(0.0, machine.FirstTouchdown!.Fpm);
        Assert.Equal(59.0, machine.FirstTouchdown.Fpm!.Value, precision: 3);
        Assert.Equal(TouchdownRateSource.VerticalSpeedBeforeContact, machine.FirstTouchdown.FpmSource);
        Assert.Equal(1.0349, machine.FirstTouchdown.GForce, precision: 4);
    }

    [Fact]
    public void Advance_NoTouchdownRateAndNoVerticalSpeedEither_RecordsNotMeasuredRatherThanZero()
    {
        // Nothing to measure the landing with at all. The one outcome that must never be produced
        // here is 0.0, which reads on the report card as the softest landing physically possible.
        var machine = ApproachingMachine(finalApproachVsFpm: 0);

        machine.Advance(Sample(100, aglFt: 0, gsKt: 130, onGround: true, touchdownFps: 0, gForce: 1.02));
        machine.Advance(Sample(101, aglFt: 0, gsKt: 120, onGround: true, touchdownFps: 0, gForce: 1.03));

        Assert.NotNull(machine.FirstTouchdown);
        Assert.Null(machine.FirstTouchdown!.Fpm);
        Assert.Equal(TouchdownRateSource.NotMeasured, machine.FirstTouchdown.FpmSource);
    }

    [Fact]
    public void Advance_HarderSecondContactWithinTheFirstsWindow_DoesNotOverwriteTheFirstContactsRate()
    {
        // The observation window of contact 1 is still open when contact 2 lands. The sim's touchdown
        // figure at that moment belongs to contact 2, and must not reach back into contact 1's record -
        // the first contact is what the sector's landing is scored on.
        var machine = ApproachingMachine(finalApproachVsFpm: -120);

        machine.Advance(Sample(100, aglFt: 0, gsKt: 130, onGround: true, touchdownFps: 1.0, gForce: 1.2));
        machine.Advance(Sample(101, aglFt: 5, gsKt: 110, onGround: false, vsFpm: 300));
        machine.Advance(Sample(102, aglFt: 3, gsKt: 105, onGround: false, vsFpm: -400));
        machine.Advance(Sample(103, aglFt: 0, gsKt: 100, onGround: true, touchdownFps: 4.0, gForce: 1.9));

        Assert.Equal(2, machine.Touchdowns.Count);
        Assert.Equal(60.0, machine.FirstTouchdown!.Fpm!.Value, precision: 3);
        Assert.Equal(240.0, machine.HardestTouchdown!.Fpm!.Value, precision: 3);
    }

    [Fact]
    public void HardestTouchdown_PrefersAMeasuredContactOverAnUnmeasuredOne()
    {
        // An unknown must never outrank a real figure when picking the hardest contact.
        var machine = ApproachingMachine(finalApproachVsFpm: 0);

        // Contact 1: nothing measurable at all.
        machine.Advance(Sample(100, aglFt: 0, gsKt: 130, onGround: true, touchdownFps: 0, gForce: 1.1));
        Assert.Null(machine.FirstTouchdown!.Fpm);

        // Bounce, then a second contact the sim does report a rate for. Airborne samples carry no
        // vertical speed, so contact 2 can only get its figure from the simvar.
        machine.Advance(Sample(104, aglFt: 5, gsKt: 110, onGround: false, vsFpm: 0));
        machine.Advance(Sample(105, aglFt: 3, gsKt: 105, onGround: false, vsFpm: 0));
        machine.Advance(Sample(106, aglFt: 0, gsKt: 100, onGround: true, touchdownFps: 3.0, gForce: 1.6));

        Assert.Equal(2, machine.Touchdowns.Count);
        Assert.NotNull(machine.HardestTouchdown!.Fpm);
        Assert.Equal(180.0, machine.HardestTouchdown.Fpm!.Value, precision: 3);
    }
}

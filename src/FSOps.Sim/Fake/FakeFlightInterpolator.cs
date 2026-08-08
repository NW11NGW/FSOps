namespace FSOps.Sim.Fake;

/// <summary>
/// Turns a scripted flight plus a point in sim-time into a single telemetry sample, linearly
/// interpolating numeric values between the two bracketing keyframes. Kept separate from
/// <see cref="FakeSimSource"/> so it can be unit tested without any timers or threading.
/// </summary>
public static class FakeFlightInterpolator
{
    public static TelemetrySample Sample(FakeFlightScript script, double simSeconds)
    {
        var (a, b, t) = FindBracket(script.Keyframes, simSeconds);
        return Interpolate(script.Aircraft, a, b, t);
    }

    /// <summary>Finds the two keyframes surrounding <paramref name="simSeconds"/> and how far between them it falls (0-1).</summary>
    internal static (FakeKeyframe From, FakeKeyframe To, double Fraction) FindBracket(IReadOnlyList<FakeKeyframe> keyframes, double simSeconds)
    {
        if (keyframes.Count == 0)
        {
            throw new ArgumentException("Replay script has no keyframes.", nameof(keyframes));
        }

        if (simSeconds <= keyframes[0].TSeconds)
        {
            return (keyframes[0], keyframes[0], 0);
        }

        for (var i = 0; i < keyframes.Count - 1; i++)
        {
            var from = keyframes[i];
            var to = keyframes[i + 1];

            if (simSeconds <= to.TSeconds)
            {
                var span = to.TSeconds - from.TSeconds;
                var fraction = span <= 0 ? 1.0 : (simSeconds - from.TSeconds) / span;
                return (from, to, Math.Clamp(fraction, 0, 1));
            }
        }

        var last = keyframes[^1];
        return (last, last, 0);
    }

    private static TelemetrySample Interpolate(FakeAircraft aircraft, FakeKeyframe from, FakeKeyframe to, double fraction)
    {
        double Lerp(double a, double b) => a + (b - a) * fraction;

        // Flags (and the discrete flight phase they represent, e.g. on-ground) switch partway
        // through the segment instead of fading - there is no meaningful "half on the ground".
        var useTo = fraction >= 0.5;

        return new TelemetrySample(
            DateTimeOffset.UtcNow,
            Lerp(from.LatitudeDeg, to.LatitudeDeg),
            Lerp(from.LongitudeDeg, to.LongitudeDeg),
            Lerp(from.AltitudeMslFt, to.AltitudeMslFt),
            Lerp(from.AltitudeAglFt, to.AltitudeAglFt),
            Lerp(from.IndicatedAirspeedKt, to.IndicatedAirspeedKt),
            Lerp(from.GroundSpeedKt, to.GroundSpeedKt),
            Lerp(from.VerticalSpeedFpm, to.VerticalSpeedFpm),
            Lerp(from.TrueHeadingDeg, to.TrueHeadingDeg),
            Lerp(from.MagneticHeadingDeg, to.MagneticHeadingDeg),
            useTo ? to.OnGround : from.OnGround,
            useTo ? to.EngineRunning : from.EngineRunning,
            useTo ? to.ParkingBrakeSet : from.ParkingBrakeSet,
            Lerp(from.GForce, to.GForce),
            Lerp(from.TouchdownNormalVelocityFps, to.TouchdownNormalVelocityFps),
            Lerp(from.TotalFuelKg, to.TotalFuelKg),
            aircraft.Title,
            aircraft.AtcModel,
            aircraft.AtcType);
    }
}

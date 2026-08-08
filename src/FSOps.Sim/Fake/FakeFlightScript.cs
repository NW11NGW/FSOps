namespace FSOps.Sim.Fake;

/// <summary>
/// Shape of a replay JSON file: an aircraft identity plus a list of keyframes describing the
/// flight over time. <see cref="FakeSimSource"/> interpolates between keyframes so the replayed
/// telemetry is smooth at whatever rate it is sampled, rather than jumping between a handful of
/// points.
/// </summary>
public sealed class FakeFlightScript
{
    public FakeAircraft Aircraft { get; set; } = new();

    public List<FakeKeyframe> Keyframes { get; set; } = new();
}

public sealed class FakeAircraft
{
    public string Title { get; set; } = string.Empty;

    public string AtcModel { get; set; } = string.Empty;

    public string AtcType { get; set; } = string.Empty;
}

/// <summary>
/// One point in the scripted flight. <see cref="Phase"/> is not used by <see cref="FakeSimSource"/>
/// itself - it exists purely so a human (or a test) can see at a glance which part of the flight a
/// keyframe belongs to.
/// </summary>
public sealed class FakeKeyframe
{
    public double TSeconds { get; set; }

    public string Phase { get; set; } = string.Empty;

    public double LatitudeDeg { get; set; }

    public double LongitudeDeg { get; set; }

    public double AltitudeMslFt { get; set; }

    public double AltitudeAglFt { get; set; }

    public double IndicatedAirspeedKt { get; set; }

    public double GroundSpeedKt { get; set; }

    public double VerticalSpeedFpm { get; set; }

    public double TrueHeadingDeg { get; set; }

    public double MagneticHeadingDeg { get; set; }

    public bool OnGround { get; set; }

    public bool EngineRunning { get; set; }

    public bool ParkingBrakeSet { get; set; }

    public double GForce { get; set; } = 1.0;

    public double TouchdownNormalVelocityFps { get; set; }

    public double TotalFuelKg { get; set; }
}

namespace FSOps.Sim;

/// <summary>
/// One snapshot of the aircraft's state, sim-agnostic. Both <see cref="Fake.FakeSimSource"/> and
/// the real SimConnect source produce these, so nothing downstream ever needs to know which one
/// is running.
/// </summary>
public sealed record TelemetrySample(
    DateTimeOffset TimestampUtc,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeMslFt,
    double AltitudeAglFt,
    double IndicatedAirspeedKt,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    double TrueHeadingDeg,
    double MagneticHeadingDeg,
    bool OnGround,
    bool EngineRunning,
    bool ParkingBrakeSet,
    double GForce,
    double TouchdownNormalVelocityFps,
    double TotalFuelKg,
    string AircraftTitle,
    string AtcModel,
    string AtcType);

/// <summary>Aircraft identity, known once the sim has reported it after connecting or a livery/aircraft change.</summary>
public sealed record AircraftIdentity(string Title, string AtcModel, string AtcType);

/// <summary>Lifecycle of the connection to whatever is producing telemetry - the real sim or a replay.</summary>
public enum SimConnectionState
{
    Disconnected,
    Connecting,
    Connected
}

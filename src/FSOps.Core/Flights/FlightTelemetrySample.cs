namespace FSOps.Core.Flights;

/// <summary>
/// A sim-agnostic telemetry snapshot, shaped identically to <c>FSOps.Sim.TelemetrySample</c> but
/// declared here so <see cref="FlightPhaseStateMachine"/> can live in FSOps.Core with no
/// dependency on FSOps.Sim (FSOps.Sim already depends on FSOps.Core, so the reverse would be
/// circular). FSOps.Server maps the real telemetry type onto this one at the boundary.
/// </summary>
public sealed record FlightTelemetrySample(
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
    string AtcType,
    double SimulationRate,
    bool IsSlewActive);

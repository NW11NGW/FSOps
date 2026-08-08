using System.Runtime.InteropServices;
using CTrue.FsConnect;
using Microsoft.FlightSimulator.SimConnect;

namespace FSOps.Sim.SimConnect;

/// <summary>
/// The numeric simvars sampled every telemetry request. Field order must match declaration order
/// here exactly - SimConnect marshals the response by position, not by name, so reordering these
/// without also reordering what the sim sends back would silently mix up values.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TelemetryData
{
    [SimVar(NameId = FsSimVar.PlaneLatitude, UnitId = FsUnit.Degree, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double Latitude;

    [SimVar(NameId = FsSimVar.PlaneLongitude, UnitId = FsUnit.Degree, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double Longitude;

    [SimVar(NameId = FsSimVar.PlaneAltitude, UnitId = FsUnit.Feet, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double AltitudeMsl;

    [SimVar(NameId = FsSimVar.PlaneAltitudeAboveGround, UnitId = FsUnit.Feet, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double AltitudeAgl;

    [SimVar(NameId = FsSimVar.AirspeedIndicated, UnitId = FsUnit.Knots, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double IndicatedAirspeed;

    [SimVar(NameId = FsSimVar.GroundVelocity, UnitId = FsUnit.Knots, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double GroundSpeed;

    [SimVar(NameId = FsSimVar.VerticalSpeed, UnitId = FsUnit.FeetPerMinute, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double VerticalSpeed;

    [SimVar(NameId = FsSimVar.PlaneHeadingDegreesTrue, UnitId = FsUnit.Degree, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double HeadingTrue;

    [SimVar(NameId = FsSimVar.PlaneHeadingDegreesMagnetic, UnitId = FsUnit.Degree, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double HeadingMagnetic;

    [SimVar(NameId = FsSimVar.SimOnGround, UnitId = FsUnit.Bool, DataType = SIMCONNECT_DATATYPE.INT32)]
    public int OnGround;

    // ":1" - the first (and normally only, for GA/airliner single-fuselage aircraft) engine.
    [SimVar(NameId = FsSimVar.GeneralEngCombustion, Instance = 1, UnitId = FsUnit.Bool, DataType = SIMCONNECT_DATATYPE.INT32)]
    public int EngineCombustion;

    [SimVar(NameId = FsSimVar.BrakeParkingPosition, UnitId = FsUnit.Bool, DataType = SIMCONNECT_DATATYPE.INT32)]
    public int ParkingBrake;

    [SimVar(NameId = FsSimVar.GForce, UnitId = FsUnit.GForce, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double GForce;

    // Only meaningful in the instant of touchdown; zero the rest of the time. This is the sim's
    // own slope-proof touchdown rate, used for landing scoring in a later phase.
    [SimVar(NameId = FsSimVar.PlaneTouchdownNormalVelocity, UnitId = FsUnit.FeetPerSecond, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double TouchdownNormalVelocity;

    [SimVar(NameId = FsSimVar.FuelTotalQuantityWeight, UnitId = FsUnit.Kg, DataType = SIMCONNECT_DATATYPE.FLOAT64)]
    public double FuelTotalWeightKg;
}

/// <summary>
/// Aircraft identity strings. Kept in a separate data definition from <see cref="TelemetryData"/>
/// because they are strings, not floats/ints, and only need refetching on connect or when the
/// loaded aircraft changes - not on every telemetry tick.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct AircraftIdentityData
{
    [SimVar(NameId = FsSimVar.Title, DataType = SIMCONNECT_DATATYPE.STRING256)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;

    [SimVar(NameId = FsSimVar.AtcModel, DataType = SIMCONNECT_DATATYPE.STRING256)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string AtcModel;

    [SimVar(NameId = FsSimVar.AtcType, DataType = SIMCONNECT_DATATYPE.STRING256)]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string AtcType;
}

namespace FSOps.Core.Flights;

public enum GroundFuelChangeKind
{
    /// <summary>No meaningful change - within <see cref="FuelUpliftDetector.NoiseFloorKg"/> of the previous reading.</summary>
    None,

    /// <summary>A rise in total fuel while on the ground - a refuelling event. See docs/PLAN.md
    /// "Persistent fuel state and tankering" - charged at the airport it happened at.</summary>
    Uplift,

    /// <summary>
    /// A fall in total fuel while on the ground - defuelling. FSOps' policy (decided for this
    /// chunk, documented here so it stays consistent everywhere this type is consumed): treated as
    /// a NON-EVENT, not a credit. No ledger line is posted; the tracked asset is simply reduced to
    /// match. This mirrors the reconciliation rule for an unexplained decrease ("treat as
    /// consumed") and avoids a pointless uplift-then-defuel loop being able to move money at all.
    /// </summary>
    Defuel,
}

/// <summary>
/// Reading a rise or fall in total fuel while on the ground (or, for reconciliation, at flight
/// start) into a classified event with its magnitude. The single source of this classification -
/// used both by <c>FlightLifecycleService.ProcessSample</c> (live detection while a flight is
/// tracked) and by <c>FlightEndpoints.StartAsync</c> (reconciling whatever changed while FSOps
/// wasn't watching) - so "what counts as a real change" can never drift between the two call
/// sites. Pure and stateless: every caller owns its own "previous reading" baseline.
/// </summary>
public static class FuelUpliftDetector
{
    /// <summary>
    /// Minimum change, in kg, treated as a real event rather than telemetry/float noise. Small
    /// enough to catch a genuine partial uplift, large enough that sensor jitter across samples
    /// never fires a spurious ledger line.
    /// </summary>
    public const double NoiseFloorKg = 1.0;

    public static GroundFuelChangeKind Classify(double previousKg, double currentKg)
    {
        var delta = currentKg - previousKg;
        if (Math.Abs(delta) < NoiseFloorKg)
        {
            return GroundFuelChangeKind.None;
        }

        return delta > 0 ? GroundFuelChangeKind.Uplift : GroundFuelChangeKind.Defuel;
    }

    /// <summary>Magnitude of the change, always positive regardless of direction - pair with
    /// <see cref="Classify"/> to know which way it went.</summary>
    public static double MagnitudeKg(double previousKg, double currentKg) => Math.Abs(currentKg - previousKg);
}

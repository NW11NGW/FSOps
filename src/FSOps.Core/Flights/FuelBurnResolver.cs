namespace FSOps.Core.Flights;

/// <summary>
/// What a sector is billed for, resolved from telemetry when it can be trusted, or a safe
/// fallback when it can't. Replaces the old uplift-detection model (fuel charged when bought,
/// never on burn) - FSOps now bills a sector for what it actually burned, at the departure
/// airport's price, since that is where the aircraft would realistically have been fuelled.
/// <para>
/// Two separate, deliberately pure steps: <see cref="Measure"/> turns whatever a tracker
/// observed into a single "what did this sector burn" figure (or null, if nothing usable was ever
/// observed), following a three-tier fallback baked into the measurement itself; <see cref="Resolve"/>
/// then decides whether that figure is trustworthy enough to bill, or should itself fall back to a
/// caller-supplied figure (a completed sector's own planned charge, or zero for an abandon - see
/// <see cref="Resolve"/>'s own doc for why the two calls use different values there).
/// </para>
/// </summary>
public readonly record struct FuelBurnResolution(double BilledKg, bool UsedFallback);

public static class FuelBurnResolver
{
    /// <summary>
    /// A measured burn is trusted only up to this multiple of the plausibility ceiling passed to
    /// <see cref="Resolve"/> (in practice, always the sector's own planned charge). Generous enough
    /// to cover a real diversion or extra holding, tight enough to catch a bad reading (a sim reset,
    /// a corrupted sample) rather than billing it as real.
    /// </summary>
    private const double MaxPlausibleMultipleOfCeiling = 3.0;

    /// <summary>
    /// Turns a tracker's raw fuel observations into one "what was burned" figure, or null if
    /// nothing usable was ever observed. Three tiers, in order:
    /// <list type="number">
    /// <item>
    /// <b>Engines were observed running at some point</b> (<paramref name="engineStartFuelKg"/> is
    /// set): returns <paramref name="accumulatedDecreaseKg"/> outright - the running sum of every
    /// fuel DECREASE seen WHILE THE ENGINES WERE RUNNING, since the sample where they first came
    /// alive (see the tracker's own accumulation logic in
    /// <c>FlightLifecycleService.ProcessSample</c>). Gating on engine state cuts both ways, and
    /// deliberately so: between "Start flight" and engine start, the tank can rise for reasons that
    /// are not burn (MSFS's own spawn load, a menu fuel set, a ground-crew uplift before startup); equally,
    /// once tracking has started, the tank can also FALL for reasons that are not burn while the
    /// engines are off (a defuel, a menu change, ground-crew activity during a turnaround stop) -
    /// neither direction may ever be read as burn. Accumulating only qualifying decreases, rather
    /// than a single start-minus-end subtraction, additionally makes the figure immune to a rise at
    /// ANY point after the baseline without needing to detect or classify it specially: a rise
    /// simply contributes nothing to the sum, the same way an engines-off decrease does.
    /// </item>
    /// <item>
    /// <b>Engines were never observed running</b> but at least one sample was received
    /// (<paramref name="firstSampleFuelKg"/> is set): falls back to a plain
    /// <paramref name="firstSampleFuelKg"/> minus <paramref name="lastFuelKg"/> subtraction - a sim
    /// that connected late, or a telemetry gap that never caught a genuine engine-start sample,
    /// still deserves its best-available estimate rather than nothing.
    /// </item>
    /// <item><b>No telemetry was ever received at all</b>: returns null.</item>
    /// </list>
    /// The result may still be non-positive or implausible (most likely from tier 2, since tier 1
    /// can never go negative by construction) - <see cref="Resolve"/> is what guards against that.
    /// </summary>
    public static double? Measure(double? engineStartFuelKg, double accumulatedDecreaseKg, double? firstSampleFuelKg, double lastFuelKg)
    {
        if (engineStartFuelKg is not null)
        {
            return accumulatedDecreaseKg;
        }

        if (firstSampleFuelKg is { } first)
        {
            return first - lastFuelKg;
        }

        return null;
    }

    /// <summary>
    /// Resolves what to bill from <paramref name="measuredBurnKg"/> (see <see cref="Measure"/>).
    /// <paramref name="plausibilityCeilingKg"/> bounds what a measured burn can plausibly be - in
    /// practice always the sector's own planned charge (<c>FuelBreakdown.ChargedFuelKg</c>),
    /// regardless of how much of the sector was actually flown, since a genuine burn is never a
    /// large multiple of what the WHOLE sector was expected to need.
    /// <para>
    /// A measured burn is trusted only when it is a genuine, plausible, positive figure: null (see
    /// <see cref="Measure"/>'s tier 3), zero or negative, or beyond the plausibility ceiling, all
    /// fall back to <paramref name="fallbackKg"/> rather than producing a wild charge or a credit.
    /// <paramref name="plausibilityCeilingKg"/>/<paramref name="fallbackKg"/> are deliberately
    /// separate parameters, not one reused for both jobs: a completed sector falls back to its own
    /// full planned charge (it nominally happened; treat an unmeasurable burn as "as planned"),
    /// while a flight abandoned mid-sector falls back to zero (it did not necessarily complete;
    /// treat an unmeasurable burn as "unproven" rather than assuming a full sector's worth). Both
    /// callers still share the exact same plausibility ceiling logic, so a bad reading is caught
    /// identically either way.
    /// </para>
    /// </summary>
    public static FuelBurnResolution Resolve(double? measuredBurnKg, double plausibilityCeilingKg, double fallbackKg)
    {
        var safeFallbackKg = Math.Max(0, fallbackKg);

        if (measuredBurnKg is not { } burnKg)
        {
            return new FuelBurnResolution(safeFallbackKg, UsedFallback: true);
        }

        var ceilingKg = Math.Max(0, plausibilityCeilingKg) * MaxPlausibleMultipleOfCeiling;
        var implausiblyLarge = ceilingKg > 0 && burnKg > ceilingKg;

        if (burnKg <= 0 || implausiblyLarge)
        {
            return new FuelBurnResolution(safeFallbackKg, UsedFallback: true);
        }

        return new FuelBurnResolution(burnKg, UsedFallback: false);
    }
}

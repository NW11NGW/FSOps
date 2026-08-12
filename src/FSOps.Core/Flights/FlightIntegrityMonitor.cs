using FSOps.Core.Planning;

namespace FSOps.Core.Flights;

/// <summary>
/// Detects three ways a flight's telemetry can stop being trustworthy: the sim clock running
/// faster than real time, slew (repositioning without flying), and a position jump between
/// consecutive samples too large for any real aircraft to have flown. Pure - no I/O, no wall-clock
/// reads - fed one <see cref="FlightTelemetrySample"/> at a time exactly like
/// <see cref="FlightPhaseStateMachine.Advance"/>, so the server's flight lifecycle service can
/// drive both off the same samples with no extra plumbing.
/// <para>
/// What each finding means downstream. The governing rule is that the game's own mechanics never
/// reward a shortcut, and that where something genuinely cannot be verified FSOps says so and pays
/// nothing for it rather than guessing generously. So: an elevated simulation
/// rate does NOT invalidate the flight - accelerating a long cruise is normal single-player
/// behaviour - it only means anything measured in wall-clock time (block-time variance, on-time
/// performance) is meaningless and must be reported as "not measured" rather than scored. Landing
/// quality is unaffected, since it comes from the sim's own instantaneous touchdown telemetry, not
/// elapsed time. Slew and a position jump are different in kind: both mean part of the recorded
/// path was covered in a way no flight can be, so the sector is not valid for payment - a
/// structural gate on payment, not a deduction from it.
/// </para>
/// </summary>
public sealed class FlightIntegrityMonitor
{
    /// <summary>
    /// Ground speed a real aircraft's telemetry can never legitimately imply between two samples,
    /// in knots, once the implied speed has been normalised for a reported simulation rate (see
    /// <see cref="Observe"/>). The fastest anything in a normal hangar does over the ground is
    /// nowhere near 700 kt (a fast jet's cruise TAS at altitude, plus an unrealistically strong
    /// tailwind); 1,200 kt leaves a wide margin on top of that, so a routine flight - including one
    /// flown at an elevated simulation rate, which is normalised out before this comparison - can
    /// never trip it. Only a slew-to-position, a scenery/loading jump, or an actual teleport
    /// implies a speed anywhere near this, typically by several more orders of magnitude, so the
    /// exact value is not sensitive - it just needs to sit safely above real flight and safely
    /// below what a genuine jump implies.
    /// </summary>
    public const double ImpossibleGroundSpeedKt = 1200.0;

    private FlightTelemetrySample? _last;

    /// <summary>True once any sample reported a simulation rate above 1.0.</summary>
    public bool ElevatedSimRateDetected { get; private set; }

    /// <summary>Highest simulation rate observed across every sample seen so far. Starts at 1.0 (normal speed).</summary>
    public double MaxSimulationRateObserved { get; private set; } = 1.0;

    /// <summary>True once any sample reported slew mode active.</summary>
    public bool SlewDetected { get; private set; }

    /// <summary>True once two consecutive samples implied a ground speed over <see cref="ImpossibleGroundSpeedKt"/>.</summary>
    public bool PositionJumpDetected { get; private set; }

    /// <summary>Slew or a position jump both mean the sector cannot be paid for - callers must
    /// check this (or the two flags it combines) structurally rather than subtracting a penalty
    /// from an otherwise-normal payout.</summary>
    public bool SectorInvalidForPayment => SlewDetected || PositionJumpDetected;

    /// <summary>Feeds one telemetry sample through the monitor. Order matters - the position-jump
    /// check compares each sample against whatever was observed immediately before it.</summary>
    public void Observe(FlightTelemetrySample sample)
    {
        if (sample.SimulationRate > 1.0)
        {
            ElevatedSimRateDetected = true;
            MaxSimulationRateObserved = Math.Max(MaxSimulationRateObserved, sample.SimulationRate);
        }

        if (sample.IsSlewActive)
        {
            SlewDetected = true;
        }

        if (_last is { } last)
        {
            var deltaHours = (sample.TimestampUtc - last.TimestampUtc).TotalHours;

            // A non-positive gap (out-of-order or duplicate timestamps) carries no speed
            // information - skip rather than divide by zero or risk a spurious flag.
            if (deltaHours > 0)
            {
                var distanceNm = GreatCircle.DistanceNm(last.LatitudeDeg, last.LongitudeDeg, sample.LatitudeDeg, sample.LongitudeDeg);
                var rawImpliedGroundSpeedKt = distanceNm / deltaHours;

                // Time acceleration inflates the position delta covered per wall-clock second by
                // the same factor, so a routine 4x cruise would otherwise misfire this as a
                // teleport - normalise back to the aircraft's true ground speed by the reported
                // rate before comparing to the threshold. A non-positive or unreported rate (an
                // older replay fixture, or the sim reporting oddly) falls back to 1x, which only
                // makes the check MORE sensitive, never less, so an unavailable simvar can never
                // hide a real jump.
                var effectiveRate = Math.Max(1.0, (SafeRate(last.SimulationRate) + SafeRate(sample.SimulationRate)) / 2.0);
                var impliedGroundSpeedKt = rawImpliedGroundSpeedKt / effectiveRate;

                if (impliedGroundSpeedKt > ImpossibleGroundSpeedKt)
                {
                    PositionJumpDetected = true;
                }
            }
        }

        _last = sample;
    }

    private static double SafeRate(double reportedRate) => reportedRate > 0 ? reportedRate : 1.0;
}

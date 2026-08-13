using FSOps.Core.Planning;

namespace FSOps.Core.Flights;

/// <summary>
/// Holds back telemetry at the start of a sim connection until its POSITION has been vouched for by
/// a second, agreeing fix. Pure - no I/O, no wall-clock reads - and driven entirely by the values
/// handed to <see cref="Accept"/>.
/// <para>
/// The first real flight ever flown with FSOps opened with a fix at roughly 0.0N 90.0E - 5,505 nm
/// from the stand the aircraft was actually parked on - because SimConnect delivers a packet before
/// the sim has a real aircraft state to put in it. That single reading was then handed to every
/// feature that consumes a position, and each of them trusted it independently: the integrity
/// monitor voided a clean two-hour sector on it, the first VATSIM corroboration check ran against
/// it, and it was written into the flight's own recorded track as the position the aircraft
/// started from. Each of those was fixable on its own, and fixing them one at a time would have
/// left the NEXT feature that reads a position to rediscover the same bug from scratch.
/// </para>
/// <para>
/// So the rule is applied once, at the single point every consumer is fed from: a position nothing
/// has vouched for is not handed out at all. A fix is vouched when the fix after it agrees with it -
/// when the aircraft could actually have travelled between the two at a possible speed. A bad
/// reading cannot satisfy that, because the real position that follows it is thousands of miles
/// away; two consecutive real fixes satisfy it immediately.
/// </para>
/// <para>
/// This deliberately only guards ACQUISITION. Once a fix has been vouched for, this gate stands
/// aside permanently and every later sample flows through untouched, so nothing about the middle of
/// a flight - phase changes, touchdown capture, OOOI - behaves any differently than before. Bad
/// data arriving mid-flight is a different problem with a different answer, and
/// <see cref="FlightIntegrityMonitor"/> already carries its own corroboration rules for the one
/// consequence that actually costs the player money. Defence in depth: this gate does not replace
/// that one, and neither is sufficient alone.
/// </para>
/// </summary>
public sealed class PositionAcquisitionGate
{
    /// <summary>
    /// How long telemetry may be held back before the gate gives up waiting and passes everything
    /// through regardless. This is the safety valve, and it is not optional: without it, a sim that
    /// reported implausible positions indefinitely would produce a total telemetry blackout - no
    /// fuel, no altitude, no phase tracking, nothing - which is a far worse failure than the bad
    /// position this exists to stop. Twenty seconds is longer than any burst of bad opening fixes
    /// yet observed, and it fails OPEN, which is the direction everything in this area is required
    /// to fail in.
    /// </summary>
    public static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(20);

    private (double Lat, double Lon, DateTimeOffset Utc, double SimulationRate)? _unvouchedFix;
    private DateTimeOffset? _firstFixUtc;

    /// <summary>True once a position has been vouched for and the gate has stood aside for good.</summary>
    public bool Acquired { get; private set; }

    /// <summary>How many samples were held back before acquisition. Zero in the normal case where
    /// the first two fixes agree... which is not the normal case at all in practice, since the
    /// opening fix is routinely junk - see the class doc.</summary>
    public int WithheldSampleCount { get; private set; }

    /// <summary>True if acquisition ended by running out of patience rather than by two fixes
    /// agreeing - meaning what follows is NOT vouched for and the caller should say so in its log.</summary>
    public bool AcquiredByTimeout { get; private set; }

    /// <summary>
    /// Decides whether a sample may be passed on. Returns true for every sample once acquired.
    /// </summary>
    /// <returns>True if this sample's position can be trusted enough to hand out; false to withhold it.</returns>
    public bool Accept(double latitudeDeg, double longitudeDeg, DateTimeOffset utc, double simulationRate)
    {
        if (Acquired)
        {
            return true;
        }

        _firstFixUtc ??= utc;

        if (_unvouchedFix is { } previous && AgreesWith(previous, latitudeDeg, longitudeDeg, utc, simulationRate))
        {
            // Two fixes that agree. Whichever of them was right, THIS one is corroborated - the
            // aircraft could really have got here from there - so it and everything after it goes
            // through. The earlier fix stays withheld: it is the one with nothing behind it.
            Acquired = true;
            return true;
        }

        if (utc - _firstFixUtc.Value >= AcquisitionTimeout)
        {
            Acquired = true;
            AcquiredByTimeout = true;
            return true;
        }

        _unvouchedFix = (latitudeDeg, longitudeDeg, utc, simulationRate);
        WithheldSampleCount++;
        return false;
    }

    private static bool AgreesWith(
        (double Lat, double Lon, DateTimeOffset Utc, double SimulationRate) previous,
        double latitudeDeg, double longitudeDeg, DateTimeOffset utc, double simulationRate)
    {
        var interval = utc - previous.Utc;
        if (interval <= TimeSpan.Zero)
        {
            // Out-of-order or duplicate timestamps carry no speed information, so they can neither
            // corroborate nor contradict.
            return false;
        }

        // Same numbers, and the same reasoning, as the integrity monitor's own check - and now the
        // same arithmetic too: the rate normalisation is CALLED rather than restated, because a
        // restatement is exactly how the two drifted from each other's intent once already. See
        // FlightIntegrityMonitor.
        var judgedInterval = interval < FlightIntegrityMonitor.MinimumJudgeableInterval
            ? FlightIntegrityMonitor.MinimumJudgeableInterval
            : interval;
        var effectiveRate = FlightIntegrityMonitor.NormalisingSimulationRate(previous.SimulationRate, simulationRate);
        var distanceNm = GreatCircle.DistanceNm(previous.Lat, previous.Lon, latitudeDeg, longitudeDeg);

        return distanceNm / judgedInterval.TotalHours / effectiveRate <= FlightIntegrityMonitor.ImpossibleGroundSpeedKt;
    }
}

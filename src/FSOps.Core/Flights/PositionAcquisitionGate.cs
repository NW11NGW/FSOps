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
/// has vouched for is not handed out at all.
/// </para>
/// <para>
/// <b>What "vouched for" means was wrong the first time, and the correction is the point of this
/// class.</b> Originally a fix was vouched when the fix after it agreed with it - when the aircraft
/// could have travelled between the two at a possible speed - on the stated premise that "a bad
/// reading cannot satisfy that, because the real position that follows it is thousands of miles
/// away". That premise quietly assumed the fault happens ONCE. It does not. On 2026-08-13 the same
/// sim reported the same bad fix for fifteen to thirty seconds, byte-identical, and the second
/// instance corroborated the first: zero distance over fifteen seconds is zero knots, which is
/// comfortably possible, so the gate acquired on the junk and handed it out. <b>A stuck bad fix
/// vouches for itself.</b> It went into that flight's recorded track and drew its departure marker
/// in the Indian Ocean.
/// </para>
/// <para>
/// The correction is not to demand movement. <b>An aircraft cold and dark on stand genuinely does
/// not move</b>, and its position is perfectly correct; refusing to believe a static reading would
/// refuse to acquire on every flight that starts where flights actually start. Zero displacement is
/// not evidence of falsehood. The discriminator is PLAUSIBILITY, not motion: a fix 5,505 nm from
/// where this sector departs is not believable however many times it repeats, and a fix sitting
/// still on the ramp at Bristol is believable immediately. So when the caller can say where the
/// aircraft is expected to be - see the constructor - that is what a fix is judged against, using
/// the same tolerance <see cref="FlightIntegrityMonitor.StartingFixToleranceNm"/> the integrity
/// monitor already applies to its own opening fix. One idea, one number, two places.
/// </para>
/// <para>
/// <b>With no expected position</b> the older corroboration rule is all there is, and it is kept -
/// but it is honestly the weaker one, and its limit is exactly the fault above: two identical bad
/// fixes still agree with each other. Nothing available at this layer separates "parked" from
/// "stuck" when there is no idea where the aircraft ought to be, and inventing a rule that did
/// would break the parked case, which is the common one. That is precisely why the expected
/// position is now plumbed through from the flight that is being tracked, and why acquiring without
/// one should be read as a weaker guarantee rather than an equivalent one.
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

    /// <summary>
    /// The same safety valve, for the case where an expected position IS known - and deliberately
    /// three times as long.
    /// <para>
    /// The two windows are different lengths because they are waiting for different things. Without
    /// an anchor, waiting longer buys nothing: there is no test the next sample can pass that the
    /// last one could not, so twenty seconds is simply how long it is worth blocking telemetry
    /// before giving up. With an anchor there is a specific thing being waited for - a reading from
    /// somewhere the aircraft could actually be - and the observed fault lasted long enough to slip
    /// past twenty seconds. Sixty is the window
    /// <see cref="FlightIntegrityMonitor.StartingFixAcquisitionWindow"/> already allows for exactly
    /// the same situation, so the two agree rather than each having their own number.
    /// </para>
    /// <para>
    /// It still fails OPEN, and it must. The legitimate case behind it is a player who starts
    /// tracking somewhere other than the route's departure airport; blocking their telemetry
    /// forever would be a far worse failure than an uncorroborated opening position. The cost is
    /// that such a player gets no telemetry for up to a minute - no live map, no phase tracking -
    /// which is bounded, announced in the log, and flagged on <see cref="AcquiredByTimeout"/> so
    /// nothing downstream mistakes it for a corroborated fix.
    /// </para>
    /// </summary>
    public static readonly TimeSpan AnchoredAcquisitionTimeout = FlightIntegrityMonitor.StartingFixAcquisitionWindow;

    /// <summary>
    /// Where the aircraft is expected to be as acquisition begins, or null when the caller has
    /// nothing to offer. Null-safe by design - see the class doc for how much weaker the guarantee
    /// is without it.
    /// </summary>
    private readonly (double Lat, double Lon)? _expectedPosition;

    private (double Lat, double Lon, DateTimeOffset Utc, double SimulationRate)? _unvouchedFix;
    private DateTimeOffset? _firstFixUtc;

    /// <param name="expectedPosition">
    /// Where the aircraft should be when acquisition starts - the flight's departure airport at the
    /// start of a sector, or wherever it was last credibly seen when a dropped link comes back.
    /// Optional: the sim can connect long before any flight is being tracked, and there is genuinely
    /// nothing to anchor on then.
    /// </param>
    public PositionAcquisitionGate((double Lat, double Lon)? expectedPosition = null)
    {
        _expectedPosition = expectedPosition;
    }

    /// <summary>True when this gate was given somewhere to judge fixes against - see the class doc
    /// for why the two paths are not equally strong.</summary>
    public bool IsAnchored => _expectedPosition is not null;

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

        if (_expectedPosition is { } expected)
        {
            // Anchored. A fix from somewhere the aircraft could credibly be needs no second opinion
            // at all - including a completely stationary one, which is what a cold and dark aircraft
            // on its stand reports and is exactly right. This acquires on the very first sample in
            // the ordinary case, where the old rule always lost at least one.
            var fromExpectedNm = GreatCircle.DistanceNm(expected.Lat, expected.Lon, latitudeDeg, longitudeDeg);
            if (fromExpectedNm <= FlightIntegrityMonitor.StartingFixToleranceNm)
            {
                Acquired = true;
                return true;
            }

            // And a fix from nowhere near it is NOT vouched for by another fix that is equally
            // nowhere near it, however perfectly the two agree. That is the whole correction: a
            // stuck feed agrees with itself forever, and agreement between two implausible readings
            // is not evidence of anything.
            if (utc - _firstFixUtc.Value >= AnchoredAcquisitionTimeout)
            {
                Acquired = true;
                AcquiredByTimeout = true;
                return true;
            }

            WithheldSampleCount++;
            return false;
        }

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

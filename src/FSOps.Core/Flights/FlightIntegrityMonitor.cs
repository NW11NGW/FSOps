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
/// <para>
/// Because a position jump GATES PAYMENT, this monitor must fail OPEN and never closed. A cheat
/// that occasionally gets away with it is a far smaller harm than a pilot losing a sector they
/// actually flew. The first real flight ever recorded proved why that has to be stated as a rule
/// rather than left as a sentiment: the very first telemetry sample of a clean EGGD-EGPH sector
/// carried an uninitialised SimConnect fix (0.0N, 90.0E - roughly the Bay of Bengal), 5,505 nm from
/// the stand the aircraft was actually sitting on. One reading, twelve minutes before the aircraft
/// even moved, implied 1,310,308 kt and voided a two-hour flight permanently. The next-highest
/// implied speed anywhere in that flight was 457.8 kt. The threshold was never the problem; the
/// problem was that a single unsupported observation could condemn a sector on its own. See
/// <see cref="Observe"/> for the two guards that now stand in the way of that.
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

    /// <summary>
    /// Floor applied to the gap between two samples before it is used as a divisor. Two samples
    /// arriving microseconds apart (a duplicated frame, a burst delivery after a stall) carry a
    /// gap so small that ANY position delta divides out to an enormous speed, so the raw gap is
    /// clamped up to this before the division. Clamping rather than SKIPPING the pair matters:
    /// skipping short gaps would blind the check completely at the per-sim-frame sampling rate the
    /// sim source switches to below 2,000 ft, where gaps are routinely 10-30 ms. A genuine teleport
    /// still trips the threshold by many orders of magnitude at this floor, while ordinary
    /// position noise (a few metres) resolves to a few hundred knots and is ignored.
    /// </summary>
    public static readonly TimeSpan MinimumJudgeableInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How much FLIGHT TIME the aircraft must have been tracked through plausible movement for a
    /// position to count as CORROBORATED - both before a suspected jump (proving the aircraft was
    /// really where it appeared to leave from) and after it (proving it really stayed where it
    /// appeared to arrive). See <see cref="Observe"/>. A minute is far longer than any burst of bad
    /// fixes a connecting or reconnecting SimConnect session produces, and far shorter than the time
    /// either side of a real mid-flight reposition.
    /// <para>
    /// Measured in flight time rather than wall-clock time, so it is normalised for an elevated
    /// simulation rate exactly like the speed check above it. A minute of flying is a minute of
    /// corroboration whether the player watched it in real time or at 4x; anything else would make
    /// how hard this check is to satisfy depend on a setting that has nothing to do with integrity.
    /// </para>
    /// </summary>
    public static readonly TimeSpan CorroborationDwell = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How close the aircraft has to get back to the position a suspected jump left FROM, as a
    /// fraction of how far that jump supposedly went, for the jump to be dismissed as bad data.
    /// Coming back means the aircraft was there all along and the excursion was never real - which
    /// is what a burst of bad fixes looks like, and the exact opposite of what a reposition looks
    /// like, since the entire point of a reposition is to stay where you went. Expressed as a
    /// fraction so it scales with the jump instead of needing a fixed radius that would be far too
    /// tight for a 5,000 nm glitch and far too loose for a 6 nm one.
    /// </summary>
    public const double ReturnToOriginFraction = 0.25;

    /// <summary>
    /// How far from the expected starting position (when one is supplied - see the constructor) an
    /// opening fix may be and still be believed. Generous on purpose: it only has to separate "the
    /// aircraft is somewhere around the departure airport" from "this reading is not a position at
    /// all", and the reading that caused this guard to exist was out by 5,505 nm.
    /// </summary>
    public const double StartingFixToleranceNm = 100.0;

    /// <summary>
    /// How long opening fixes may be rejected for before the monitor gives up waiting and accepts
    /// whatever the sim is reporting. Without this bound, a player who legitimately starts tracking
    /// from somewhere other than the route's departure airport would have EVERY sample discarded
    /// and the position check silently disabled for the whole sector. After this window the
    /// corroboration rule in <see cref="Observe"/> takes over on its own.
    /// </summary>
    public static readonly TimeSpan StartingFixAcquisitionWindow = TimeSpan.FromSeconds(60);

    /// <summary>Where the aircraft is expected to be when tracking starts, or null when the caller
    /// has nothing to offer - see the constructor.</summary>
    private readonly (double Lat, double Lon)? _expectedStartPosition;

    private FlightTelemetrySample? _last;

    /// <summary>Timestamp of the first sample seen, used only to bound the opening-fix window.</summary>
    private DateTimeOffset? _firstSampleUtc;

    /// <summary>True once an opening fix has been accepted (or the acquisition window has expired),
    /// after which every sample is taken at face value.</summary>
    private bool _startingFixAccepted;

    /// <summary>Unbroken run of plausible movement ending at <see cref="_last"/>, in flight time.
    /// Reset to zero by any implausible transition.</summary>
    private TimeSpan _plausibleDwell;

    /// <summary>Where a suspected jump left from, while that suspicion is outstanding. Null when
    /// there is no suspicion. See <see cref="Observe"/>.</summary>
    private (double Lat, double Lon)? _suspectedJumpOrigin;

    /// <summary>How far the suspected jump appeared to go, used to scale the "came back" test - see
    /// <see cref="ReturnToOriginFraction"/>.</summary>
    private double _suspectedJumpDistanceNm;

    /// <param name="expectedStartPosition">
    /// Where the aircraft should be when tracking begins - normally the flight's departure airport.
    /// Optional, and null-safe by design, so this type stays pure and every caller that has nothing
    /// useful to pass keeps working unchanged. When it IS supplied, an opening fix implausibly far
    /// from it is discarded as a bad reading rather than treated as one end of a teleport, which is
    /// a real discriminator rather than a blanket exemption for the opening moments: a first fix at
    /// the departure airport followed by a jump is still a teleport and is still caught.
    /// </param>
    public FlightIntegrityMonitor((double Lat, double Lon)? expectedStartPosition = null)
    {
        _expectedStartPosition = expectedStartPosition;
    }

    /// <summary>True once any sample reported a simulation rate above 1.0.</summary>
    public bool ElevatedSimRateDetected { get; private set; }

    /// <summary>Highest simulation rate observed across every sample seen so far. Starts at 1.0 (normal speed).</summary>
    public double MaxSimulationRateObserved { get; private set; } = 1.0;

    /// <summary>True once any sample reported slew mode active.</summary>
    public bool SlewDetected { get; private set; }

    /// <summary>True once a corroborated position jump has been observed - see <see cref="Observe"/>
    /// for what "corroborated" requires and why an uncorroborated one is not enough.</summary>
    public bool PositionJumpDetected { get; private set; }

    /// <summary>Slew or a position jump both mean the sector cannot be paid for - callers must
    /// check this (or the two flags it combines) structurally rather than subtracting a penalty
    /// from an otherwise-normal payout.</summary>
    public bool SectorInvalidForPayment => SlewDetected || PositionJumpDetected;

    /// <summary>
    /// Feeds one telemetry sample through the monitor. Order matters - the position-jump check
    /// compares each sample against whatever was observed immediately before it.
    /// <para>
    /// An implausible transition A-&gt;B, taken alone, says only that ONE of A and B is wrong; it
    /// says nothing about which. A teleport and a single bad fix produce exactly the same pair of
    /// numbers. What separates them is context, and the monitor now demands two kinds of it before
    /// it will condemn a sector:
    /// </para>
    /// <para>
    /// 1. An opening fix that is nowhere near where the flight is supposed to be starting is thrown
    /// away rather than believed (only when the caller supplied an expected start - see the
    /// constructor).
    /// </para>
    /// <para>
    /// 2. Otherwise, a transition only OPENS A SUSPICION, and only if the aircraft had already been
    /// tracked through <see cref="CorroborationDwell"/> of plausible movement before it - proving
    /// the position it left from was really occupied, which is exactly what a bad opening fix, or a
    /// burst of them, can never establish. The suspicion is then confirmed once the aircraft has
    /// flown on plausibly for another <see cref="CorroborationDwell"/>, and dismissed if the
    /// aircraft comes back to where it supposedly left from (see
    /// <see cref="ReturnToOriginFraction"/>) - because coming back means it was there all along and
    /// the excursion was never real.
    /// </para>
    /// <para>
    /// Confirming on "flew on normally", rather than on the next transition being plausible, is what
    /// keeps a SUSTAINED reposition caught. Slewing at speed for several seconds produces a whole
    /// run of impossible transitions, not one; each merely restarts the confirmation clock, and the
    /// suspicion is still sitting there when the run ends and normal flight resumes somewhere else
    /// entirely.
    /// </para>
    /// <para>
    /// The cost of this is that a teleport in the first minute of tracking, or in the last, is not
    /// flagged. That is the correct direction to be wrong in (see the class doc), it is bounded,
    /// and slew - the way this is actually done in practice - is still caught outright by its own
    /// simvar with no corroboration needed at all.
    /// </para>
    /// </summary>
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

        // Guard 1: discard opening fixes that cannot be this flight's starting position. Deliberately
        // AFTER the slew/sim-rate flags above - a discarded fix is still a real report from the sim
        // about how the sim is being run, and only its POSITION is untrustworthy.
        if (!AcceptOpeningFix(sample))
        {
            return;
        }

        if (_last is { } last)
        {
            var interval = sample.TimestampUtc - last.TimestampUtc;

            // A non-positive gap (out-of-order or duplicate timestamps) carries no speed
            // information at all - skip rather than risk a spurious flag on it.
            if (interval > TimeSpan.Zero)
            {
                var judgedInterval = interval < MinimumJudgeableInterval ? MinimumJudgeableInterval : interval;
                var simulationRate = Math.Max(1.0, (SafeRate(last.SimulationRate) + SafeRate(sample.SimulationRate)) / 2.0);
                var distanceNm = GreatCircle.DistanceNm(last.LatitudeDeg, last.LongitudeDeg, sample.LatitudeDeg, sample.LongitudeDeg);

                // Time acceleration inflates the position delta covered per wall-clock second by
                // the same factor, so a routine 4x cruise would otherwise misfire this as a
                // teleport - normalise back to the aircraft's true ground speed by the reported
                // rate before comparing to the threshold. A non-positive or unreported rate (an
                // older replay fixture, or the sim reporting oddly) falls back to 1x, which only
                // makes the check MORE sensitive, never less, so an unavailable simvar can never
                // hide a real jump.
                var impliedGroundSpeedKt = distanceNm / judgedInterval.TotalHours / simulationRate;

                if (impliedGroundSpeedKt > ImpossibleGroundSpeedKt)
                {
                    // Only the FIRST implausible transition of a run opens a suspicion; the rest are
                    // the same excursion continuing, and must not be allowed to displace the origin
                    // that will be used to decide whether the aircraft ever came back.
                    if (_suspectedJumpOrigin is null && _plausibleDwell >= CorroborationDwell)
                    {
                        _suspectedJumpOrigin = (last.LatitudeDeg, last.LongitudeDeg);
                        _suspectedJumpDistanceNm = distanceNm;
                    }

                    _plausibleDwell = TimeSpan.Zero;
                }
                else
                {
                    _plausibleDwell += interval * simulationRate;
                }

                ResolveSuspicion(sample);
            }
        }

        _last = sample;
    }

    /// <summary>
    /// Decides the fate of an outstanding jump suspicion against the newest sample: dismissed if the
    /// aircraft has come back to where the jump supposedly started from, confirmed once it has
    /// instead flown on plausibly for <see cref="CorroborationDwell"/>, and otherwise left
    /// outstanding. A suspicion that is never resolved either way - the flight ends first - simply
    /// expires unflagged, which is the fail-open direction this monitor is required to fail in.
    /// </summary>
    private void ResolveSuspicion(FlightTelemetrySample sample)
    {
        if (_suspectedJumpOrigin is not { } origin)
        {
            return;
        }

        var fromOriginNm = GreatCircle.DistanceNm(origin.Lat, origin.Lon, sample.LatitudeDeg, sample.LongitudeDeg);

        // Scale "came back" against how far the excursion actually got, not just its first hop -
        // a reposition covered in several steps (or an interpolated sweep) is one excursion, and
        // judging it by its opening step would set an unreasonably tight bar for calling it off.
        _suspectedJumpDistanceNm = Math.Max(_suspectedJumpDistanceNm, fromOriginNm);

        if (fromOriginNm <= _suspectedJumpDistanceNm * ReturnToOriginFraction)
        {
            _suspectedJumpOrigin = null;
            return;
        }

        if (_plausibleDwell >= CorroborationDwell)
        {
            PositionJumpDetected = true;
            _suspectedJumpOrigin = null;
        }
    }

    /// <summary>
    /// Decides whether a sample's position may be used at all. Returns true for everything once an
    /// opening fix has been accepted, which is the steady state for all but the first moments of a
    /// flight (and immediately and permanently true when no expected start was supplied).
    /// </summary>
    private bool AcceptOpeningFix(FlightTelemetrySample sample)
    {
        if (_startingFixAccepted)
        {
            return true;
        }

        if (_expectedStartPosition is not { } expectedStart)
        {
            _startingFixAccepted = true;
            return true;
        }

        _firstSampleUtc ??= sample.TimestampUtc;

        var fromExpectedStartNm = GreatCircle.DistanceNm(
            expectedStart.Lat, expectedStart.Lon, sample.LatitudeDeg, sample.LongitudeDeg);
        if (fromExpectedStartNm <= StartingFixToleranceNm)
        {
            _startingFixAccepted = true;
            return true;
        }

        if (sample.TimestampUtc - _firstSampleUtc.Value >= StartingFixAcquisitionWindow)
        {
            // Never saw a fix near the expected start. The aircraft is legitimately somewhere else
            // (positioned at another airport, say), so stop rejecting and let corroboration do the
            // work from here on - see StartingFixAcquisitionWindow.
            _startingFixAccepted = true;
            return true;
        }

        return false;
    }

    private static double SafeRate(double reportedRate) => reportedRate > 0 ? reportedRate : 1.0;
}

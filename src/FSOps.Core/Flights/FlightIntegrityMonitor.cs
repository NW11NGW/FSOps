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

    /// <summary>
    /// Total displacement below which a discontinuity is never a teleport, however absurd the speed
    /// it implies. At 5 Hz even a two-mile correction implies 36,000 kt, so the SPEED test alone
    /// cannot tell a scenery reload or a ground settle from a reposition - but a jump has to be
    /// worth something to be a cheat, and one that gains the player nothing must never cost them a
    /// sector.
    /// <para>
    /// Why five miles specifically. The number this replaces is about 62 m: that is all it takes for
    /// a correction to exceed <see cref="ImpossibleGroundSpeedKt"/> across
    /// <see cref="MinimumJudgeableInterval"/>, so before this floor existed the effective "teleport"
    /// threshold was the length of a wide-body aircraft. Five miles is 150 times that. It has to sit
    /// comfortably ABOVE the corrections that actually occur - the characterised case trips at two
    /// miles, which is the size of error a scenery load or a ground settle produces - and comfortably
    /// BELOW anything worth cheating for. Five miles is also already the distance this app uses
    /// elsewhere to mean "the aircraft is still where we left it" (FlightLifecycleService's reconnect
    /// resume radius), and it is well under a minute of cruise, so no sector's payment turns on it.
    /// <para>
    /// It is deliberately NOT asked to carry the whole opening-fix case on its own: corrections of
    /// eight, forty and ninety-five miles are closed by the departure rule below
    /// (<see cref="DepartureCorrectionRadiusNm"/>), not by this floor, precisely because a floor
    /// large enough to swallow those WOULD be large enough to be worth exploiting.
    /// </para>
    /// <para>
    /// It is measured
    /// against the excursion's TOTAL displacement from where it started, not against a single
    /// sample-to-sample hop, so a reposition delivered as a run of small steps is still measured as
    /// the one large move it really is (see <see cref="Observe"/>).
    /// </para>
    /// </summary>
    public const double NegligibleJumpDistanceNm = 5.0;

    /// <summary>
    /// How close to the expected starting position a jump has to LAND for it to be read as a
    /// correction rather than a reposition - see <see cref="Observe"/>. Nobody teleports to the
    /// stand they are supposed to be standing on, so a jump that puts the aircraft back at its own
    /// departure airport, before it has ever left it, gains nothing by construction.
    /// <para>
    /// Ten miles covers the airport and everything immediately around it. It is deliberately much
    /// tighter than <see cref="StartingFixToleranceNm"/>, which answers a different question ("is
    /// this reading a position at all?") and would be far too generous here: excusing a jump that
    /// landed 95 nm from the departure airport WOULD hand the player most of a short sector.
    /// </para>
    /// </summary>
    public const double DepartureCorrectionRadiusNm = 10.0;

    /// <summary>
    /// How far the aircraft has to move before the monitor accepts that the position feed is
    /// genuinely updating rather than repeating itself. About 90 m - a parked aircraft does not
    /// creep that far, and anything taxiing covers it in seconds, while a stalled feed never covers
    /// it at all. Used for two things, both of which turn on "did this reading actually change?":
    /// deciding whether a resumed feed has come back to life (see
    /// <see cref="NotifyTelemetryInterrupted"/>), and deciding whether the aircraft has really left
    /// its departure area (see <see cref="Observe"/>).
    /// </summary>
    private const double ObservableMovementNm = 0.05;

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

    /// <summary>How far the suspected jump appeared to go: the greatest distance from
    /// <see cref="_suspectedJumpOrigin"/> reached while the excursion was still running. Used both
    /// to scale the "came back" test (see <see cref="ReturnToOriginFraction"/>) and to decide
    /// whether the excursion was ever worth anything (see <see cref="NegligibleJumpDistanceNm"/>).
    /// </summary>
    private double _suspectedJumpDistanceNm;

    /// <summary>True once the aircraft has been seen to move, plausibly, outside
    /// <see cref="DepartureCorrectionRadiusNm"/> of its expected start. A one-way latch: after this
    /// the departure-correction rule in <see cref="Observe"/> is spent for the rest of the sector.
    /// </summary>
    private bool _leftTheStartingArea;

    /// <summary>True between a telemetry interruption and the first evidence that the resumed feed
    /// is really updating - see <see cref="NotifyTelemetryInterrupted"/>.</summary>
    private bool _awaitingProofTheFeedResumed;

    /// <summary>The first position reported after an interruption, against which that evidence is
    /// judged. Null until one arrives.</summary>
    private (double Lat, double Lon)? _positionWhenFeedResumed;

    /// <summary>
    /// The first position this monitor ever accepted, against which "has the aircraft actually gone
    /// anywhere?" is measured - see <see cref="NoteWhetherTheAircraftHasLeftItsDepartureArea"/>.
    /// Never reset, because the question is about the whole sector.
    /// <para>
    /// It has to be DISPLACEMENT from a fixed origin, and emphatically not a running sum of
    /// per-transition distances. A sum of hops is PATH LENGTH, and path length grows without bound
    /// for a reading that is going nowhere: a wrong fix carrying a metre of ordinary noise displaces
    /// the aircraft by a metre forever while accumulating a metre of "travel" every sample, so at
    /// 5 Hz it clears any fixed distance in seconds. Since such a fix also reports itself well
    /// outside the departure area, the sum-of-hops form spent the departure-correction exemption
    /// before the brakes came off - which is the exact opposite of what that precondition exists
    /// for, and it re-opened the original garbage-opening-fix bug for any bad reading that was not
    /// bit-identical every sample.
    /// </para>
    /// </summary>
    private (double Lat, double Lon)? _firstObservedPosition;
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

    /// <summary>
    /// Tells the monitor that the telemetry link went away and has come back, so the samples either
    /// side of the hole are not consecutive observations of the same continuous motion. Callers must
    /// raise this for a live sim reconnect exactly as they rebuild
    /// <see cref="PositionAcquisitionGate"/>, and for the same reason.
    /// <para>
    /// The rule is not a heuristic: YOU CANNOT INFER SPEED ACROSS A GAP IN TELEMETRY. A transition
    /// spanning an outage says nothing about how fast anything moved, so measuring it is simply
    /// wrong - and it is wrong in the direction that costs a player their sector, because the
    /// aircraft really did keep flying while nobody was watching, so the first honest position after
    /// the link returns is genuinely miles from the last one before it went away. Worse, a
    /// reconnecting SimConnect commonly replays the position it last had for a while, which bridges
    /// the hole and makes the monitor's own timeline look unbroken; the gap is then invisible in the
    /// timestamps and only the connection state can reveal it.
    /// </para>
    /// <para>
    /// What is dropped is everything that describes CONTINUITY - the previous sample and the
    /// unbroken run of plausible movement behind it. What is deliberately kept is everything already
    /// FOUND: slew, an elevated sim rate, a confirmed jump, and any suspicion still outstanding. A
    /// reconnect must never launder evidence, or dropping the link would become the cheat.
    /// </para>
    /// <para>
    /// An unusually long gap between two samples with no reconnect behind it is NOT treated this
    /// way, and that is a considered decision rather than an omission. When samples simply stop and
    /// restart, the timestamps stay honest, so the implied speed across the gap is still a true
    /// lower bound on how fast the aircraft moved and an honest flight cannot trip it - there is no
    /// false positive to fix. Suppressing those transitions would only delete a real detection: a
    /// telemetry blackout used to cover a reposition. It is staleness, not silence, that breaks the
    /// arithmetic, and staleness arrives with a reconnect.
    /// </para>
    /// </summary>
    public void NotifyTelemetryInterrupted()
    {
        _last = null;
        _plausibleDwell = TimeSpan.Zero;
        _awaitingProofTheFeedResumed = true;
        _positionWhenFeedResumed = null;
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
    /// numbers. What separates them is context, and the monitor demands several kinds of it before
    /// it will condemn a sector.
    /// </para>
    /// <para>
    /// 1. An opening fix that is nowhere near where the flight is supposed to be starting is thrown
    /// away rather than believed (only when the caller supplied an expected start - see the
    /// constructor).
    /// </para>
    /// <para>
    /// 2. A transition is not judged at all when it spans a break in the telemetry - see
    /// <see cref="NotifyTelemetryInterrupted"/>. You cannot infer speed across a gap.
    /// </para>
    /// <para>
    /// 3. Nor when it puts the aircraft back at the airport this flight is departing from, before
    /// the aircraft has ever left it. Nobody teleports TO the stand they are supposed to be standing
    /// on, so that move cannot be a cheat whatever it implies - see
    /// <see cref="IsACorrectionBackToItsOwnDeparture"/>. This is what makes an opening fix that is
    /// wrong but BELIEVABLE - two miles out, or ninety - survivable at all: it is inside
    /// <see cref="StartingFixToleranceNm"/>, so the guard above believes it, and once it has been
    /// held for a <see cref="CorroborationDwell"/> nothing else stood between the correction and a
    /// lost sector.
    /// </para>
    /// <para>
    /// 4. Otherwise, a transition only OPENS A SUSPICION, and only if the aircraft had already been
    /// tracked through <see cref="CorroborationDwell"/> of plausible movement before it - proving
    /// the position it left from was really occupied, which is exactly what a bad opening fix, or a
    /// burst of them, can never establish. The suspicion is then confirmed once the aircraft has
    /// flown on plausibly for another <see cref="CorroborationDwell"/>, and dismissed if the
    /// aircraft comes back to where it supposedly left from (see
    /// <see cref="ReturnToOriginFraction"/>) - because coming back means it was there all along and
    /// the excursion was never real.
    /// </para>
    /// <para>
    /// 5. And even a fully corroborated suspicion is only acted on if the excursion was big enough
    /// to be worth making - see <see cref="NegligibleJumpDistanceNm"/>. "Came back" cannot save an
    /// honest pilot on its own, because it is scaled to the jump distance from the FALSE origin and
    /// the aircraft is never going back to a place it has never been; a jump that gains nothing has
    /// to be dismissed on its own merits rather than on where the aircraft goes next.
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

        // The first position actually believed - the fixed origin the "has it gone anywhere?" test
        // measures displacement from. See _firstObservedPosition.
        _firstObservedPosition ??= (sample.LatitudeDeg, sample.LongitudeDeg);

        if (_last is { } last)
        {
            var interval = sample.TimestampUtc - last.TimestampUtc;

            // A non-positive gap (out-of-order or duplicate timestamps) carries no speed
            // information at all - skip rather than risk a spurious flag on it.
            if (interval > TimeSpan.Zero)
            {
                var judgedInterval = interval < MinimumJudgeableInterval ? MinimumJudgeableInterval : interval;
                var simulationRate = NormalisingSimulationRate(last.SimulationRate, sample.SimulationRate);
                var distanceNm = GreatCircle.DistanceNm(last.LatitudeDeg, last.LongitudeDeg, sample.LatitudeDeg, sample.LongitudeDeg);

                // Time acceleration inflates the position delta covered per wall-clock second by
                // the same factor, so a routine 4x cruise would otherwise misfire this as a
                // teleport - normalise back to the aircraft's true ground speed by the reported
                // rate before comparing to the threshold. See NormalisingSimulationRate for why the
                // two samples' rates are combined by maximum rather than by average, and for the
                // false positive that averaging produced on the one transition where the rate
                // changes.
                var impliedGroundSpeedKt = distanceNm / judgedInterval.TotalHours / simulationRate;

                if (impliedGroundSpeedKt > ImpossibleGroundSpeedKt)
                {
                    // Only the FIRST implausible transition of a run opens a suspicion; the rest are
                    // the same excursion continuing, and must not be allowed to displace the origin
                    // that will be used to decide whether the aircraft ever came back.
                    if (_suspectedJumpOrigin is null)
                    {
                        if (_plausibleDwell >= CorroborationDwell && !IsACorrectionBackToItsOwnDeparture(sample))
                        {
                            _suspectedJumpOrigin = (last.LatitudeDeg, last.LongitudeDeg);
                            _suspectedJumpDistanceNm = distanceNm;
                        }
                    }
                    else if (_suspectedJumpOrigin is { } running)
                    {
                        // The same excursion continuing - a reposition delivered in several steps,
                        // or an interpolated sweep. Measure it by where it has got to, so a move
                        // spread over a run of individually small hops is still measured as the one
                        // large displacement it really is, whether or not the odd ordinary-looking
                        // frame turns up in the middle of it.
                        //
                        // Growth is confined to IMPLAUSIBLE transitions, and that is the whole
                        // point: an excursion grows when the aircraft is moved, not when it flies.
                        // Letting ordinary onward flight inflate the distance instead is how a
                        // two-mile correction ended up condemning a sector, since flying away from
                        // a false origin looks identical to a jump that keeps getting bigger.
                        _suspectedJumpDistanceNm = Math.Max(
                            _suspectedJumpDistanceNm,
                            GreatCircle.DistanceNm(running.Lat, running.Lon, sample.LatitudeDeg, sample.LongitudeDeg));
                    }

                    // Note what is deliberately NOT done here: an implausible transition does not
                    // clear _awaitingProofTheFeedResumed. Jitter is nothing but implausible
                    // transitions, and it is the opposite of evidence that a feed is healthy - a
                    // reading that cannot even agree with itself must not be allowed to certify the
                    // link as recovered and hand back the right to rebuild dwell out of whatever
                    // freeze follows it. Only a plausible transition can do that.
                    _plausibleDwell = TimeSpan.Zero;
                }
                else
                {
                    if (HasTheResumedFeedProvedItself(sample))
                    {
                        _plausibleDwell += interval * simulationRate;
                        NoteWhetherTheAircraftHasLeftItsDepartureArea(sample);
                    }
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

        if (fromOriginNm <= _suspectedJumpDistanceNm * ReturnToOriginFraction)
        {
            _suspectedJumpOrigin = null;
            return;
        }

        if (_plausibleDwell >= CorroborationDwell)
        {
            // Corroborated on both sides - but only condemn the sector if the excursion was worth
            // making. An aircraft that ends up five miles from where it was cannot have gained
            // anything by it, so whatever produced those five miles, it was not a cheat. See
            // NegligibleJumpDistanceNm. Either way the suspicion is now resolved and cleared, so a
            // dead one can never be reopened later by an unrelated glitch somewhere else entirely.
            if (_suspectedJumpDistanceNm > NegligibleJumpDistanceNm)
            {
                PositionJumpDetected = true;
            }

            _suspectedJumpOrigin = null;
        }
    }

    /// <summary>
    /// True when an implausible transition puts the aircraft back at (or beside) the very airport
    /// this flight is supposed to be departing from, and it has not yet left it. Nobody teleports TO
    /// the stand they are meant to be standing on: that move gains nothing, and it is exactly the
    /// shape of a bad opening fix finally being corrected, whether the bad fix was two miles out or
    /// five thousand.
    /// <para>
    /// Note what this is keyed on, because a near-synonym of it gives a sector away for free. The
    /// test is on the jump's DESTINATION being the departure airport. It is emphatically NOT "no
    /// jump counts before the flight has departed": under that reading a cheat taxis, teleports
    /// along the ground to a mile short of the ARRIVAL airport, takes off, flies a three-minute
    /// circuit and lands, and is paid the whole fare - because revenue is priced from the route, not
    /// from the distance actually flown, so skipping the entire sector costs nothing.
    /// </para>
    /// <para>
    /// It is also strictly one-way. The exemption is spent for good the moment the aircraft gets
    /// airborne or is flown out of the departure area (see
    /// <see cref="NoteWhetherTheAircraftHasLeftItsDepartureArea"/>) and can never be re-entered.
    /// That matters because landing back where you departed is a legitimate, PAID outcome - the
    /// resolver treats it as a diversion - so an exemption still available in the air would let a
    /// cheat teleport home from anywhere and be paid in full.
    /// </para>
    /// </summary>
    private bool IsACorrectionBackToItsOwnDeparture(FlightTelemetrySample sample)
    {
        if (_leftTheStartingArea || _expectedStartPosition is not { } expectedStart)
        {
            return false;
        }

        return GreatCircle.DistanceNm(expectedStart.Lat, expectedStart.Lon, sample.LatitudeDeg, sample.LongitudeDeg)
            <= DepartureCorrectionRadiusNm;
    }

    /// <summary>
    /// Latches, permanently, the fact that the aircraft has genuinely left its departure area -
    /// either by getting airborne or by being flown out of it. Once this is set the
    /// departure-correction exemption is gone for the rest of the sector and can never be
    /// re-entered, which is the point: landing back where you departed is a legitimate, paid outcome
    /// (the resolver treats it as a diversion), so an exemption that survived into the airborne
    /// phase would let a cheat teleport home from anywhere and still be paid in full.
    /// <para>
    /// Called only for PLAUSIBLE transitions, and only once the aircraft has been DISPLACED from the
    /// first position this monitor ever saw. That precondition is the load-bearing one: being
    /// REPORTED somewhere is not the same as having gone there. A bad fix reports itself outside the
    /// departure area - and airborne, and anything else it likes - on every single sample, and if
    /// that were enough it would spend the exemption on the flight's behalf before the aircraft had
    /// released its brakes. A bad fix does not MOVE, whether it is frozen solid or wandering by a
    /// metre; a real aircraft is ninety metres from where it started within seconds of rolling.
    /// See <see cref="_firstObservedPosition"/> for why this must not be a sum of hops.
    /// </para>
    /// </summary>
    private void NoteWhetherTheAircraftHasLeftItsDepartureArea(FlightTelemetrySample sample)
    {
        if (_leftTheStartingArea || _expectedStartPosition is not { } expectedStart)
        {
            return;
        }

        if (_firstObservedPosition is not { } origin
            || GreatCircle.DistanceNm(origin.Lat, origin.Lon, sample.LatitudeDeg, sample.LongitudeDeg) <= ObservableMovementNm)
        {
            return;
        }

        if (!sample.OnGround
            || GreatCircle.DistanceNm(expectedStart.Lat, expectedStart.Lon, sample.LatitudeDeg, sample.LongitudeDeg)
                > DepartureCorrectionRadiusNm)
        {
            _leftTheStartingArea = true;
        }
    }

    /// <summary>
    /// After an interruption, decides whether the feed has shown itself to be live again. Until it
    /// has, a position that never changes must not accumulate corroboration: an unchanging reading
    /// is precisely what a reconnecting SimConnect replaying its last known state looks like, and
    /// counting it would rebuild - out of stale packets - the very dwell that then condemns the
    /// sector when the truth arrives. A parked aircraft looks identical, which is why this only
    /// applies while a resumed feed is under suspicion and never at any other time.
    /// </summary>
    private bool HasTheResumedFeedProvedItself(FlightTelemetrySample sample)
    {
        if (!_awaitingProofTheFeedResumed)
        {
            return true;
        }

        if (_positionWhenFeedResumed is not { } resumedAt)
        {
            _positionWhenFeedResumed = (sample.LatitudeDeg, sample.LongitudeDeg);
            return false;
        }

        if (GreatCircle.DistanceNm(resumedAt.Lat, resumedAt.Lon, sample.LatitudeDeg, sample.LongitudeDeg)
            <= ObservableMovementNm)
        {
            return false;
        }

        _awaitingProofTheFeedResumed = false;
        return true;
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

    /// <summary>
    /// The simulation rate a transition between two samples must be normalised by: the GREATER of
    /// the two reported rates, never their average.
    /// <para>
    /// Exactly one transition per rate change straddles two different reported rates, and the ground
    /// it covers was flown at one of them, not at something in between. Averaging under-divides that
    /// transition badly: for a step from R1 to R2 with the interval actually flown at R2, the implied
    /// speed comes out as <c>groundSpeed x 2 x R2 / (R1 + R2)</c>, which tends to TWICE the true
    /// ground speed as the step gets larger. Against a 1,200 kt threshold that condemns anything
    /// above about 600 kt over the ground on a single 1x -&gt; 64x or 1x -&gt; 128x step - an airliner
    /// eastbound in a jet stream, not an exotic case. It is worse than it sounds because dwell is
    /// measured in flight time: at 128x the sixty seconds needed to confirm the suspicion elapse in
    /// under half a wall-clock second, so the sector goes almost immediately. (The default keybind's
    /// stepwise doubling only implies 1.33x ground speed and was always safe; a bound "set rate" or
    /// an add-on produces the large single step.)
    /// </para>
    /// <para>
    /// Taking the ARRIVING sample's rate instead would fix the step up and break the mirror image of
    /// it: on a step down from 128x to 1x where the interval was flown fast, the arriving rate is 1
    /// and the same false positive reappears in reverse. The maximum is right in both directions
    /// because it can never under-divide, only over-divide - and over-dividing is the fail-open
    /// direction everything here is required to fail in. It costs nothing against cheating: a
    /// teleport of hundreds of miles in a fifth of a second still implies tens of thousands of knots
    /// after being divided by 128.
    /// </para>
    /// <para>
    /// A non-positive or unreported rate falls back to 1x, which only ever makes the check MORE
    /// sensitive, so an unavailable simvar can never hide a real jump.
    /// </para>
    /// </summary>
    public static double NormalisingSimulationRate(double previousRate, double currentRate) =>
        Math.Max(1.0, Math.Max(SafeRate(previousRate), SafeRate(currentRate)));

    private static double SafeRate(double reportedRate) => reportedRate > 0 ? reportedRate : 1.0;
}



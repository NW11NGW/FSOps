using FSOps.Core.Flights;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Flights;

/// <summary>
/// Adversarial verification of the two layers that decide whether a flown sector is valid for
/// payment - <see cref="PositionAcquisitionGate"/> and <see cref="FlightIntegrityMonitor"/> -
/// exercised TOGETHER, in the same order and with the same arguments the running app uses
/// (<c>SimTelemetryService.AcceptPosition</c> gates, then <c>FlightLifecycleService.ProcessSample</c>
/// observes). Testing either in isolation cannot answer the question these tests exist for, because
/// the gate changes WHICH samples the monitor ever sees, and the gate's fail-open timeout is
/// precisely the thing that could hand the monitor a fix nothing vouched for.
/// <para>
/// Two halves, deliberately: everything named <c>...IsStillPaid</c> is a shape an HONEST pilot can
/// produce and must never be punished for; everything named <c>...IsNotPaid</c> is a cheat that must
/// still be caught. A fix that only satisfies the first half is not a fix.
/// </para>
/// <para>
/// Honest limit: this is recorded and synthetic telemetry, not a simulator. What it proves is that
/// the SHAPES real and plausible telemetry take come out right - not that MSFS cannot produce a
/// shape nobody has thought of.
/// </para>
/// </summary>
public class SectorPaymentAdversarialTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Bristol's stand, as the first real flight's own first believable fix recorded it.</summary>
    private const double BristolLat = 51.38526;
    private const double BristolLon = -2.71770;

    /// <summary>The uninitialised opening fix from the flight that started all this: the Bay of
    /// Bengal, 5,505 nm from the stand.</summary>
    private const double BadFixLat = 0.0;
    private const double BadFixLon = 90.0;

    /// <summary>Glasgow - a plausible "the player is actually somewhere else" departure, ~290 nm
    /// from Bristol, comfortably outside <see cref="FlightIntegrityMonitor.StartingFixToleranceNm"/>.</summary>
    private const double GlasgowLat = 55.8719;
    private const double GlasgowLon = -4.4331;

    /// <summary>Roughly what SimConnect delivers - see FlightIntegrityMonitor's own doc, which
    /// records the live rate as "roughly 5 Hz".</summary>
    private const double Hz = 5.0;

    private const double Step = 1.0 / Hz;

    // ---------------------------------------------------------------------------------------
    // The rig: the real pipeline order, nothing else.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The gate and the monitor wired the way the app wires them. <see cref="Feed"/> is a faithful
    /// reduction of <c>SimTelemetryService.AcceptPosition</c> followed by
    /// <c>FlightLifecycleService.ProcessSample</c>'s call to <c>IntegrityMonitor.Observe</c> - a
    /// withheld sample reaches no consumer at all, which is the whole point of the gate.
    /// </summary>
    private sealed class Rig
    {
        private PositionAcquisitionGate _gate = new();

        public Rig((double Lat, double Lon)? expectedStart = null)
            => Monitor = new FlightIntegrityMonitor(expectedStart);

        public FlightIntegrityMonitor Monitor { get; }

        public PositionAcquisitionGate Gate => _gate;

        public int SamplesWithheld { get; private set; }

        public int SamplesObserved { get; private set; }

        /// <summary>True if the gate has ever given up waiting rather than acquiring on agreement -
        /// latched across reconnects, because "did this run ever fail open?" is the question.</summary>
        public bool EverAcquiredByTimeout { get; private set; }

        public bool SectorWouldBePaid => !Monitor.SectorInvalidForPayment;

        public void Feed(FlightTelemetrySample sample)
        {
            if (!_gate.Accept(sample.LatitudeDeg, sample.LongitudeDeg, sample.TimestampUtc, sample.SimulationRate))
            {
                SamplesWithheld++;
                return;
            }

            EverAcquiredByTimeout |= _gate.AcquiredByTimeout;
            SamplesObserved++;
            Monitor.Observe(sample);
        }

        /// <summary>
        /// What the app does when the sim link drops and comes back: a brand-new gate AND a
        /// <see cref="FlightIntegrityMonitor.NotifyTelemetryInterrupted"/> on the monitor.
        /// <para>
        /// Both halves matter and they are wired through different paths in production -
        /// <c>SimTelemetryService.BeginFreshAcquisition</c> rebuilds the gate and raises
        /// <c>TelemetryInterrupted</c>, which <c>FlightLifecycleService.OnTelemetryInterrupted</c>
        /// forwards to the active tracker's monitor. An earlier version of this rig reset only the
        /// gate, which is what the app did at the time; keeping that after the app changed would
        /// have left six reconnect cases passing against a model of the old code.
        /// </para>
        /// </summary>
        public void SimulateSimReconnect()
        {
            _gate = new PositionAcquisitionGate();
            Monitor.NotifyTelemetryInterrupted();
        }
    }

    /// <param name="onGround">
    /// Defaults to FALSE - i.e. airborne - which is the adversarial setting for the
    /// departure-correction latch, since being airborne is one of the two things that spends it.
    /// Tests about an aircraft genuinely sitting on its stand pass true, so that only the
    /// distance disjunct can fire and the case is the one a real parked aircraft produces.
    /// </param>
    private static FlightTelemetrySample At(
        double seconds, double latitudeDeg, double longitudeDeg,
        double simulationRate = 1.0, bool isSlewActive = false, bool onGround = false) =>
        new(Base + TimeSpan.FromSeconds(seconds), latitudeDeg, longitudeDeg, 5000, 4800, 250, 250, 0, 0, 0,
            onGround, true, false, 1.0, 0, 5000, "Test Aircraft", "TEST", "Test", simulationRate, isSlewActive);

    /// <summary>Feeds <paramref name="seconds"/> of the aircraft sitting perfectly still at one
    /// position. Returns the timestamp one step past the last sample fed.</summary>
    private static double Hold(
        Rig rig, double latitudeDeg, double longitudeDeg, double fromSeconds, double seconds,
        double simulationRate = 1.0, bool isSlewActive = false, bool onGround = false)
    {
        var t = fromSeconds;
        for (var i = 0; i < (int)Math.Round(seconds * Hz); i++, t += Step)
        {
            rig.Feed(At(t, latitudeDeg, longitudeDeg, simulationRate, isSlewActive, onGround));
        }

        return t;
    }

    /// <summary>Feeds <paramref name="seconds"/> of ordinary flight tracking due north at
    /// <paramref name="groundSpeedKt"/>. Position deltas already reflect
    /// <paramref name="simulationRate"/>, exactly as a real accelerated sim's would.</summary>
    private static (double T, double Lat) Fly(
        Rig rig, double startLatitudeDeg, double longitudeDeg, double fromSeconds, double seconds,
        double groundSpeedKt = 460.0, double simulationRate = 1.0, bool isSlewActive = false,
        double hz = Hz)
    {
        var step = 1.0 / hz;
        // ~60 nm per degree of latitude.
        var degreesPerStep = groundSpeedKt * simulationRate * step / 3600.0 / 60.0;
        var t = fromSeconds;
        var lat = startLatitudeDeg;
        for (var i = 0; i < (int)Math.Round(seconds * hz); i++, t += step, lat += degreesPerStep)
        {
            rig.Feed(At(t, lat, longitudeDeg, simulationRate, isSlewActive));
        }

        return (t, lat);
    }

    // ---------------------------------------------------------------------------------------
    // Half one: shapes an honest pilot can produce. None of these may cost a sector.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CleanSectorFromTheStand_IsPaid()
    {
        // The baseline. If this ever fails, nothing below means anything.
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(rig.SectorWouldBePaid);
        Assert.False(rig.EverAcquiredByTimeout);
    }

    /// <summary>
    /// The attack on the gate's fail-open timeout, which is the thing most likely to put the
    /// original defect back. A garbage opening fix that STAYS PUT corroborates itself - the gate's
    /// agreement rule cannot tell "two real fixes of a parked aircraft" from "two copies of the same
    /// uninitialised packet" - so the gate acquires on it immediately and hands it straight to the
    /// monitor. Everything then rests on the monitor's own two guards.
    /// <para>
    /// Parameterised over how long the sim reports the bad position for, because that is the only
    /// variable that matters: the monitor's opening-fix guard lasts
    /// <see cref="FlightIntegrityMonitor.StartingFixAcquisitionWindow"/>, and its corroboration rule
    /// needs <see cref="FlightIntegrityMonitor.CorroborationDwell"/> of "plausible movement" either
    /// side - which a stationary garbage fix supplies, since standing still is perfectly plausible.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(59)]
    [InlineData(75)]
    [InlineData(110)]
    [InlineData(118)]
    public void StuckGarbageOpeningFix_ThenACleanSector_IsStillPaid(int garbageSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BadFixLat, BadFixLon, 0, garbageSeconds);
        t = Hold(rig, BristolLat, BristolLon, t, 120);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(
            rig.SectorWouldBePaid,
            $"a clean sector was voided because the sim reported a bad opening position for {garbageSeconds}s");
    }

    /// <summary>
    /// Was a characterised defect; now the property it should always have asserted. Once the sim had
    /// reported the SAME bad position for longer than
    /// <see cref="FlightIntegrityMonitor.StartingFixAcquisitionWindow"/> +
    /// <see cref="FlightIntegrityMonitor.CorroborationDwell"/>, guard 1 had stood aside AND the bad
    /// position had "corroborated" itself by standing still, so the correction back to the truth read
    /// as a teleport. The acquisition gate could not help - two identical bad packets AGREE with each
    /// other, so the gate acquires on the first one and passes everything after it.
    /// <para>
    /// Closed by the departure-correction rule: the correction LANDS at the departure airport, which
    /// nobody teleports to. Kept parameterised well past the old 120 s boundary because the boundary
    /// was the symptom, not the bug.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(125)]
    [InlineData(180)]
    [InlineData(600)]
    public void StuckGarbageOpeningFixHeldPastGuardPlusDwell_IsStillPaid(int garbageSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BadFixLat, BadFixLon, 0, garbageSeconds);
        t = Hold(rig, BristolLat, BristolLon, t, 120);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.False(rig.Monitor.PositionJumpDetected);
        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// The same attack, but the garbage never agrees with itself - it thrashes between two absurd
    /// positions, so the gate CANNOT acquire on agreement and is forced down its
    /// <see cref="PositionAcquisitionGate.AcquisitionTimeout"/> path. This is the literal
    /// "accepted-by-timeout garbage fix reaches the monitor" case.
    /// </summary>
    [Theory]
    [InlineData(25)]
    [InlineData(60)]
    [InlineData(150)]
    public void JitteringGarbageOpeningFixes_AcceptedByTheGateTimeout_ThenACleanSector_IsStillPaid(int garbageSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));

        var t = 0.0;
        for (var i = 0; i < (int)(garbageSeconds * Hz); i++, t += Step)
        {
            // Two uninitialised-looking readings half a world apart, alternating - nothing can
            // vouch for anything.
            var (lat, lon) = i % 2 == 0 ? (BadFixLat, BadFixLon) : (0.0, -90.0);
            rig.Feed(At(t, lat, lon));
        }

        t = Hold(rig, BristolLat, BristolLon, t, 120);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(rig.EverAcquiredByTimeout, "this test proves nothing unless the gate really did fail open");
        Assert.True(
            rig.SectorWouldBePaid,
            $"a clean sector was voided after {garbageSeconds}s of jittering opening fixes the gate had to accept by timeout");
    }

    /// <summary>
    /// The worst realistic composite: the sim thrashes long enough to force the gate's timeout, THEN
    /// settles on one wrong position long enough to look like a parked aircraft, and only then tells
    /// the truth. Both failure modes in one run.
    /// </summary>
    [Fact]
    public void JitteringThenStuckGarbage_ThenACleanSector_IsStillPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));

        var t = 0.0;
        for (var i = 0; i < (int)(30 * Hz); i++, t += Step)
        {
            var (lat, lon) = i % 2 == 0 ? (BadFixLat, BadFixLon) : (0.0, -90.0);
            rig.Feed(At(t, lat, lon));
        }

        t = Hold(rig, BadFixLat, BadFixLon, t, 150);
        t = Hold(rig, BristolLat, BristolLon, t, 120);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        // Recorded so the two contributions are not confused: the gate DID fail open here, and that
        // was never what lost the sector - the stuck phase that followed was, exactly as in
        // StuckGarbageOpeningFixHeldPastGuardPlusDwell_IsStillPaid above. Shortening or removing the
        // gate's timeout would not have saved this sector, and lengthening it would not have either.
        Assert.True(rig.EverAcquiredByTimeout);
        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// A first fix that is wrong but PLAUSIBLE - a few miles out rather than half a world away, the
    /// shape a scenery-load settle or a mis-set spawn produces. It is inside the monitor's starting
    /// tolerance, so guard 1 does not discard it and the whole weight falls on corroboration.
    /// </summary>
    [Theory]
    [InlineData(2.0, 5)]
    [InlineData(2.0, 30)]
    [InlineData(2.0, 55)]
    [InlineData(40.0, 55)]
    public void PlausibleButWrongOpeningFix_CorrectedWithinTheDwell_IsStillPaid(double offsetNm, int heldSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));

        // ~60 nm per degree of latitude.
        var t = Hold(rig, BristolLat + offsetNm / 60.0, BristolLon, 0, heldSeconds);
        t = Hold(rig, BristolLat, BristolLon, t, 120);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(rig.SectorWouldBePaid, $"a clean sector was voided by an opening fix {offsetNm} nm out for {heldSeconds}s");
    }

    /// <summary>
    /// Was the worst of the characterised defects, and the one least like the original bug: a wrong
    /// opening fix CLOSE ENOUGH TO BE BELIEVED (inside
    /// <see cref="FlightIntegrityMonitor.StartingFixToleranceNm"/>, so guard 1 never fires) needed
    /// only <see cref="FlightIntegrityMonitor.CorroborationDwell"/> - sixty seconds - to void a
    /// clean sector. Two nautical miles was enough, and so was any offset above roughly 62 m, which
    /// is all it takes to exceed <see cref="FlightIntegrityMonitor.ImpossibleGroundSpeedKt"/> across
    /// <see cref="FlightIntegrityMonitor.MinimumJudgeableInterval"/>.
    /// <para>
    /// Now closed by TWO independent measures, which is worth keeping visible: the two-mile row is
    /// covered by <see cref="FlightIntegrityMonitor.NegligibleJumpDistanceNm"/> (an excursion that
    /// small cannot be worth making), and the eight-, forty- and ninety-five-mile rows by the
    /// departure-correction rule (the correction LANDS at the departure airport). If either measure
    /// regresses, only some of these rows go red, which localises it immediately.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(2.0, 70)]
    [InlineData(8.0, 90)]
    [InlineData(40.0, 90)]
    [InlineData(95.0, 300)]
    public void PlausibleButWrongOpeningFixHeldPastTheDwell_IsStillPaid(double offsetNm, int heldSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));

        var t = Hold(rig, BristolLat + offsetNm / 60.0, BristolLon, 0, heldSeconds);
        t = Hold(rig, BristolLat, BristolLon, t, 120);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(
            offsetNm < FlightIntegrityMonitor.StartingFixToleranceNm,
            "this case is about fixes guard 1 BELIEVES; a larger offset would be a different case");
        Assert.False(rig.Monitor.PositionJumpDetected);
        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// The flight departs somewhere the route did not expect - the player repositioned, or is flying
    /// the aircraft from where it actually parked. Guard 1's expected-start check has nothing useful
    /// to say here and must not turn that into a lost sector.
    /// </summary>
    [Fact]
    public void SectorFlownFromADifferentAirportThanTheRouteExpected_IsStillPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, GlasgowLat, GlasgowLon, 0, 180);
        Fly(rig, GlasgowLat, GlasgowLon, t, 1800);

        Assert.True(
            GreatCircle.DistanceNm(BristolLat, BristolLon, GlasgowLat, GlasgowLon) > FlightIntegrityMonitor.StartingFixToleranceNm,
            "this test proves nothing unless the actual departure really is outside the starting tolerance");
        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>Tracking begun with the aircraft already airborne and hundreds of miles down-route -
    /// picking a flight up halfway, which the app permits.</summary>
    [Fact]
    public void SectorStartedWhileAlreadyAirborneFarFromDeparture_IsStillPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        // Six degrees of latitude north of Bristol: ~360 nm, well past the starting tolerance.
        Fly(rig, BristolLat + 6.0, BristolLon, 0, 2400);

        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// The sim link drops mid-cruise and comes back reporting a STALE position for a while before
    /// catching up - the gate is rebuilt on reconnect (see <c>SimTelemetryService.AcceptPosition</c>)
    /// but the monitor is not, so this is the one place mid-flight bad data meets a monitor whose
    /// dwell is already long past the corroboration threshold.
    /// </summary>
    /// <param name="outageSeconds">How long the link was down - i.e. how far the aircraft really moved.</param>
    /// <param name="staleSeconds">How long the stale position is repeated after the link returns.</param>
    /// <summary>
    /// The benign version, first: the link drops and comes back reporting the truth straight away.
    /// The transition across the outage is measured over the outage's own length, so it implies an
    /// entirely ordinary ground speed and nothing is flagged. This is the case that makes the
    /// characterisation below meaningful - it is the STALENESS that does the damage, not the outage.
    /// </summary>
    [Theory]
    [InlineData(120)]
    [InlineData(1200)]
    public void SimReconnectMidFlightReportingTheTruthImmediately_IsStillPaid(int outageSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterCruise, latAtDrop) = Fly(rig, BristolLat, BristolLon, t, 1200);

        var resumeT = afterCruise + outageSeconds;
        var latAtResume = latAtDrop + 460.0 * outageSeconds / 3600.0 / 60.0;

        rig.SimulateSimReconnect();
        Fly(rig, latAtResume, BristolLon, resumeT, 1200);

        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// Was the clearest of the characterised defects, because it needed no design judgement to call
    /// wrong. The link drops mid-cruise and comes back reporting a STALE position for a moment before
    /// catching up; two seconds of staleness was enough to void the sector. The stale fixes bridge
    /// the outage, so by the time the true position arrives the monitor's previous sample is only
    /// milliseconds old while the aircraft has really moved however far the outage lasted - the
    /// monitor then divides a real distance by an interval that never contained it.
    /// <para>
    /// Closed by <see cref="FlightIntegrityMonitor.NotifyTelemetryInterrupted"/>, which drops the
    /// previous sample and the dwell behind it, plus the "has the resumed feed proved itself?" rule
    /// that stops an unchanging reading rebuilding dwell out of stale packets. The rig now models
    /// both halves of the app's reconnect handling - see <see cref="Rig.SimulateSimReconnect"/>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(120, 2)]
    [InlineData(120, 30)]
    [InlineData(120, 150)]
    [InlineData(1200, 2)]
    [InlineData(1200, 30)]
    [InlineData(1200, 150)]
    public void SimReconnectMidFlightEmittingAStalePosition_IsStillPaid(int outageSeconds, int staleSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);

        // Twenty minutes of real flight before the link drops, so the monitor is deep in its
        // "corroborated" steady state and nothing about acquisition is doing the work here.
        var (afterCruise, latAtDrop) = Fly(rig, BristolLat, BristolLon, t, 1200);

        // The link is down: no samples at all for the outage. Wall-clock timestamps keep running,
        // which is what the real source does (SimConnectSource stamps DateTimeOffset.UtcNow).
        var resumeT = afterCruise + outageSeconds;
        var latAtResume = latAtDrop + 460.0 * outageSeconds / 3600.0 / 60.0;

        rig.SimulateSimReconnect();

        // ...and it comes back insisting the aircraft is still where it was when the link dropped.
        var afterStale = Hold(rig, latAtDrop, BristolLon, resumeT, staleSeconds);
        Fly(rig, latAtResume, BristolLon, afterStale, 1200);

        Assert.False(rig.Monitor.PositionJumpDetected);
        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>The sim is paused mid-cruise: the position stops moving, wall-clock timestamps keep
    /// running. MSFS has reported the rate as both 1.0 and 0.0 in this state, so both are checked -
    /// a zero rate falls back to 1x inside the monitor, which only makes the check stricter.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.0)]
    public void SimPausedMidCruise_IsStillPaid(double reportedRateWhilePaused)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterCruise, lat) = Fly(rig, BristolLat, BristolLon, t, 900);
        var afterPause = Hold(rig, lat, BristolLon, afterCruise, 600, simulationRate: reportedRateWhilePaused);
        Fly(rig, lat, BristolLon, afterPause, 900);

        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>Time acceleration through cruise, at the rates MSFS actually offers. Position deltas
    /// are inflated by exactly the reported rate, which is the normalisation the monitor performs.</summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(4.0)]
    [InlineData(8.0)]
    [InlineData(16.0)]
    [InlineData(128.0)]
    public void ElevatedSimRateThroughCruise_IsStillPaid(double rate)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterClimb, lat) = Fly(rig, BristolLat, BristolLon, t, 600);
        var (afterCruise, lat2) = Fly(rig, lat, BristolLon, afterClimb, 600, simulationRate: rate);
        Fly(rig, lat2, BristolLon, afterCruise, 600);

        Assert.True(rig.Monitor.ElevatedSimRateDetected);
        Assert.Equal(rate, rig.Monitor.MaxSimulationRateObserved);
        Assert.True(rig.SectorWouldBePaid, $"an ordinary {rate}x cruise voided the sector");
        // Elevated rate is explicitly NOT a payment gate - only slew and a position jump are.
        Assert.False(rig.Monitor.PositionJumpDetected);
        Assert.False(rig.Monitor.SlewDetected);
    }

    /// <summary>
    /// The instant the player changes rate, ONE transition straddles two different reported rates:
    /// the interval was flown at the new rate, but the monitor normalises it by the average of the
    /// old and the new. That under-normalisation is bounded - for a step from R1 to R2 the implied
    /// speed is <c>groundSpeed x 2 x R2 / (R1 + R2)</c>, which tends to twice the true ground speed
    /// however large the step is.
    /// <para>
    /// Twice ground speed is the number that matters, because
    /// <see cref="FlightIntegrityMonitor.ImpossibleGroundSpeedKt"/> is 1,200. Ordinary airliner
    /// ground speeds leave plenty of room; the margin is not unlimited, which is what the
    /// characterisation below records.
    /// </para>
    /// <para>
    /// This models the rate change the way the sim really delivers it: the arriving sample reports
    /// the NEW rate, because <c>SimulationRate</c> and the position are fields of one
    /// <c>TelemetryData</c> struct filled by a single SimConnect request and delivered as one object
    /// - they are a simultaneous snapshot and cannot lag each other by a frame. Verified in
    /// <c>SimConnectSource.HandleTelemetry</c> / <c>TelemetryDataDefinitions</c>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1.0, 2.0, 460.0)]
    [InlineData(1.0, 4.0, 460.0)]
    [InlineData(1.0, 128.0, 460.0)]
    [InlineData(1.0, 128.0, 520.0)]
    [InlineData(128.0, 1.0, 460.0)]
    [InlineData(2.0, 4.0, 620.0)]
    public void SimRateChangedAbruptlyMidCruise_IsStillPaid(double fromRate, double toRate, double groundSpeedKt)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterClimb, nextLat) = Fly(rig, BristolLat, BristolLon, t, 600, groundSpeedKt, simulationRate: fromRate);

        // Fly returns where the NEXT sample would go, so step back one to get the last one actually
        // fed - the transition under test has to be exactly one new-rate step, no more.
        var lastFedLat = nextLat - groundSpeedKt * fromRate * Step / 3600.0 / 60.0;
        var degreesPerStep = groundSpeedKt * toRate * Step / 3600.0 / 60.0;

        // The first sample at the NEW rate, having already covered a full new-rate step of ground.
        rig.Feed(At(afterClimb, lastFedLat + degreesPerStep, BristolLon, simulationRate: toRate));
        var (afterCruise, lat2) = Fly(
            rig, lastFedLat + 2 * degreesPerStep, BristolLon, afterClimb + Step, 600, groundSpeedKt, simulationRate: toRate);
        Fly(rig, lat2, BristolLon, afterCruise, 600, groundSpeedKt, simulationRate: toRate);

        Assert.True(
            rig.SectorWouldBePaid,
            $"a {fromRate}x -> {toRate}x rate change at {groundSpeedKt} kt voided the sector");
    }

    /// <summary>
    /// Was a characterised defect: a single LARGE rate step at high ground speed pushed the
    /// under-normalised transition past 1,200 kt, because averaging the two reported rates implies
    /// <c>groundSpeed x 2R2/(R1+R2)</c> - twice the true ground speed at the limit. Anything over
    /// ~600 kt over the ground tripped it, which is an airliner eastbound in a jet stream, not an
    /// exotic case. Closed by normalising on the MAXIMUM of the two rates
    /// (<see cref="FlightIntegrityMonitor.NormalisingSimulationRate"/>), which can only ever
    /// over-divide - the fail-open direction.
    /// <para>
    /// CADENCE MATTERS HERE AND THE ROWS ARE CHOSEN FOR IT. At 5 Hz a 1x-&gt;128x step at 640 kt
    /// covers 4.55 nm, which is under
    /// <see cref="FlightIntegrityMonitor.NegligibleJumpDistanceNm"/> - so at that cadence the
    /// five-mile floor would mask a regression here and this test would pass for the wrong reason.
    /// The sim source requests every sixth frame, so at 20 or 10 fps the same step covers 6.8 or
    /// 13.6 nm and the floor cannot help. The coarse-cadence rows are the ones with teeth; the 5 Hz
    /// rows are kept only so the contrast stays on the record.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(64.0, 640.0, 5.0)]
    [InlineData(128.0, 640.0, 5.0)]
    [InlineData(128.0, 700.0, 5.0)]
    [InlineData(64.0, 640.0, 20.0 / 6.0)]
    [InlineData(128.0, 640.0, 20.0 / 6.0)]
    [InlineData(128.0, 700.0, 20.0 / 6.0)]
    [InlineData(128.0, 640.0, 10.0 / 6.0)]
    [InlineData(128.0, 700.0, 10.0 / 6.0)]
    public void LargeSimRateStepAtHighGroundSpeed_IsStillPaid(double toRate, double groundSpeedKt, double hz)
    {
        var step = 1.0 / hz;
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterClimb, nextLat) = Fly(rig, BristolLat, BristolLon, t, 600, groundSpeedKt, hz: hz);

        // See SimRateChangedAbruptlyMidCruise_IsStillPaid: step back one to the last sample fed, so
        // the transition under test is exactly one new-rate step and nothing else.
        var lastFedLat = nextLat - groundSpeedKt * step / 3600.0 / 60.0;
        var degreesPerStep = groundSpeedKt * toRate * step / 3600.0 / 60.0;
        rig.Feed(At(afterClimb, lastFedLat + degreesPerStep, BristolLon, simulationRate: toRate));
        Fly(rig, lastFedLat + 2 * degreesPerStep, BristolLon, afterClimb + step, 600, groundSpeedKt,
            simulationRate: toRate, hz: hz);

        // The arithmetic the old averaging produced, stated so the boundary is not a mystery: it
        // implied gs * 2R/(1+R) against a 1,200 kt threshold. Asserted so these rows cannot quietly
        // stop exercising the regression.
        var impliedUnderAveraging = groundSpeedKt * 2 * toRate / (1 + toRate);
        Assert.True(impliedUnderAveraging > FlightIntegrityMonitor.ImpossibleGroundSpeedKt);

        Assert.False(rig.Monitor.PositionJumpDetected);
        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// Guards the cadence reasoning in the test above rather than trusting my arithmetic: at 5 Hz
    /// the worst rate step really is under the five-mile floor, and at the sim source's coarser real
    /// cadences it really is not. If this ever stops holding, the rows above have changed meaning.
    /// </summary>
    [Fact]
    public void TheFiveMileFloorMasksTheRateStepAtFiveHertzButNotAtRealisticFrameRates()
    {
        double StepDistanceNm(double hz) => 640.0 * 128.0 / 3600.0 / hz;

        Assert.True(StepDistanceNm(5.0) < FlightIntegrityMonitor.NegligibleJumpDistanceNm);
        Assert.True(StepDistanceNm(20.0 / 6.0) > FlightIntegrityMonitor.NegligibleJumpDistanceNm);
        Assert.True(StepDistanceNm(10.0 / 6.0) > FlightIntegrityMonitor.NegligibleJumpDistanceNm);
    }

    /// <summary>Below 2,000 ft the sim source switches to per-frame delivery, so samples arrive
    /// 10-30 ms apart - short enough that the raw gap would divide any position noise out to an
    /// enormous speed. The 100 ms clamp exists for exactly this; prove it holds at 60 Hz.</summary>
    [Fact]
    public void PerFrameSamplingNearTheGround_IsStillPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        Fly(rig, BristolLat, BristolLon, t, 600, groundSpeedKt: 160.0, hz: 60.0);

        Assert.True(rig.SectorWouldBePaid);
    }

    // ---------------------------------------------------------------------------------------
    // Second pass: attacks aimed at the fix itself, not at the bugs it closed.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A resumed feed that JITTERS rather than freezing. This is the shape the fix's own author
    /// named as unproven, and it matters because an implausible transition clears
    /// "awaiting proof the feed resumed" unconditionally - so jitter can hand back the right to
    /// accumulate dwell, and a stale freeze after it can then rebuild the dwell that condemns the
    /// sector when the truth arrives.
    /// <para>
    /// The gate is what decides whether this is reachable, which is why it has to be tested through
    /// the composite rather than against the monitor alone: jitter cannot corroborate itself, so a
    /// burst shorter than <see cref="PositionAcquisitionGate.AcquisitionTimeout"/> is withheld
    /// entirely and never reaches the monitor at all. Longer than that and the gate fails open and
    /// starts passing it. Both sides of that boundary are covered here.
    /// </para>
    /// </summary>
    /// <param name="jitterSeconds">
    /// Spans <see cref="PositionAcquisitionGate.AcquisitionTimeout"/> deliberately. Below it the gate
    /// withholds the whole burst and the monitor never sees the jitter at all; above it the gate
    /// fails open and the jitter really does arrive, which is where this used to void the sector -
    /// an implausible transition cleared the resumed-feed flag unconditionally, so jitter certified a
    /// link that had proved nothing, and a stale freeze after it rebuilt the dwell that condemned the
    /// correction. Only a plausible transition can certify the link now, so both sides of that
    /// boundary have to pay, and the rows above and below 20 s are what proves it.
    /// </param>
    [Theory]
    [InlineData(5, 90)]
    [InlineData(10, 90)]
    [InlineData(19, 90)]
    [InlineData(30, 90)]
    [InlineData(60, 90)]
    [InlineData(30, 150)]
    [InlineData(120, 300)]
    public void SimReconnectResumingWithJitterThenAStaleFreeze_IsStillPaid(int jitterSeconds, int freezeSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterCruise, latAtDrop) = Fly(rig, BristolLat, BristolLon, t, 1200);

        const int outageSeconds = 120;
        var resumeT = afterCruise + outageSeconds;
        var latAtResume = latAtDrop + 460.0 * outageSeconds / 3600.0 / 60.0;

        rig.SimulateSimReconnect();

        // The feed comes back thrashing between two absurd readings - nothing can vouch for anything.
        var jitterT = resumeT;
        for (var i = 0; i < (int)(jitterSeconds * Hz); i++, jitterT += Step)
        {
            var (lat, lon) = i % 2 == 0 ? (BadFixLat, BadFixLon) : (0.0, -90.0);
            rig.Feed(At(jitterT, lat, lon));
        }

        // ...then settles on the position the aircraft had when the link died, and holds it.
        var afterFreeze = Hold(rig, latAtDrop, BristolLon, jitterT, freezeSeconds);

        // ...and only then tells the truth.
        Fly(rig, latAtResume, BristolLon, afterFreeze, 1200);

        Assert.True(
            rig.SectorWouldBePaid,
            $"a clean sector was voided by {jitterSeconds}s of jitter then {freezeSeconds}s of stale position after a reconnect");
    }

    /// <summary>A resumed feed that jitters and then simply tells the truth, with no stale phase at
    /// all - the simpler half of the case above, kept separate so a failure localises. Safe at every
    /// duration, because without the freeze there is no dwell to rebuild.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    public void SimReconnectResumingWithJitterThenTheTruth_IsStillPaid(int jitterSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterCruise, latAtDrop) = Fly(rig, BristolLat, BristolLon, t, 1200);

        const int outageSeconds = 120;
        var resumeT = afterCruise + outageSeconds;
        var latAtResume = latAtDrop + 460.0 * outageSeconds / 3600.0 / 60.0;

        rig.SimulateSimReconnect();

        var jitterT = resumeT;
        for (var i = 0; i < (int)(jitterSeconds * Hz); i++, jitterT += Step)
        {
            var (lat, lon) = i % 2 == 0 ? (BadFixLat, BadFixLon) : (0.0, -90.0);
            rig.Feed(At(jitterT, lat, lon));
        }

        Fly(rig, latAtResume, BristolLon, jitterT, 1200);

        Assert.True(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// Was a characterised defect and is the ORIGINAL bug's shape: a garbage opening fix, held, then
    /// corrected - with the single addition that the garbage reading is not bit-identical every
    /// sample. The departure-correction exemption used to be spent by a running SUM of
    /// per-transition distance, which is PATH LENGTH, not displacement; a wrong fix with a metre of
    /// wander per sample has zero displacement but unbounded path length, so it crept past the 90 m
    /// threshold while going nowhere and spent the exemption before the brakes came off.
    /// <para>
    /// Closed by measuring displacement from the first position the monitor ever accepted. Kept
    /// parameterised across 40, 95 and 5,505 nm because those three exercise different guards on the
    /// way in: the first two are inside <see cref="FlightIntegrityMonitor.StartingFixToleranceNm"/>
    /// and believed immediately, the third is rejected until the acquisition window expires.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(40.0, 90)]
    [InlineData(95.0, 90)]
    [InlineData(5505.0, 180)]
    public void WrongOpeningFixCarryingOrdinaryNoise_ThenACleanSector_IsStillPaid(double offsetNm, int heldSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));

        // A metre or so of wander per sample, never in one direction for long - float noise, not
        // movement. ~60 nm per degree of latitude, so 1 m is about 1.5e-5 degrees.
        const double noiseDeg = 1.5e-5;
        var wrongLat = BristolLat + offsetNm / 60.0;
        var t = 0.0;
        for (var i = 0; i < (int)(heldSeconds * Hz); i++, t += Step)
        {
            rig.Feed(At(t, wrongLat + (i % 2 == 0 ? noiseDeg : -noiseDeg), BristolLon));
        }

        t = Hold(rig, BristolLat, BristolLon, t, 120);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        // The same case with a PERFECTLY still wrong fix is
        // PlausibleButWrongOpeningFixHeldPastTheDwell_IsStillPaid. Noise was the whole difference
        // between them, which is what identified path-length-versus-displacement as the cause; both
        // must now pay, and keeping the pair adjacent is what would make a relapse obvious.
        Assert.False(rig.Monitor.PositionJumpDetected);
        Assert.True(
            rig.SectorWouldBePaid,
            $"a clean sector was voided by a noisy opening fix {offsetNm} nm out held for {heldSeconds}s");
    }

    /// <summary>
    /// The shape "never reset" invites: the first position the monitor accepts is GOOD, and the
    /// garbage arrives afterwards. Displacement is then measured from the true stand, so a glitch
    /// that reports the aircraft far away is displaced from the origin by construction - the
    /// aircraft has not moved an inch, but the reading has - and if that is enough to spend the
    /// departure-correction exemption, the correction back to the stand is judged as a teleport.
    /// <para>
    /// This is the mirror of the case the exemption was built for. The original bug put the garbage
    /// FIRST, which makes the origin garbage and the true position the outlier; here a settled,
    /// believed opening fix is followed by a glitch, which is what a scenery reload or a mid-preflight
    /// sim hiccup produces after FSOps has already been tracking happily for a minute.
    /// </para>
    /// <para>
    /// Run with the aircraft correctly reporting ON GROUND throughout, so the airborne disjunct
    /// cannot be what spends the latch and the only thing under test is the distance one.
    /// </para>
    /// </summary>
    /// <param name="glitchOffsetNm">
    /// Under <see cref="FlightIntegrityMonitor.DepartureCorrectionRadiusNm"/> the reported position
    /// is still "at the departure airport", so the latch's distance disjunct never fires and the
    /// correction is excused. Beyond it, see
    /// <see cref="GoodOpeningFixThenASustainedGlitchBeyondTheDepartureRadius_IsStillPaid"/>.
    /// </param>
    /// <param name="glitchSeconds">
    /// Under <see cref="FlightIntegrityMonitor.CorroborationDwell"/> the glitch never corroborates
    /// itself, so the correction opens no suspicion whatever else is true. This is the bound that
    /// makes the defect below narrow rather than routine.
    /// </param>
    [Theory]
    [InlineData(2.0, 90)]
    [InlineData(8.0, 90)]
    [InlineData(40.0, 5)]
    [InlineData(40.0, 30)]
    [InlineData(40.0, 55)]
    [InlineData(95.0, 55)]
    public void GoodOpeningFixThenAGlitchExcursionThenTheTruth_IsStillPaid(double glitchOffsetNm, int glitchSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));

        // A settled, believed opening fix on the stand - this is what becomes the displacement origin.
        var t = Hold(rig, BristolLat, BristolLon, 0, 30, onGround: true);

        // Then the sim reports the aircraft somewhere else entirely, and holds it there.
        t = Hold(rig, BristolLat + glitchOffsetNm / 60.0, BristolLon, t, glitchSeconds, onGround: true);

        // Then it corrects, and the flight proceeds normally.
        t = Hold(rig, BristolLat, BristolLon, t, 120, onGround: true);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(
            rig.SectorWouldBePaid,
            $"a clean sector was voided by a {glitchOffsetNm} nm glitch held {glitchSeconds}s after a good opening fix");
    }

    /// <summary>
    /// CLOSED - this was a defect and is now the guarantee. Kept inverted rather than deleted,
    /// because the shape is the one most likely to come back.
    /// <para>
    /// It was the mirror of the case the departure exemption was built for. Displacement from the
    /// first observed position is evidence that the READING moved, not that the AIRCRAFT did. With
    /// the bad fix first - the original incident - the two coincide and the guard holds, since a bad
    /// reading has no displacement of its own. Reverse the order and it stopped holding: a good
    /// opening fix became the origin, a later glitch reported forty miles away, and that reading
    /// genuinely was displaced from the origin though the aircraft had not moved.
    /// </para>
    /// <para>
    /// Two changes closed it. The origin now follows continuity, so an implausible transition moves
    /// it rather than leaving it fixed for the whole session; and a confirmed jump is withdrawn when
    /// the aircraft turns out to be back where the jump began, so a correction arriving after the
    /// dwell has elapsed still counts.
    /// </para>
    /// <para>
    /// The bounds are kept as assertions rather than comments: the glitch sits beyond
    /// <see cref="FlightIntegrityMonitor.DepartureCorrectionRadiusNm"/> and outlasts a full
    /// <see cref="FlightIntegrityMonitor.CorroborationDwell"/>, which is what made it hard.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(40.0, 90)]
    [InlineData(95.0, 90)]
    [InlineData(40.0, 300)]
    public void GoodOpeningFixThenASustainedGlitchBeyondTheDepartureRadius_IsStillPaid(
        double glitchOffsetNm, int glitchSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 30, onGround: true);
        t = Hold(rig, BristolLat + glitchOffsetNm / 60.0, BristolLon, t, glitchSeconds, onGround: true);
        t = Hold(rig, BristolLat, BristolLon, t, 120, onGround: true);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(glitchOffsetNm > FlightIntegrityMonitor.DepartureCorrectionRadiusNm);
        Assert.True(glitchSeconds > FlightIntegrityMonitor.CorroborationDwell.TotalSeconds);
        Assert.False(rig.Monitor.PositionJumpDetected);
        Assert.True(
            rig.SectorWouldBePaid,
            $"a clean sector was voided by a {glitchOffsetNm} nm glitch held {glitchSeconds}s after the aircraft had settled");
    }

    /// <summary>The same shape reached two other ways, kept because each is a route a real session
    /// could take: repeated glitch/correct cycles before departure, and a reconnect while still on
    /// the stand followed by one.
    /// <para>
    /// The repeated-cycles case is the one worth keeping longest. It used to be unsurvivable for a
    /// reason that is easy to miss - surviving the first glitch is what put the session over the
    /// line for the second, because each correction is itself a hold long enough to rebuild the
    /// dwell. A session that glitched twice could not stay on the safe side however well the first
    /// one was handled.
    /// </para></summary>
    [Fact]
    public void RepeatedGlitchAndCorrectionCyclesBeforeDeparture_AreStillPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 30, onGround: true);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            t = Hold(rig, BristolLat + 40.0 / 60.0, BristolLon, t, 90, onGround: true);
            t = Hold(rig, BristolLat, BristolLon, t, 90, onGround: true);
        }

        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(rig.SectorWouldBePaid, "a sector was voided by glitching more than once before departure");
    }

    [Fact]
    public void SimReconnectOnTheStandThenAGlitchAndCorrection_IsStillPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 60, onGround: true);

        rig.SimulateSimReconnect();

        t = Hold(rig, BristolLat, BristolLon, t + 30, 60, onGround: true);
        t = Hold(rig, BristolLat + 40.0 / 60.0, BristolLon, t, 90, onGround: true);
        t = Hold(rig, BristolLat, BristolLon, t, 120, onGround: true);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(rig.SectorWouldBePaid, "a sector was voided by a glitch after a reconnect on the stand");
    }

    /// <summary>
    /// An unsignalled gap - samples simply stop and restart with no reconnect behind them - is
    /// deliberately NOT treated as an interruption, on the reasoning that honest timestamps keep the
    /// implied speed a true lower bound. Testing that reasoning rather than taking it: across a real
    /// outage the aircraft covers exactly what it flies, so the implied speed is its ground speed
    /// however long the hole is.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(300)]
    [InlineData(1200)]
    [InlineData(3600)]
    public void UnsignalledTelemetryGapWithHonestTimestamps_IsStillPaid(int gapSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterCruise, latAtGap) = Fly(rig, BristolLat, BristolLon, t, 1200);

        // No SimulateSimReconnect: the feed never reported a state change, it just went quiet.
        var resumeT = afterCruise + gapSeconds;
        var latAtResume = latAtGap + 460.0 * gapSeconds / 3600.0 / 60.0;
        Fly(rig, latAtResume, BristolLon, resumeT, 1200);

        Assert.True(rig.SectorWouldBePaid, $"a clean sector was voided by a {gapSeconds}s silent telemetry gap");
    }

    /// <summary>
    /// The one shape where the "silent gaps are safe" reasoning does not hold, probed because the
    /// reasoning is only as good as its assumption: it assumes the aircraft flew at the rate its
    /// endpoints report. If the sim was ACCELERATED entirely inside the gap - so both the last
    /// sample before it and the first after it report 1x - then the ground covered is real but there
    /// is no reported rate anywhere to normalise it by, and the implied speed is the ground speed
    /// times the hidden rate.
    /// <para>
    /// Whether this is reachable in practice is a question about MSFS, not about this code: it needs
    /// telemetry to stall for minutes without the connection state changing, while the player
    /// accelerates and decelerates entirely within the stall. I could not judge that without a
    /// simulator, so this records the arithmetic rather than claiming a likelihood.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(4.0)]
    [InlineData(8.0)]
    public void UnsignalledGapHidingAnEntireSimRateExcursion_IsCharacterised(double hiddenRate)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterCruise, latAtGap) = Fly(rig, BristolLat, BristolLon, t, 1200);

        const int gapSeconds = 300;
        var resumeT = afterCruise + gapSeconds;
        // The aircraft really covered hiddenRate x the usual ground during the hole.
        var latAtResume = latAtGap + 460.0 * hiddenRate * gapSeconds / 3600.0 / 60.0;
        Fly(rig, latAtResume, BristolLon, resumeT, 1200);

        var impliedKt = 460.0 * hiddenRate;
        var shouldTrip = impliedKt > FlightIntegrityMonitor.ImpossibleGroundSpeedKt;

        // Stated as the arithmetic rather than as a flat expectation, so this stays true if the
        // threshold moves: 2x and 4x are under 1,200 kt and pay; 8x is over it and does not.
        Assert.Equal(shouldTrip, rig.Monitor.PositionJumpDetected);
        Assert.Equal(!shouldTrip, rig.SectorWouldBePaid);
    }

    // ---------------------------------------------------------------------------------------
    // Half two: cheats. All of these must still cost the sector.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The departure-correction exemption must be strictly one-way. Landing back where you departed
    /// is a legitimate, PAID outcome (the resolver treats it as a diversion), so an exemption still
    /// available in the air would let a cheat fly a few miles, teleport home, and be paid in full.
    /// </summary>
    [Fact]
    public void TeleportBackToTheDepartureAirportAfterGettingAirborne_IsNotPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);

        // Airborne and away - this is what spends the exemption for good.
        var (afterCruise, lat) = Fly(rig, BristolLat, BristolLon, t, 900);
        Assert.True(GreatCircle.DistanceNm(BristolLat, BristolLon, lat, BristolLon) > FlightIntegrityMonitor.DepartureCorrectionRadiusNm);

        // ...then straight back to the departure stand, and park there.
        Hold(rig, BristolLat, BristolLon, afterCruise, 300);

        Assert.True(rig.Monitor.PositionJumpDetected);
        Assert.False(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// The five-mile floor exists so an excursion too small to be worth making cannot cost a sector.
    /// The cheat it invites is farming it: hop just under the floor, wait for the suspicion to
    /// resolve harmlessly, hop again. This measures what that actually costs rather than asserting
    /// it is impossible, because it is not impossible - it is meant to be uneconomic.
    /// <para>
    /// Each cycle needs a full <see cref="FlightIntegrityMonitor.CorroborationDwell"/> of genuine
    /// plausible flight to clear the suspicion, and that flight covers ground of its own. The ratio
    /// of distance stolen to distance actually flown is the number that matters, and it is asserted
    /// here so that loosening either constant shows up as a failure rather than as a quiet
    /// concession.
    /// </para>
    /// </summary>
    [Fact]
    public void FarmingHopsJustUnderTheFiveMileFloor_IsUneconomicRatherThanImpossible()
    {
        const double hopNm = 4.9;
        const int hops = 40;
        const double cruiseKt = 460.0;

        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterClimb, lat) = Fly(rig, BristolLat, BristolLon, t, 300);

        var stolenNm = 0.0;
        var flownNm = 0.0;
        var startLat = lat;

        for (var i = 0; i < hops; i++)
        {
            // The hop itself: one sample, just under the floor.
            lat += hopNm / 60.0;
            stolenNm += hopNm;
            rig.Feed(At(afterClimb, lat, BristolLon));
            afterClimb += Step;

            // Then the flight required to clear the suspicion before the next hop can be free.
            // 65 s rather than 60 so the dwell is genuinely satisfied at this cadence.
            var (next, nextLat) = Fly(rig, lat, BristolLon, afterClimb, 65, cruiseKt);
            flownNm += cruiseKt * 65 / 3600.0;
            afterClimb = next;
            lat = nextLat;
        }

        Fly(rig, lat, BristolLon, afterClimb, 300);

        // It works - that is the honest finding, and it is the deliberate cost of the floor.
        Assert.True(rig.SectorWouldBePaid);
        Assert.Equal(hopNm * hops, stolenNm, 3);

        // But it buys a third of a mile for every mile actually flown, so a 600 nm sector still
        // needs hundreds of miles of real flying and hours of real time. If this ratio ever improves
        // materially, the floor or the dwell has been loosened and this test is the alarm.
        var stolenPerMileFlown = stolenNm / flownNm;
        Assert.True(
            stolenPerMileFlown < 0.75,
            $"hop farming now steals {stolenPerMileFlown:F2} nm per nm actually flown, which is no longer uneconomic");

        var totalToSteal600Nm = 600.0 / stolenPerMileFlown;
        Assert.True(
            totalToSteal600Nm > 500,
            $"stealing 600 nm now costs only {totalToSteal600Nm:F0} nm of real flying");
    }

    /// <summary>Contiguous hops under the floor are one excursion, not many, because the suspected
    /// distance is measured from the origin across the whole run - so a reposition delivered as a
    /// sweep of small steps is still measured as the single large move it really is.</summary>
    [Theory]
    [InlineData(4.9, 20)]
    [InlineData(2.0, 50)]
    [InlineData(0.5, 200)]
    public void ContiguousHopsUnderTheFiveMileFloor_IsNotPaid(double hopNm, int hops)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (afterClimb, lat) = Fly(rig, BristolLat, BristolLon, t, 300);

        for (var i = 0; i < hops; i++, afterClimb += Step)
        {
            lat += hopNm / 60.0;
            rig.Feed(At(afterClimb, lat, BristolLon));
        }

        Fly(rig, lat, BristolLon, afterClimb, 300);

        Assert.True(hopNm * hops > FlightIntegrityMonitor.NegligibleJumpDistanceNm);
        Assert.True(rig.Monitor.PositionJumpDetected, $"{hops} contiguous {hopNm} nm hops were absorbed");
        Assert.False(rig.SectorWouldBePaid);
    }

    /// <summary>A reconnect must not launder evidence: a suspicion already outstanding when the link
    /// drops has to survive it, or dropping the link becomes the cheat.</summary>
    [Fact]
    public void TeleportFollowedByAReconnectBeforeItCouldBeConfirmed_IsNotPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (beforeJump, lat) = Fly(rig, BristolLat, BristolLon, t, 600);

        // 600 nm north in one sample, then the link drops almost immediately - well before the
        // suspicion could be corroborated by onward flight.
        var jumpedLat = lat + 10.0;
        var (afterShortRun, latAfter) = Fly(rig, jumpedLat, BristolLon, beforeJump, 5);

        rig.SimulateSimReconnect();
        Fly(rig, latAfter, BristolLon, afterShortRun + 60, 900);

        Assert.True(rig.Monitor.PositionJumpDetected, "a reconnect laundered an outstanding suspicion");
        Assert.False(rig.SectorWouldBePaid);
    }

    /// <summary>Slew found before a reconnect must survive it too - the flags are findings, not
    /// continuity state.</summary>
    [Fact]
    public void SlewFollowedByAReconnect_IsNotPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 60, isSlewActive: true);
        rig.SimulateSimReconnect();
        Fly(rig, BristolLat, BristolLon, t + 60, 1800);

        Assert.True(rig.Monitor.SlewDetected);
        Assert.False(rig.SectorWouldBePaid);
    }

    [Fact]
    public void MidFlightTeleportWithRealFlightEitherSide_IsNotPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (beforeJump, lat) = Fly(rig, BristolLat, BristolLon, t, 600);

        // 600 nm north in a single sample interval.
        var jumpedLat = lat + 10.0;
        Fly(rig, jumpedLat, BristolLon, beforeJump, 600);

        Assert.True(rig.Monitor.PositionJumpDetected);
        Assert.False(rig.SectorWouldBePaid);
    }

    /// <summary>The teleport, then the aircraft parked at the far end rather than flown on - a
    /// "stationary is plausible" corroboration must confirm the suspicion just as readily as flying
    /// on does, or parking after the jump would launder it.</summary>
    [Fact]
    public void MidFlightTeleportFollowedByParkingAtTheDestination_IsNotPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (beforeJump, lat) = Fly(rig, BristolLat, BristolLon, t, 600);

        Hold(rig, lat + 10.0, BristolLon, beforeJump, 300);

        Assert.True(rig.Monitor.PositionJumpDetected);
        Assert.False(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// A SUSTAINED reposition, which is what slewing actually looks like: not one impossible
    /// transition but a long run of them. An earlier shape of this fix absorbed exactly this, because
    /// each new impossible transition reset the clock and nothing was left holding the suspicion.
    /// Run with the slew simvar OFF, so position data alone has to catch it.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(120)]
    public void SustainedSlewWithTheSlewSimvarNeverReported_IsNotPaid(int slewSeconds)
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (beforeSlew, lat) = Fly(rig, BristolLat, BristolLon, t, 600);

        // ~6,000 kt of "flight" - five times the impossible threshold, so every single transition
        // through the run is individually impossible.
        var (afterSlew, latAfterSlew) = Fly(rig, lat, BristolLon, beforeSlew, slewSeconds, groundSpeedKt: 6000.0);
        Fly(rig, latAfterSlew, BristolLon, afterSlew, 600);

        Assert.True(rig.Monitor.PositionJumpDetected, $"{slewSeconds}s of sustained repositioning was absorbed");
        Assert.False(rig.SectorWouldBePaid);
    }

    [Fact]
    public void SlewSimvarActiveForASingleSample_IsNotPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        rig.Feed(At(t, BristolLat, BristolLon, isSlewActive: true));
        Fly(rig, BristolLat, BristolLon, t + Step, 600);

        Assert.True(rig.Monitor.SlewDetected);
        Assert.False(rig.SectorWouldBePaid);
    }

    /// <summary>Slew reported during the very first moments of tracking, before anything is
    /// corroborated - the simvar needs no corroboration at all, so the gate withholding the opening
    /// position must not also swallow the finding.</summary>
    [Fact]
    public void SlewReportedOnTheVeryFirstSamples_IsNotPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 5, isSlewActive: true);
        Fly(rig, BristolLat, BristolLon, t, 1800);

        Assert.True(rig.Monitor.SlewDetected);
        Assert.False(rig.SectorWouldBePaid);
    }

    /// <summary>A cheat that teleports and then keeps flying, but chose an elevated sim rate hoping
    /// the normalisation would divide the jump away. 600 nm in one step at 128x still implies
    /// ~84,000 kt.</summary>
    [Fact]
    public void MidFlightTeleportAttemptedUnderTimeAcceleration_IsNotPaid()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (beforeJump, lat) = Fly(rig, BristolLat, BristolLon, t, 600, simulationRate: 128.0);
        Fly(rig, lat + 10.0, BristolLon, beforeJump, 600, simulationRate: 128.0);

        Assert.True(rig.Monitor.PositionJumpDetected);
        Assert.False(rig.SectorWouldBePaid);
    }

    /// <summary>
    /// The known, deliberate gap, asserted so it is a recorded decision rather than a surprise: a
    /// teleport in the last minute of tracking is never confirmed, because confirmation requires
    /// <see cref="FlightIntegrityMonitor.CorroborationDwell"/> of flight AFTER it and the flight
    /// ends first. Slew - how this is actually done - is still caught outright by its own simvar.
    /// </summary>
    [Fact]
    public void TeleportInTheFinalSecondsOfTracking_IsNotCaught_AndThatIsTheAcceptedCost()
    {
        var rig = new Rig((BristolLat, BristolLon));
        var t = Hold(rig, BristolLat, BristolLon, 0, 120);
        var (beforeJump, lat) = Fly(rig, BristolLat, BristolLon, t, 600);
        Fly(rig, lat + 10.0, BristolLon, beforeJump, 20);

        Assert.False(rig.Monitor.PositionJumpDetected);
    }
}

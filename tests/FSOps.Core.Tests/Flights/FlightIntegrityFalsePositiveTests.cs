using FSOps.Core.Flights;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Flights;

/// <summary>
/// Three ways an honestly-flown sector could still be voided, driven through the REAL pipeline order
/// rather than against the monitor alone: <see cref="PositionAcquisitionGate.Accept"/> first, then
/// <see cref="FlightIntegrityMonitor.Observe"/>, at the sim's own ~5 Hz, with the flight's real
/// departure position supplied exactly as <c>FlightEndpoints</c> supplies it.
/// <para>
/// Every case here is bad DATA, not a cheat: a first fix that is wrong but nearby, a first fix that
/// is wrong and stuck, and a sim link that drops mid-flight and comes back replaying the position it
/// last had. None of them gains the player a single mile, and none of them may cost a sector. The
/// companion assertions at the bottom hold the other side of the line: everything that IS a cheat is
/// still caught.
/// </para>
/// </summary>
public class FlightIntegrityFalsePositiveTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The sim source's normal cadence - five samples a second.</summary>
    private const double SampleIntervalSeconds = 0.2;

    /// <summary>Bristol's stand, as the first real flight's own second (first believable) fix recorded it.</summary>
    private static readonly (double Lat, double Lon) Stand = (51.38526, -2.71770);

    /// <summary>Roughly one nautical mile of latitude, so a test can say "wrong by 8 nm" and mean it.</summary>
    private static (double Lat, double Lon) North((double Lat, double Lon) from, double nm) =>
        (from.Lat + nm / 60.0, from.Lon);

    /// <summary>The opposite of <see cref="North"/>. Every flight here departs northbound, so a bad
    /// opening fix is placed to the SOUTH deliberately: put it north and the sector flies straight
    /// back through it, which dismisses the suspicion by accident and would make the test pass for a
    /// reason that has nothing to do with the rule being tested.</summary>
    private static (double Lat, double Lon) South((double Lat, double Lon) from, double nm) =>
        (from.Lat - nm / 60.0, from.Lon);

    [Theory]
    [InlineData(2.0)]
    [InlineData(8.0)]
    [InlineData(40.0)]
    [InlineData(95.0)]
    public void OpeningFixWrongButNearby_ThenTheTruePosition_DoesNotVoidTheSector(double wrongByNm)
    {
        // Case 1. The opening fix is inside StartingFixToleranceNm, so it is believed rather than
        // discarded, and it is held long enough to build a full CorroborationDwell behind it. When
        // the aircraft's real position finally arrives, the correction reads as a teleport FROM a
        // place the aircraft never was. Two miles is the size of error an ordinary scenery load or
        // ground settle produces.
        var pipeline = new Pipeline(Stand);
        var wrong = South(Stand, wrongByNm);

        pipeline.Hold(wrong, seconds: 90);
        pipeline.Hold(Stand, seconds: 30);
        pipeline.Fly(Stand, seconds: 300);

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(75)]
    [InlineData(125)]
    [InlineData(180)]
    [InlineData(600)]
    public void StuckGarbageOpeningFix_HeldForAnyLength_DoesNotVoidTheSector(double heldSeconds)
    {
        // Case 2. The uninitialised SimConnect fix that started all of this - 0.0N 90.0E, 5,505 nm
        // from the stand - but STUCK rather than momentary. The acquisition gate cannot help: a
        // stationary fix corroborates itself, because two identical packets agree. Past
        // StartingFixAcquisitionWindow + CorroborationDwell the monitor starts believing it, and the
        // arrival of the truth condemns the sector.
        var pipeline = new Pipeline(Stand);

        pipeline.Hold((0.0, 90.0), heldSeconds);
        pipeline.Hold(Stand, seconds: 30);
        pipeline.Fly(Stand, seconds: 300);

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(120, 2)]
    [InlineData(120, 30)]
    [InlineData(120, 90)]
    [InlineData(120, 240)]
    [InlineData(300, 5)]
    [InlineData(45, 10)]
    public void ReconnectMidFlight_ReplayingAStalePosition_DoesNotVoidTheSector(double outageSeconds, double staleSeconds)
    {
        // Case 3. Twenty minutes into a clean sector the sim link drops. When it comes back it
        // replays the position it last had, which bridges the outage so the monitor's own timeline
        // looks unbroken; then the true position arrives and the aircraft has genuinely moved on
        // through the gap. That transition spans an OUTAGE, so it carries no information about how
        // fast anything travelled - but it is measured as though it did, with twenty minutes of
        // corroboration standing behind it.
        var pipeline = new Pipeline(Stand);

        var atOutage = pipeline.Fly(Stand, seconds: 1200);
        pipeline.Reconnect(outageSeconds);
        pipeline.Hold(atOutage, staleSeconds);

        // Where the aircraft really got to while nobody was watching.
        var truth = North(atOutage, Pipeline.CruiseKt / 3600.0 * outageSeconds);
        pipeline.Fly(truth, seconds: 600);

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(40.0, 90)]
    [InlineData(95.0, 90)]
    [InlineData(5505.0, 180)]
    [InlineData(40.0, 300)]
    public void WrongOpeningFixCarryingOrdinaryNoise_ThenTheTruePosition_DoesNotVoidTheSector(
        double wrongByNm, double heldSeconds)
    {
        // Case 5, and it is case 1 and case 2 again with one detail changed: the bad reading is not
        // bit-identical every sample. A metre of wander is nothing - a tenth of a knot - but a
        // running SUM of per-sample hops is path length, not displacement, and path length grows
        // without bound while the aircraft goes nowhere at all. Ten seconds of it is enough to look
        // like an aircraft that has "covered ground", which spends the departure-correction
        // exemption before the brakes have come off.
        var pipeline = new Pipeline(Stand);
        var wrong = South(Stand, wrongByNm);

        pipeline.Wander(wrong, heldSeconds, metresPerSample: 1.0);
        pipeline.Hold(Stand, seconds: 30, onGround: true);
        pipeline.Fly(Stand, seconds: 600);

        // Guard: the wander really does add up to many times the ~93 m of movement the monitor asks
        // for before it believes an aircraft has gone somewhere, while displacing it by one metre.
        // Without this the theory could pass by being too gentle to reproduce anything.
        var pathLengthMetres = heldSeconds / SampleIntervalSeconds * 1.0;
        Assert.True(pathLengthMetres > 300,
            $"the wander only accumulates {pathLengthMetres:N0} m of path length, which proves nothing");

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(40.0, 90)]
    [InlineData(95.0, 90)]
    [InlineData(40.0, 300)]
    [InlineData(5505.0, 120)]
    public void GoodOpeningFixThenASustainedGlitchAwayAndBack_DoesNotVoidTheSector(
        double glitchAwayNm, double glitchHeldSeconds)
    {
        // Case 7 - the mirror of case 5, and the order is the whole difference. When the BAD reading
        // comes first it becomes the origin, so the hold displaces nothing and the guard holds. Put
        // the GOOD reading first and the origin is the stand, so a glitch reporting the aircraft
        // forty miles away is displaced from it by forty miles - even though the aircraft has not
        // moved an inch - and the guard reads that as an aircraft that has gone somewhere.
        //
        // What makes it plainly a bug rather than a limit: the glitch position was ARRIVED AT by an
        // implausible transition, so the monitor already knew the aircraft could not have flown
        // there. It was the hold afterwards, judged on its own plausible transitions, that spent the
        // exemption - by which time that knowledge had been thrown away.
        var pipeline = new Pipeline(Stand);

        pipeline.Hold(Stand, seconds: 30, onGround: true);
        pipeline.Hold(South(Stand, glitchAwayNm), glitchHeldSeconds, onGround: true);
        pipeline.Hold(Stand, seconds: 30, onGround: true);
        pipeline.Fly(Stand, seconds: 600);

        // Guards: the glitch is outside the departure radius (inside it, the exemption was never at
        // risk) and is held past the corroboration dwell (below it, nothing can be corroborated).
        Assert.True(glitchAwayNm > FlightIntegrityMonitor.DepartureCorrectionRadiusNm,
            "a glitch inside the departure radius cannot spend the exemption, so this would prove nothing");
        Assert.True(glitchHeldSeconds > FlightIntegrityMonitor.CorroborationDwell.TotalSeconds,
            "a glitch held for less than the dwell cannot corroborate anything, so this would prove nothing");

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(40.0, 90)]
    [InlineData(40.0, 300)]
    [InlineData(95.0, 90)]
    [InlineData(5505.0, 120)]
    public void GlitchAwayAndBackAfterTheAircraftHasSettledOnItsStand_DoesNotVoidTheSector(
        double glitchAwayNm, double glitchHeldSeconds)
    {
        // Case 8, and it is wider than case 7 rather than a variant of it. Case 7 needed the sim to
        // misbehave inside the first minute of tracking; this one needs the opposite - an aircraft
        // that has been sitting on its stand for a couple of minutes, which is the ordinary way a
        // sector begins.
        //
        // Nothing about the departure exemption is at fault here: the outbound transition lands
        // forty miles away, so of course it declines to excuse it. The fault is that the suspicion is
        // CONFIRMED while the glitch is still being reported - before any return could possibly be
        // observed - and the finding then latched for good. The aircraft does come back, which is
        // exactly the rule meant to recognise an excursion that was never real, but it comes back
        // ninety seconds too late to count.
        var pipeline = new Pipeline(Stand);

        pipeline.Hold(Stand, seconds: 120, onGround: true);
        pipeline.Hold(South(Stand, glitchAwayNm), glitchHeldSeconds, onGround: true);
        pipeline.Hold(Stand, seconds: 60, onGround: true);
        pipeline.Fly(Stand, seconds: 600);

        Assert.True(glitchHeldSeconds > FlightIntegrityMonitor.CorroborationDwell.TotalSeconds,
            "a glitch shorter than the dwell can never be confirmed, so this would prove nothing");

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void RepeatedGlitchAndCorrectCycles_DoNotVoidTheSector(int cycles)
    {
        // The nastier consequence of case 8: surviving one glitch is what puts a session over the
        // corroboration line for the next, so a sim that misbehaves twice could not stay on the safe
        // side however briefly each one lasted. "Glitch twice and lose the sector" is a harder thing
        // to explain to a pilot than any of the other shapes.
        var pipeline = new Pipeline(Stand);

        pipeline.Hold(Stand, seconds: 120, onGround: true);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            pipeline.Hold(South(Stand, 40.0), seconds: 90, onGround: true);
            pipeline.Hold(Stand, seconds: 90, onGround: true);
        }

        pipeline.Fly(Stand, seconds: 600);

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(2.0, 120)]
    [InlineData(8.0, 120)]
    [InlineData(40.0, 5)]
    [InlineData(40.0, 30)]
    [InlineData(95.0, 55)]
    public void GoodOpeningFixThenAGlitchTooSmallOrTooBriefToCorroborate_DoesNotVoidTheSector(
        double glitchAwayNm, double glitchHeldSeconds)
    {
        // The bounds of the case above, pinned in their own right: a glitch inside the departure
        // radius is harmless at any length, and a glitch of any size is harmless if it is over
        // before it can corroborate itself. These pay already - they are here so that a future
        // change cannot quietly move the boundary without a test noticing.
        var pipeline = new Pipeline(Stand);

        pipeline.Hold(Stand, seconds: 30, onGround: true);
        pipeline.Hold(South(Stand, glitchAwayNm), glitchHeldSeconds, onGround: true);
        pipeline.Hold(Stand, seconds: 30, onGround: true);
        pipeline.Fly(Stand, seconds: 600);

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(30, 90)]
    [InlineData(60, 90)]
    [InlineData(60, 150)]
    public void ReconnectFollowedByJitterThenAStaleFreeze_DoesNotVoidTheSector(
        double jitterSeconds, double staleSeconds)
    {
        // Case 6. The link comes back thrashing between absurd positions before it settles. Jitter
        // is nothing but implausible transitions - the opposite of evidence that a feed is healthy -
        // so it must not be allowed to satisfy "the feed has proved itself" and hand back the right
        // to rebuild dwell out of a freeze that follows.
        //
        // Only reachable past the acquisition gate's own timeout: jitter cannot corroborate itself,
        // so a shorter burst is withheld in its entirety and never reaches the monitor at all.
        var pipeline = new Pipeline(Stand);

        var atOutage = pipeline.Fly(Stand, seconds: 1200);
        pipeline.Reconnect(outageSeconds: 120);
        pipeline.Jitter((0.0, 90.0), (0.0, -90.0), jitterSeconds);
        pipeline.Hold(atOutage, staleSeconds);

        var truth = North(atOutage, Pipeline.CruiseKt / 3600.0 * 120);
        pipeline.Fly(truth, seconds: 600);

        Assert.True(jitterSeconds > PositionAcquisitionGate.AcquisitionTimeout.TotalSeconds,
            "a burst shorter than the gate's timeout never reaches the monitor, so this would prove nothing");

        AssertFlownHonestly(pipeline);
    }

    [Theory]
    [InlineData(1.0, 64.0, 64.0, 640.0, 0.2)]
    [InlineData(1.0, 128.0, 128.0, 640.0, 0.2)]
    [InlineData(1.0, 128.0, 128.0, 700.0, 0.2)]
    [InlineData(128.0, 1.0, 128.0, 640.0, 0.2)]
    [InlineData(1.0, 2.0, 2.0, 460.0, 0.2)]
    // The same steps on a sim running at 20 and 10 fps rather than 30. Nothing about the arithmetic
    // changes - implied speed does not depend on the interval - but the ground the straddling
    // transition covers does, which takes it clear of the negligible-jump floor. Without these, this
    // theory would be silently proving the FLOOR rather than the normalisation.
    [InlineData(1.0, 128.0, 128.0, 640.0, 0.3)]
    [InlineData(1.0, 64.0, 64.0, 640.0, 0.6)]
    [InlineData(128.0, 1.0, 128.0, 640.0, 0.3)]
    public void LargeSimulationRateStepAtHighGroundSpeed_DoesNotVoidTheSector(
        double fromRate, double toRate, double rateGroundWasFlownAt, double groundSpeedKt, double intervalSeconds)
    {
        // Case 4, and nothing has to malfunction for it: a player binds "set rate", goes straight
        // from 1x to 64x or 128x, and the aircraft is quick over the ground. Exactly one transition
        // straddles the two rates and the ground it covers was flown at ONE of them, so combining
        // them by average under-divides that sample to about twice ground speed - past a 1,200 kt
        // threshold for anything over roughly 600 kt, which is an airliner in a jet stream.
        //
        // Both directions are here on purpose. Normalising by the ARRIVING sample's rate would cure
        // the step up and rebuild the identical fault on the step down, where the ground was flown
        // fast and the arriving rate is 1. Only taking the greater of the two is safe both ways.
        var pipeline = new Pipeline(Stand)
        {
            GroundSpeedKt = groundSpeedKt,
            SimulationRate = fromRate,
            IntervalSeconds = intervalSeconds,
        };

        pipeline.Hold(Stand, seconds: 30, onGround: true);
        pipeline.Continue(600);
        var straddlingJumpNm = pipeline.StepRateTo(toRate, rateGroundWasFlownAt);
        pipeline.Continue(600);

        // Guard, so a passing test cannot mean "the scenario never happened": the transition really
        // does straddle a rate change big enough to matter, and it really would have been condemned
        // by averaging the two rates.
        Assert.Equal(Math.Max(fromRate, toRate), pipeline.Monitor.MaxSimulationRateObserved);
        var averagedImpliedKt = straddlingJumpNm / (intervalSeconds / 3600.0) / ((fromRate + toRate) / 2.0);
        Assert.True(
            averagedImpliedKt > FlightIntegrityMonitor.ImpossibleGroundSpeedKt || Math.Max(fromRate, toRate) <= 2.0,
            $"this case proves nothing: averaging the rates implies only {averagedImpliedKt:N0} kt, which was never over the threshold");

        Assert.False(pipeline.Monitor.PositionJumpDetected,
            $"a {fromRate}x -> {toRate}x rate change at {groundSpeedKt} kt over the ground voided the sector");
        Assert.False(pipeline.Monitor.SectorInvalidForPayment);
    }

    // ---------------------------------------------------------------------------------------
    // The other side of the line. Nothing above may be bought at the price of anything below.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TeleportFollowedByParkingAtTheFarEnd_IsStillCaught()
    {
        // The case that rules out the obvious alternative fix. Deferring confirmation until an
        // excursion is "genuinely left behind" would pay this: a cheat who teleports to the
        // destination and shuts down never leaves anything behind. Revocation on RETURN has no such
        // hole - parking at the far end is the opposite of coming back.
        var pipeline = new Pipeline(Stand);

        var beforeJump = pipeline.Fly(Stand, seconds: 600);
        pipeline.Hold(North(beforeJump, 600), seconds: 600);

        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void TeleportThenFlyingOnWithoutEverReturning_StaysCaughtForTheWholeFlight()
    {
        // Revocation must not decay into "wait long enough and it clears". The flag may only come
        // off by the aircraft coming home, so a teleport followed by hours of onward flight stays
        // flagged for every one of them.
        var pipeline = new Pipeline(Stand);

        var beforeJump = pipeline.Fly(Stand, seconds: 600);
        var afterJump = North(beforeJump, 600);
        pipeline.Fly(afterJump, seconds: 600);
        Assert.True(pipeline.Monitor.PositionJumpDetected);

        pipeline.Fly(North(afterJump, 75), seconds: 3600);

        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void SlewIsNeverRevokedByComingHome()
    {
        // Slew is one-way, full stop, and revocation must not be able to reach it: any slewing
        // invalidates the sector however the flight ends. Here the aircraft slews away and then
        // comes all the way back to where it started, which is exactly what revokes a position
        // jump - and must do nothing whatever to the slew finding.
        var pipeline = new Pipeline(Stand);

        pipeline.Fly(Stand, seconds: 600);
        pipeline.Sample(North(Stand, 600), slew: true);
        pipeline.Fly(North(Stand, 600), seconds: 120);
        pipeline.Sample(Stand);
        pipeline.Fly(Stand, seconds: 600);

        Assert.True(pipeline.Monitor.SlewDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void JumpToTheDepartureAirportOnceAirborne_IsStillCaught()
    {
        // The departure-correction rule is strictly one-way. It must be spent by GETTING AIRBORNE,
        // not only by flying beyond the departure radius - because landing back where you departed
        // is a legitimate, paid outcome, so an exemption still live in the air would pay a cheat for
        // teleporting home. Here the aircraft never leaves the departure radius at all, so only the
        // airborne half of the latch can catch it.
        var pipeline = new Pipeline(Stand);

        pipeline.Hold(Stand, seconds: 30, onGround: true);
        var afterCircuit = pipeline.Circuit(Stand, cycles: 12, legNm: 2.0);

        var jumpTo = North(Stand, 9.0);
        pipeline.Sample(jumpTo);
        pipeline.Fly(jumpTo, seconds: 600);

        // Guards, so this can only pass for the reason it is about: the aircraft never went further
        // than the departure radius, so the DISTANCE half of the latch cannot have fired; and the
        // jump both lands inside that radius and is far enough to clear the negligible-jump floor.
        var strayedNm = GreatCircle.DistanceNm(Stand.Lat, Stand.Lon, afterCircuit.Lat, afterCircuit.Lon);
        var jumpNm = GreatCircle.DistanceNm(afterCircuit.Lat, afterCircuit.Lon, jumpTo.Lat, jumpTo.Lon);
        Assert.True(strayedNm < FlightIntegrityMonitor.DepartureCorrectionRadiusNm,
            $"the circuit strayed {strayedNm:F1} nm, outside the departure radius, so this proves nothing about the airborne latch");
        Assert.True(GreatCircle.DistanceNm(Stand.Lat, Stand.Lon, jumpTo.Lat, jumpTo.Lon) < FlightIntegrityMonitor.DepartureCorrectionRadiusNm,
            "the jump has to LAND inside the departure radius, or the exemption was never in play");
        Assert.True(jumpNm > FlightIntegrityMonitor.NegligibleJumpDistanceNm,
            $"the jump has to clear the negligible-jump floor; it was {jumpNm:F1} nm");

        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void TeleportInTheMiddleOfARealFlight_IsStillCaught()
    {
        var pipeline = new Pipeline(Stand);

        var beforeJump = pipeline.Fly(Stand, seconds: 600);
        pipeline.Fly(North(beforeJump, 600), seconds: 600);

        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void RepositionDeliveredAsARunOfIndividuallySmallHops_IsStillCaught()
    {
        // The obvious way to probe a minimum jump distance: stay under it on every single step.
        // A reposition is one excursion however many packets it is spread over, so it is measured
        // by where it ended up, not by its largest single hop.
        var pipeline = new Pipeline(Stand);

        var position = pipeline.Fly(Stand, seconds: 600);
        for (var hop = 0; hop < 60; hop++)
        {
            position = North(position, 4.0);
            pipeline.Sample(position);
        }

        pipeline.Fly(position, seconds: 600);

        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void RepositionWhoseHopsAreSeparatedByOrdinaryLookingFrames_IsStillCaught()
    {
        // The same probe again, but hiding each hop behind a frame that looks like normal flight, on
        // the theory that a rule which only measures an UNBROKEN run of bad transitions can be
        // walked straight through. An excursion is measured by how far the aircraft was moved, not
        // by how tidily the moves were delivered.
        var pipeline = new Pipeline(Stand);

        var position = pipeline.Fly(Stand, seconds: 600);
        for (var hop = 0; hop < 60; hop++)
        {
            position = North(position, 4.0);
            pipeline.Sample(position);
            pipeline.Sample(North(position, Pipeline.CruiseKt / 3600.0 * 0.2));
            position = North(position, Pipeline.CruiseKt / 3600.0 * 0.2);
        }

        pipeline.Fly(position, seconds: 600);

        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void JumpBackToTheDepartureAirportAfterTheAircraftHasLeftIt_IsStillCaught()
    {
        // The departure-correction rule is spent the moment the aircraft is seen to fly away from
        // its departure airport, so it can never be reached for again later in the sector.
        var pipeline = new Pipeline(Stand);

        var away = pipeline.Fly(North(Stand, 400), seconds: 600);
        pipeline.Fly(Stand, seconds: 600);

        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
        Assert.True(GreatCircle.DistanceNm(Stand.Lat, Stand.Lon, away.Lat, away.Lon) > 400,
            "the aircraft has to have genuinely left the departure area for this to prove anything");
    }

    [Fact]
    public void TeleportAfterAReconnectHasBeenFlownThrough_IsStillCaught()
    {
        // A reconnect resets what the monitor can measure, but it is not a laundry: fly on
        // normally afterwards and the sector is corroborated again, teleport included.
        var pipeline = new Pipeline(Stand);

        var atOutage = pipeline.Fly(Stand, seconds: 600);
        pipeline.Reconnect(outageSeconds: 60);
        var resumed = pipeline.Fly(atOutage, seconds: 600);
        pipeline.Fly(North(resumed, 600), seconds: 600);

        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void TeleportUnderTimeAcceleration_IsStillCaught()
    {
        var pipeline = new Pipeline(Stand) { SimulationRate = 4.0 };

        var beforeJump = pipeline.Fly(Stand, seconds: 600);
        pipeline.Fly(North(beforeJump, 2000), seconds: 600);

        Assert.True(pipeline.Monitor.ElevatedSimRateDetected);
        Assert.True(pipeline.Monitor.PositionJumpDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    [Fact]
    public void SlewSimvar_StillVoidsTheSectorOnItsOwn()
    {
        var pipeline = new Pipeline(Stand);

        pipeline.Fly(Stand, seconds: 30);
        pipeline.Sample(Stand, slew: true);

        Assert.True(pipeline.Monitor.SlewDetected);
        Assert.True(pipeline.Monitor.SectorInvalidForPayment);
    }

    private static void AssertFlownHonestly(Pipeline pipeline)
    {
        Assert.False(pipeline.Monitor.SlewDetected);
        Assert.False(pipeline.Monitor.ElevatedSimRateDetected);
        Assert.False(pipeline.Monitor.PositionJumpDetected);
        Assert.False(pipeline.Monitor.SectorInvalidForPayment);
    }

    /// <summary>
    /// The app's real telemetry path in miniature: every sample goes through the acquisition gate
    /// first and only reaches the integrity monitor if the gate hands it on, which is exactly how
    /// <c>SimTelemetryService</c> feeds <c>FlightLifecycleService</c>. Testing the monitor on its own
    /// would miss anything the gate lets through - and a stationary bad fix is precisely that, since
    /// two identical packets agree with each other.
    /// </summary>
    private sealed class Pipeline
    {
        public const double CruiseKt = 450.0;

        private PositionAcquisitionGate _gate = new();
        private double _elapsedSeconds;
        private (double Lat, double Lon) _lastPosition;

        public Pipeline((double Lat, double Lon)? expectedStart = null)
        {
            Monitor = new FlightIntegrityMonitor(expectedStart);
        }

        public FlightIntegrityMonitor Monitor { get; }

        public double SimulationRate { get; set; } = 1.0;

        public double GroundSpeedKt { get; init; } = CruiseKt;

        /// <summary>How far apart samples arrive. The sim source asks for every 6th SIM FRAME, so
        /// the familiar "roughly 5 Hz" is really "roughly 30 fps"; a loaded sim at 20 or 10 fps
        /// delivers every 0.3 or 0.6 s instead, and the same rate change then covers proportionally
        /// more ground on the transition that straddles it.</summary>
        public double IntervalSeconds { get; init; } = SampleIntervalSeconds;

        public void Sample((double Lat, double Lon) position, bool slew = false, bool onGround = false)
        {
            var utc = Base + TimeSpan.FromSeconds(_elapsedSeconds);
            if (_gate.Accept(position.Lat, position.Lon, utc, SimulationRate))
            {
                Monitor.Observe(new FlightTelemetrySample(
                    utc, position.Lat, position.Lon, onGround ? 0 : 30000, onGround ? 0 : 30000,
                    onGround ? 0 : 280, onGround ? 0 : GroundSpeedKt, 0, 0, 0,
                    onGround, true, false, 1.0, 0, 5000, "Test Aircraft", "TEST", "Test", SimulationRate, slew));
            }

            _lastPosition = position;
            _elapsedSeconds += IntervalSeconds;
        }

        /// <summary>Reports the same position over and over, which is what both a parked aircraft and
        /// a frozen feed look like.</summary>
        public void Hold((double Lat, double Lon) position, double seconds, bool onGround = false)
        {
            for (var i = 0; i < SampleCount(seconds); i++)
            {
                Sample(position, onGround: onGround);
            }
        }

        /// <summary>
        /// A position that is not going anywhere, but is not bit-identical either: it alternates
        /// between two points <paramref name="metresPerSample"/> apart. Every transition is
        /// perfectly plausible (a metre at 5 Hz is a tenth of a knot), the aircraft's DISPLACEMENT
        /// stays about a metre for as long as this runs, and its PATH LENGTH grows without bound.
        /// A real reading carrying ordinary noise looks like this; nothing says a garbage one is
        /// noise-free.
        /// </summary>
        public void Wander((double Lat, double Lon) position, double seconds, double metresPerSample)
        {
            var offsetDegrees = metresPerSample / 1852.0 / 60.0;

            for (var i = 0; i < SampleCount(seconds); i++)
            {
                Sample(i % 2 == 0 ? position : (position.Lat + offsetDegrees, position.Lon));
            }
        }

        /// <summary>Thrashing between two absurd positions - readings nothing can vouch for, and
        /// which cannot vouch for each other either, so every transition through this is
        /// implausible.</summary>
        public void Jitter((double Lat, double Lon) a, (double Lat, double Lon) b, double seconds)
        {
            for (var i = 0; i < SampleCount(seconds); i++)
            {
                Sample(i % 2 == 0 ? a : b);
            }
        }

        /// <summary>Ordinary tracked flight due north, starting AT <paramref name="from"/> - so the
        /// step onto that first sample is whatever the caller has arranged, which is how the teleport
        /// tests splice a jump in. Returns where the NEXT sample would go.</summary>
        public (double Lat, double Lon) Fly((double Lat, double Lon) from, double seconds)
        {
            var samples = SampleCount(seconds);

            for (var i = 0; i < samples; i++)
            {
                Sample((from.Lat + DegreesPerSample() * i, from.Lon));
            }

            return (from.Lat + DegreesPerSample() * samples, from.Lon);
        }

        /// <summary>Flies on from wherever the last sample actually was, at the CURRENT simulation
        /// rate - so a rate changed just before this call produces exactly the one transition that
        /// straddles two rates, with the arriving sample carrying the new rate and a full new-rate
        /// step of ground.</summary>
        public (double Lat, double Lon) Continue(double seconds)
        {
            for (var i = 0; i < SampleCount(seconds); i++)
            {
                Sample((_lastPosition.Lat + DegreesPerSample(), _lastPosition.Lon));
            }

            return _lastPosition;
        }

        /// <summary>
        /// The single transition on which the player changes simulation rate. The arriving sample
        /// reports <paramref name="newRate"/> - rate and position are fields of one SimConnect struct
        /// and cannot lag each other by a frame - while the ground it covers was flown at
        /// <paramref name="rateGroundWasFlownAt"/>, which is the whole difficulty: that is the old
        /// rate when the change lands at the end of the interval and the new one when it lands at
        /// the start, and the monitor cannot tell which.
        /// </summary>
        /// <returns>How far that one transition covered, in nautical miles, so a test can assert
        /// what it was actually judging.</returns>
        public double StepRateTo(double newRate, double rateGroundWasFlownAt)
        {
            var degrees = GroundSpeedKt / 3600.0 * IntervalSeconds * rateGroundWasFlownAt / 60.0;
            SimulationRate = newRate;
            Sample((_lastPosition.Lat + degrees, _lastPosition.Lon));
            return degrees * 60.0;
        }

        /// <summary>Whole out-and-back legs from <paramref name="from"/>, airborne throughout, never
        /// straying further than <paramref name="legNm"/> from where it started and ending exactly
        /// back there - a circuit, which is a real thing pilots fly and the only shape that gets an
        /// aircraft airborne without taking it out of its departure area.</summary>
        public (double Lat, double Lon) Circuit((double Lat, double Lon) from, int cycles, double legNm)
        {
            var samplesPerLeg = (int)Math.Round(legNm / (GroundSpeedKt / 3600.0 * IntervalSeconds));
            var degreesPerSample = DegreesPerSample();

            for (var cycle = 0; cycle < cycles; cycle++)
            {
                for (var i = 1; i <= samplesPerLeg; i++)
                {
                    Sample((from.Lat + degreesPerSample * i, from.Lon));
                }

                for (var i = samplesPerLeg - 1; i >= 0; i--)
                {
                    Sample((from.Lat + degreesPerSample * i, from.Lon));
                }
            }

            return _lastPosition;
        }

        /// <summary>The sim link drops and comes back. Mirrors what the running app does: the
        /// acquisition gate is rebuilt from scratch, and the integrity monitor is told that its
        /// timeline has a hole in it.</summary>
        public void Reconnect(double outageSeconds)
        {
            _elapsedSeconds += outageSeconds;
            _gate = new PositionAcquisitionGate();
            Monitor.NotifyTelemetryInterrupted();
        }

        private double DegreesPerSample() => GroundSpeedKt / 3600.0 * IntervalSeconds * SimulationRate / 60.0;

        private int SampleCount(double seconds) => (int)Math.Round(seconds / IntervalSeconds);
    }
}


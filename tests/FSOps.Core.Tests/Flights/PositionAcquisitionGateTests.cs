using System.Text.Json;
using FSOps.Core.Flights;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Flights;

/// <summary>
/// The acquisition gate - the source-level half of the first-fix defect. The monitor-level
/// corroboration rules protect the money path once a bad position is already in circulation; this
/// stops it circulating at all. Both exist on purpose and neither is sufficient alone.
/// </summary>
public class PositionAcquisitionGateTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Base = new(2026, 8, 12, 16, 24, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => Base + TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Accept_BadOpeningFixThenRealOnes_WithholdsUntilTwoFixesAgree()
    {
        var gate = new PositionAcquisitionGate();

        // The uninitialised fix. Nothing has vouched for it, so it goes no further.
        Assert.False(gate.Accept(0.0, 90.0, At(0), 1.0));

        // The aircraft's real position at the stand. It disagrees violently with the fix before it,
        // and at this instant there is still no way to know WHICH of the two is the bad one - so it
        // is held back as well rather than guessed about.
        Assert.False(gate.Accept(51.38526, -2.71770, At(0.2), 1.0));

        // A second real fix agreeing with the first. Now the aircraft's position is corroborated.
        Assert.True(gate.Accept(51.38527, -2.71759, At(0.4), 1.0));
        Assert.True(gate.Acquired);
        Assert.False(gate.AcquiredByTimeout);
        Assert.Equal(2, gate.WithheldSampleCount);
    }

    [Fact]
    public void Accept_OrdinaryStartWithNoBadFix_WithholdsOnlyTheVeryFirstSample()
    {
        var gate = new PositionAcquisitionGate();

        Assert.False(gate.Accept(51.38526, -2.71770, At(0), 1.0));
        Assert.True(gate.Accept(51.38527, -2.71759, At(0.2), 1.0));

        // The cost on a healthy connection is exactly one sample - 200 ms at the sim's normal rate.
        Assert.Equal(1, gate.WithheldSampleCount);
        Assert.False(gate.AcquiredByTimeout);
    }

    [Fact]
    public void Accept_OnceAcquired_NeverWithholdsAgainEvenOnAnImpossibleTransition()
    {
        // The guarantee that makes this safe to ship the night before a flight: the gate governs
        // ACQUISITION only. After that, the sample stream reaching the phase machine, OOOI capture
        // and touchdown detection is byte-for-byte what it was before this existed.
        var gate = new PositionAcquisitionGate();
        gate.Accept(51.38526, -2.71770, At(0), 1.0);
        Assert.True(gate.Accept(51.38527, -2.71759, At(0.2), 1.0));

        Assert.True(gate.Accept(0.0, 90.0, At(0.4), 1.0));
        Assert.True(gate.Accept(41.3, 2.1, At(0.6), 1.0));

        // Only the very first sample was ever withheld - nothing after acquisition is touched.
        Assert.Equal(1, gate.WithheldSampleCount);
    }

    [Fact]
    public void Accept_SimNeverReportsACorroboratedPosition_FailsOpenRatherThanBlockingForever()
    {
        // The safety valve. A permanent blackout - no fuel, no altitude, no phase tracking - would be
        // a far worse failure than the bad position this exists to stop, so patience is bounded.
        var gate = new PositionAcquisitionGate();

        var t = 0.0;
        var accepted = false;
        for (var i = 0; i < 200 && !accepted; i++, t += 0.2)
        {
            // Every fix disagrees with the one before it: nothing ever corroborates anything.
            accepted = gate.Accept(i % 2 == 0 ? 0.0 : 51.4, i % 2 == 0 ? 90.0 : -2.7, At(t), 1.0);
        }

        Assert.True(accepted);
        Assert.True(gate.Acquired);
        Assert.True(gate.AcquiredByTimeout);
        Assert.True(t >= PositionAcquisitionGate.AcquisitionTimeout.TotalSeconds,
            "the gate must not give up before its stated timeout");
    }

    [Fact]
    public void Accept_ElevatedSimRate_DoesNotMistakeAcceleratedCruiseForAnUncorroboratedFix()
    {
        // 460 kt at 4x covers four seconds of ground per wall-clock second. That is ordinary
        // accelerated flight and must corroborate normally, exactly as in the integrity monitor.
        var gate = new PositionAcquisitionGate();
        const double degreesPerSecondAt4x = 460.0 / 3600.0 * 4.0 / 60.0;

        Assert.False(gate.Accept(51.0, -2.0, At(0), 4.0));
        Assert.True(gate.Accept(51.0 + degreesPerSecondAt4x, -2.0, At(1), 4.0));
        Assert.False(gate.AcquiredByTimeout);
    }

    /// <summary>
    /// The real thing: the recorded telemetry of the flight that exposed the defect. The gate must
    /// withhold the Bay of Bengal fix, and the first position it ever hands out must be Bristol.
    /// </summary>
    [Fact]
    public void Accept_RealEggdToEgphFlight_NeverHandsOutTheBadOpeningFix()
    {
        var fixture = LoadRecordedFlight();
        var gate = new PositionAcquisitionGate();

        var handedOut = new List<(double Lat, double Lon, DateTimeOffset Utc)>();
        foreach (var s in fixture.Snapshots)
        {
            if (gate.Accept(s.Lat, s.Lon, s.Utc, 1.0))
            {
                handedOut.Add((s.Lat, s.Lon, s.Utc));
            }
        }

        Assert.True(gate.Acquired);
        Assert.False(gate.AcquiredByTimeout);

        // Nothing anywhere near 0N 90E was ever passed on.
        foreach (var position in handedOut)
        {
            var fromBristolNm = GreatCircle.DistanceNm(51.38526, -2.71770, position.Lat, position.Lon);
            Assert.True(fromBristolNm < 400,
                $"a position {fromBristolNm:N0} nm from Bristol was handed out at {position.Utc:HH:mm:ss}");
        }

        // The first position the app is ever told about is the aircraft on its stand at Bristol.
        var first = handedOut[0];
        Assert.True(GreatCircle.DistanceNm(51.38526, -2.71770, first.Lat, first.Lon) < 1.0);

        // Only the opening pair is lost. On the live 5 Hz stream that is 0.4 s; this fixture is the
        // persisted 15-second decimation, so here it reads as the first two snapshots.
        Assert.Equal(2, gate.WithheldSampleCount);
        Assert.Equal(fixture.Snapshots.Count - 2, handedOut.Count);
    }

    /// <summary>Bristol, where the sectors below actually departed from.</summary>
    private static readonly (double Lat, double Lon) Eggd = (51.38526, -2.71770);

    /// <summary>
    /// The sim's uninitialised position, to the digit, as recorded in the player's own FlightEvent
    /// rows on both 2026-08-12 and 2026-08-13. 5,505 nm from the stand at Bristol.
    /// </summary>
    private const double UninitialisedLat = -2.1556893808986427E-07;
    private const double UninitialisedLon = 90.00032277330374;

    /// <summary>
    /// The second real flight, and the one the 08-12 fixture could not have predicted. Its opening
    /// PositionSnapshot rows, exactly as read out of the player's database:
    /// <code>
    /// 17:16:38  lat -2.1556893808986427E-07  lon 90.00032277330374  gsKt 0  Preflight
    /// 17:16:53  lat -2.1556893808986427E-07  lon 90.00032277330374  gsKt 0  Preflight
    /// 17:17:08  lat  51.38534252774989       lon -2.7070546666672604 gsKt 0  Preflight
    /// </code>
    /// The bad fix REPEATED, byte-identical. Under the old rule the second instance corroborated the
    /// first at zero knots and the gate acquired on the junk. Only the opening rows are reproduced
    /// here rather than a whole-flight fixture: those three rows are the entire fault, and the rest
    /// of that sector's track could not be read back (the player's app was not running), so
    /// inventing the remainder would be putting made-up numbers behind a real provenance note.
    /// </summary>
    [Fact]
    public void Accept_RealEggdToEgph20260813_AStuckBadFixNoLongerVouchesForItself()
    {
        var gate = new PositionAcquisitionGate(Eggd);

        Assert.False(gate.Accept(UninitialisedLat, UninitialisedLon, At(0), 1.0));

        // The moment the old rule got this wrong: a byte-identical repeat, agreeing perfectly with
        // the fix before it at zero knots. Agreement between two readings that are both 5,505 nm
        // from the stand is not evidence of anything.
        Assert.False(gate.Accept(UninitialisedLat, UninitialisedLon, At(15), 1.0));

        // The aircraft's real position. Believable on its own - no second opinion needed.
        Assert.True(gate.Accept(51.38534252774989, -2.7070546666672604, At(30), 1.0));
        Assert.True(gate.Acquired);
        Assert.False(gate.AcquiredByTimeout);
        Assert.Equal(2, gate.WithheldSampleCount);
    }

    [Fact]
    public void Accept_TheStuckFixRepeatingForMinutes_IsWithheldForTheWholeAcquisitionWindow()
    {
        // The 08-13 fault, run long. Under the old rule the gate acquired on sample two and every
        // one of these would have been handed out.
        var gate = new PositionAcquisitionGate(Eggd);

        for (var second = 0; second < 55; second += 5)
        {
            Assert.False(gate.Accept(UninitialisedLat, UninitialisedLon, At(second), 1.0));
        }

        Assert.False(gate.Acquired);
        Assert.Equal(11, gate.WithheldSampleCount);
    }

    [Fact]
    public void Accept_ColdAndDarkOnStand_AcquiresImmediately_BecauseNotMovingIsNotSuspicious()
    {
        // The case that rules out "demand movement before believing a position". An aircraft parked
        // cold and dark reports the same coordinates forever, and they are correct. This must not
        // cost it a single sample.
        var gate = new PositionAcquisitionGate(Eggd);

        Assert.True(gate.Accept(Eggd.Lat, Eggd.Lon, At(0), 1.0));
        Assert.True(gate.Accept(Eggd.Lat, Eggd.Lon, At(0.2), 1.0));
        Assert.True(gate.Accept(Eggd.Lat, Eggd.Lon, At(600), 1.0));

        Assert.True(gate.Acquired);
        Assert.False(gate.AcquiredByTimeout);
        Assert.Equal(0, gate.WithheldSampleCount);
    }

    [Fact]
    public void Accept_AnchoredStartWithinTheTolerance_IsBelievedWithoutASecondOpinion()
    {
        // Ninety miles out is still inside StartingFixToleranceNm. The tolerance answers "is this a
        // position at all?", not "is the aircraft on the right stand", and it is generous on purpose.
        var gate = new PositionAcquisitionGate(Eggd);

        Assert.True(gate.Accept(52.85, -2.71770, At(0), 1.0));
        Assert.Equal(0, gate.WithheldSampleCount);
    }

    [Fact]
    public void Accept_AnchoredAndTheSimNeverReportsACredibleFix_FailsOpenAfterTheLongerWindow()
    {
        // The legitimate case this must not break: a player who really did start tracking a long way
        // from the route's departure airport. Their telemetry is delayed, never denied - and the
        // result is flagged as uncorroborated so nothing downstream reads it as vouched for.
        var gate = new PositionAcquisitionGate(Eggd);

        Assert.False(gate.Accept(41.30, 2.10, At(0), 1.0));
        Assert.False(gate.Accept(41.31, 2.11, At(30), 1.0));
        Assert.False(gate.Accept(41.32, 2.12, At(59), 1.0));

        Assert.True(gate.Accept(41.33, 2.13, At(60), 1.0));
        Assert.True(gate.Acquired);
        Assert.True(gate.AcquiredByTimeout);
    }

    [Fact]
    public void Accept_AnchoredGate_StandsAsidePermanentlyOnceAcquired()
    {
        // Same guarantee as the unanchored path: this governs ACQUISITION only. Bad data arriving
        // mid-flight is FlightIntegrityMonitor's business, and a gate that kept intervening would
        // quietly suppress the evidence a sector's payment turns on.
        var gate = new PositionAcquisitionGate(Eggd);

        Assert.True(gate.Accept(Eggd.Lat, Eggd.Lon, At(0), 1.0));
        Assert.True(gate.Accept(UninitialisedLat, UninitialisedLon, At(0.2), 1.0));
        Assert.True(gate.Accept(41.3, 2.1, At(0.4), 1.0));
    }

    /// <summary>
    /// The limit of the unanchored rule, pinned deliberately rather than left to be rediscovered.
    /// With nothing to judge a position against, two identical bad fixes still corroborate each
    /// other - and this is the exact sequence that got through on 2026-08-13. Nothing available at
    /// this layer can separate "parked" from "stuck" without an anchor, and a rule that tried would
    /// break the parked case, which is the common one.
    /// <para>
    /// So this is not a defect left unfixed; it is the reason the expected position is now plumbed
    /// through from the flight being tracked. If this test ever starts failing because someone
    /// tightened the unanchored path, check what it did to a cold and dark aircraft on stand first.
    /// </para>
    /// </summary>
    [Fact]
    public void Accept_WithNoAnchor_AStuckFixStillVouchesForItself_WhichIsWhyTheAnchorExists()
    {
        var gate = new PositionAcquisitionGate();

        Assert.False(gate.Accept(UninitialisedLat, UninitialisedLon, At(0), 1.0));
        Assert.True(gate.Accept(UninitialisedLat, UninitialisedLon, At(15), 1.0));

        // Whereas the same sequence, anchored, is refused - see the test above.
        var anchored = new PositionAcquisitionGate(Eggd);
        Assert.False(anchored.Accept(UninitialisedLat, UninitialisedLon, At(0), 1.0));
        Assert.False(anchored.Accept(UninitialisedLat, UninitialisedLon, At(15), 1.0));
    }

    [Fact]
    public void IsAnchored_SaysWhichOfTheTwoRulesIsInForce()
    {
        // The two guarantees are not equivalent, and callers that log or report on acquisition need
        // to be able to tell them apart - see the class doc.
        Assert.True(new PositionAcquisitionGate(Eggd).IsAnchored);
        Assert.False(new PositionAcquisitionGate().IsAnchored);
    }

    private static RecordedFlight LoadRecordedFlight()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Flights", "Fixtures", "eggd-egph-20260812-snapshots.json");
        return JsonSerializer.Deserialize<RecordedFlight>(File.ReadAllText(path), JsonOptions)!;
    }

    private sealed record RecordedFlight(string Source, IReadOnlyList<RecordedSnapshot> Snapshots);

    private sealed record RecordedSnapshot(
        DateTimeOffset Utc, double Lat, double Lon, double AltAglFt, double GsKt, double VsFpm, string Phase);
}

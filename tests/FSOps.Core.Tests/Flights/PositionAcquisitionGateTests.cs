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

    private static RecordedFlight LoadRecordedFlight()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Flights", "Fixtures", "eggd-egph-20260812-snapshots.json");
        return JsonSerializer.Deserialize<RecordedFlight>(File.ReadAllText(path), JsonOptions)!;
    }

    private sealed record RecordedFlight(string Source, IReadOnlyList<RecordedSnapshot> Snapshots);

    private sealed record RecordedSnapshot(
        DateTimeOffset Utc, double Lat, double Lon, double AltAglFt, double GsKt, double VsFpm, string Phase);
}

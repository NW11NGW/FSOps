using System.Text.Json;
using FSOps.Sim;
using FSOps.Sim.Fake;

namespace FSOps.Core.Tests.Sim;

public class FakeFlightInterpolatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static FakeFlightScript LoadScript()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fake", "Replays", "egkk-lebl.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FakeFlightScript>(json, JsonOptions)!;
    }

    [Fact]
    public void Sample_AtTimeZero_MatchesFirstKeyframeExactly()
    {
        var script = LoadScript();
        var first = script.Keyframes[0];

        var sample = FakeFlightInterpolator.Sample(script, 0);

        Assert.Equal(first.LatitudeDeg, sample.LatitudeDeg);
        Assert.Equal(first.LongitudeDeg, sample.LongitudeDeg);
        Assert.True(sample.OnGround);
        Assert.False(sample.EngineRunning);
        Assert.True(sample.ParkingBrakeSet);
        Assert.Equal("Airbus A320neo Asobo", sample.AircraftTitle);
        Assert.Equal("A20N", sample.AtcModel);
        Assert.Equal("Airbus", sample.AtcType);
    }

    [Fact]
    public void Sample_BeforeFirstKeyframe_ClampsToFirstKeyframe()
    {
        var script = LoadScript();

        var sample = FakeFlightInterpolator.Sample(script, -50);

        Assert.Equal(script.Keyframes[0].LatitudeDeg, sample.LatitudeDeg);
    }

    [Fact]
    public void Sample_AfterLastKeyframe_ClampsToLastKeyframe()
    {
        var script = LoadScript();
        var last = script.Keyframes[^1];

        var sample = FakeFlightInterpolator.Sample(script, last.TSeconds + 500);

        Assert.Equal(last.LatitudeDeg, sample.LatitudeDeg);
        Assert.True(sample.OnGround);
        Assert.False(sample.EngineRunning);
    }

    [Fact]
    public void Sample_Midway_BetweenTwoKeyframes_LinearlyInterpolatesNumericFields()
    {
        var script = LoadScript();
        // Keyframes at 420s (climb, 2000ft AGL) and 600s (climb, 10000ft AGL): midpoint 510s
        // should sit at the arithmetic mean altitude.
        var from = script.Keyframes.Single(k => k.TSeconds == 420);
        var to = script.Keyframes.Single(k => k.TSeconds == 600);

        var sample = FakeFlightInterpolator.Sample(script, 510);

        var expectedAltitude = (from.AltitudeAglFt + to.AltitudeAglFt) / 2;
        Assert.Equal(expectedAltitude, sample.AltitudeAglFt, precision: 3);
    }

    [Fact]
    public void Sample_TouchesGroundAtStartAndEnd_AndBecomesAirborneInBetween()
    {
        var script = LoadScript();
        var duration = script.Keyframes[^1].TSeconds;

        var atStart = FakeFlightInterpolator.Sample(script, 0);
        var atEnd = FakeFlightInterpolator.Sample(script, duration);
        var midCruise = FakeFlightInterpolator.Sample(script, duration / 2);

        Assert.True(atStart.OnGround);
        Assert.True(atEnd.OnGround);
        Assert.False(midCruise.OnGround);
    }

    [Fact]
    public void Sample_AcrossFullDuration_ReachesCruiseAltitudeAndCapturesTouchdownSpike()
    {
        var script = LoadScript();
        var duration = script.Keyframes[^1].TSeconds;

        double maxAltitude = 0;
        double maxTouchdownVelocity = 0;
        var sawOnGround = false;
        var sawAirborne = false;
        var sawEngineOn = false;
        var sawEngineOff = false;

        for (double t = 0; t <= duration; t += 5)
        {
            var sample = FakeFlightInterpolator.Sample(script, t);
            maxAltitude = Math.Max(maxAltitude, sample.AltitudeAglFt);
            maxTouchdownVelocity = Math.Max(maxTouchdownVelocity, sample.TouchdownNormalVelocityFps);

            if (sample.OnGround) sawOnGround = true; else sawAirborne = true;
            if (sample.EngineRunning) sawEngineOn = true; else sawEngineOff = true;
        }

        Assert.True(maxAltitude > 30000, $"expected cruise altitude above 30000ft, saw {maxAltitude}");
        Assert.True(maxTouchdownVelocity > 0, "expected a nonzero touchdown normal velocity somewhere in the descent");
        Assert.True(sawOnGround);
        Assert.True(sawAirborne);
        Assert.True(sawEngineOn);
        Assert.True(sawEngineOff);
    }
}

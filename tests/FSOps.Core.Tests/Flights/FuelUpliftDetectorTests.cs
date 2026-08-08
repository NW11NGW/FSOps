using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

public class FuelUpliftDetectorTests
{
    [Fact]
    public void Classify_Rise_IsUplift()
    {
        Assert.Equal(GroundFuelChangeKind.Uplift, FuelUpliftDetector.Classify(previousKg: 1000, currentKg: 3000));
    }

    [Fact]
    public void Classify_Fall_IsDefuel()
    {
        Assert.Equal(GroundFuelChangeKind.Defuel, FuelUpliftDetector.Classify(previousKg: 3000, currentKg: 1000));
    }

    [Fact]
    public void Classify_NoChange_IsNone()
    {
        Assert.Equal(GroundFuelChangeKind.None, FuelUpliftDetector.Classify(previousKg: 2500, currentKg: 2500));
    }

    [Theory]
    [InlineData(2500, 2500.5)]
    [InlineData(2500, 2499.5)]
    [InlineData(2500, 2500.99)]
    public void Classify_WithinNoiseFloor_IsNone(double previousKg, double currentKg)
    {
        Assert.Equal(GroundFuelChangeKind.None, FuelUpliftDetector.Classify(previousKg, currentKg));
    }

    [Fact]
    public void Classify_ExactlyAtNoiseFloor_IsAnEvent()
    {
        // >= NoiseFloorKg counts (the check is Math.Abs(delta) < NoiseFloorKg, so exactly at the
        // floor is NOT filtered out) - pin the boundary explicitly rather than leaving it implicit.
        Assert.Equal(GroundFuelChangeKind.Uplift, FuelUpliftDetector.Classify(previousKg: 2500, currentKg: 2500 + FuelUpliftDetector.NoiseFloorKg));
    }

    [Fact]
    public void MagnitudeKg_IsAlwaysPositive_RegardlessOfDirection()
    {
        Assert.Equal(2000, FuelUpliftDetector.MagnitudeKg(1000, 3000));
        Assert.Equal(2000, FuelUpliftDetector.MagnitudeKg(3000, 1000));
    }

    /// <summary>
    /// "Handle repeated and partial uplifts" (docs/PLAN.md "Persistent fuel state and tankering"):
    /// a fuel truck topping off in several stages during one turnaround should read as several
    /// distinct uplift events when a caller runs a moving baseline through Classify/MagnitudeKg
    /// sample by sample, exactly as FlightLifecycleService.ProcessSample does.
    /// </summary>
    [Fact]
    public void RepeatedPartialUplifts_EachReadAsItsOwnEvent_WithTheRightMagnitude()
    {
        var readings = new[] { 1000.0, 1000.0, 1500.0, 1500.0, 1800.0, 1800.0, 1800.0, 2500.0 };
        var expectedEvents = new (GroundFuelChangeKind Kind, double DeltaKg)[]
        {
            (GroundFuelChangeKind.None, 0), // 1000 -> 1000
            (GroundFuelChangeKind.Uplift, 500), // 1000 -> 1500 (first partial uplift)
            (GroundFuelChangeKind.None, 0), // 1500 -> 1500
            (GroundFuelChangeKind.Uplift, 300), // 1500 -> 1800 (second partial uplift)
            (GroundFuelChangeKind.None, 0), // 1800 -> 1800
            (GroundFuelChangeKind.None, 0), // 1800 -> 1800
            (GroundFuelChangeKind.Uplift, 700), // 1800 -> 2500 (third, larger uplift)
        };

        var previous = readings[0];
        var actualEvents = new List<(GroundFuelChangeKind Kind, double DeltaKg)>();
        foreach (var current in readings[1..])
        {
            var kind = FuelUpliftDetector.Classify(previous, current);
            var delta = kind == GroundFuelChangeKind.None ? 0 : FuelUpliftDetector.MagnitudeKg(previous, current);
            actualEvents.Add((kind, delta));
            previous = current;
        }

        Assert.Equal(expectedEvents.Length, actualEvents.Count);
        for (var i = 0; i < expectedEvents.Length; i++)
        {
            Assert.Equal(expectedEvents[i].Kind, actualEvents[i].Kind);
            Assert.Equal(expectedEvents[i].DeltaKg, actualEvents[i].DeltaKg, 6);
        }

        // Total genuinely uplifted across the whole turnaround.
        var totalUplifted = actualEvents.Where(e => e.Kind == GroundFuelChangeKind.Uplift).Sum(e => e.DeltaKg);
        Assert.Equal(1500, totalUplifted, 6);
    }

    [Fact]
    public void UpliftThenDefuel_EachReadCorrectly_AndDefuelHasNoCreditConcept()
    {
        // GroundFuelChangeKind itself carries no money - callers decide what each kind means (see
        // its own doc: uplift is charged, defuel is a deliberate non-event, never a credit).
        var upliftKind = FuelUpliftDetector.Classify(previousKg: 1000, currentKg: 3000);
        var defuelKind = FuelUpliftDetector.Classify(previousKg: 3000, currentKg: 500);

        Assert.Equal(GroundFuelChangeKind.Uplift, upliftKind);
        Assert.Equal(GroundFuelChangeKind.Defuel, defuelKind);
        Assert.Equal(2500, FuelUpliftDetector.MagnitudeKg(3000, 500));
    }
}

using FSOps.Core.Scheduling;

namespace FSOps.Core.Tests.Scheduling;

public class VirtualPilotPerformanceCalculatorTests
{
    [Fact]
    public void Resolve_SameInputs_AlwaysProducesTheSameResult()
    {
        var entryId = Guid.NewGuid();
        var departure = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);

        var first = VirtualPilotPerformanceCalculator.Resolve(worldSeed: 7, entryId, departure, skillRating: 42);
        var second = VirtualPilotPerformanceCalculator.Resolve(worldSeed: 7, entryId, departure, skillRating: 42);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_DifferentEntryOrDeparture_ProducesDifferentDraws()
    {
        var departure = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
        var a = VirtualPilotPerformanceCalculator.Resolve(1, Guid.NewGuid(), departure, 50);
        var b = VirtualPilotPerformanceCalculator.Resolve(1, Guid.NewGuid(), departure, 50);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Resolve_MaximumSkill_NeverAddsDelayVariance()
    {
        var entryId = Guid.NewGuid();
        var departure = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);

        var result = VirtualPilotPerformanceCalculator.Resolve(worldSeed: 99, entryId, departure, skillRating: 100);

        Assert.Equal(0, result.DelayMinutes);
    }

    [Fact]
    public void Resolve_LowSkill_NeverProducesALargerDelayThanHighSkill_AcrossManySamples()
    {
        // Not a single-pair comparison (a single noise draw could coincidentally favour either
        // side) - averaged across many different entries/departures so the skill effect on the
        // ceiling is unambiguous: skill 10's average delay must be materially higher than skill
        // 90's, since VirtualPilotPerformanceCalculator scales both the ceiling and the draw itself
        // by (1 - skillFraction).
        double lowSkillTotal = 0;
        double highSkillTotal = 0;
        const int samples = 200;

        for (var i = 0; i < samples; i++)
        {
            var entryId = Guid.NewGuid();
            var departure = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero).AddMinutes(i);

            lowSkillTotal += VirtualPilotPerformanceCalculator.Resolve(1, entryId, departure, skillRating: 10).DelayMinutes;
            highSkillTotal += VirtualPilotPerformanceCalculator.Resolve(1, entryId, departure, skillRating: 90).DelayMinutes;
        }

        Assert.True(lowSkillTotal > highSkillTotal * 2, $"Expected low-skill average delay to be well above high-skill's; got low={lowSkillTotal / samples:F2}, high={highSkillTotal / samples:F2}.");
    }

    [Fact]
    public void Resolve_SkillOutsideZeroToHundred_IsClampedRatherThanThrowing()
    {
        var entryId = Guid.NewGuid();
        var departure = new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);

        var belowZero = VirtualPilotPerformanceCalculator.Resolve(1, entryId, departure, skillRating: -20);
        var atZero = VirtualPilotPerformanceCalculator.Resolve(1, entryId, departure, skillRating: 0);
        var aboveHundred = VirtualPilotPerformanceCalculator.Resolve(1, entryId, departure, skillRating: 150);
        var atHundred = VirtualPilotPerformanceCalculator.Resolve(1, entryId, departure, skillRating: 100);

        Assert.Equal(atZero, belowZero);
        Assert.Equal(atHundred, aboveHundred);
    }
}

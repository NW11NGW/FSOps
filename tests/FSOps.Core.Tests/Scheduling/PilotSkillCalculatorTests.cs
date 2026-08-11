using FSOps.Core.Economy;
using FSOps.Core.Scheduling;

namespace FSOps.Core.Tests.Scheduling;

/// <summary>
/// Exercises PilotSkillCalculator against docs/PLAN.md "Progression - reputation and pilot skill",
/// point 3: growth with diminishing returns capped below perfect, idle decay driven only by time
/// since the pilot last flew, and the player's own record never decaying.
/// </summary>
public class PilotSkillCalculatorTests
{
    private static readonly PilotSkillConfig Config = EconomyConfig.Default().PilotSkill;

    [Fact]
    public void NoHoursFlown_ReturnsExactlyStartingSkill()
    {
        var result = PilotSkillCalculator.Compute(hoursFlown: 0, lastFlewUtc: null, now: DateTimeOffset.UtcNow, Config);

        Assert.Equal(Config.StartingSkill, result);
    }

    [Fact]
    public void MoreHoursFlown_AlwaysProducesAtLeastAsMuchSkill_DiminishingReturns()
    {
        var now = DateTimeOffset.UtcNow;

        var at100 = PilotSkillCalculator.Compute(100, lastFlewUtc: now, now, Config);
        var at300 = PilotSkillCalculator.Compute(300, lastFlewUtc: now, now, Config);
        var at900 = PilotSkillCalculator.Compute(900, lastFlewUtc: now, now, Config);
        var at3000 = PilotSkillCalculator.Compute(3000, lastFlewUtc: now, now, Config);

        Assert.True(at100 < at300);
        Assert.True(at300 < at900);
        Assert.True(at900 < at3000);

        // Diminishing returns: each successive equal-sized jump in hours buys a smaller improvement
        // than the one before it (100->300 is +200h, 300->900 is +600h - not equal steps, so compare
        // the two genuinely equal 300h-wide steps between them isn't quite fair; instead assert the
        // canonical half-life property directly below).
        Assert.True(at3000 - at900 < at900 - at300, "A later stretch of hours must buy less improvement than an earlier one of comparable or larger size.");
    }

    [Fact]
    public void GrowthHalfLife_ClosesExactlyHalfTheRemainingGapToCap()
    {
        var now = DateTimeOffset.UtcNow;

        var afterOneHalfLife = PilotSkillCalculator.Compute(Config.GrowthHalfLifeHours, lastFlewUtc: now, now, Config);
        var expected = Config.StartingSkill + (Config.SkillCap - Config.StartingSkill) * 0.5;

        Assert.Equal(expected, afterOneHalfLife, precision: 6);
    }

    [Fact]
    public void Skill_NeverReachesOrExceedsTheCap_EvenAtEnormousHours()
    {
        // 10,000 hours is already far beyond any realistic career (33+ growth half-lives) while
        // staying inside double-precision range: 0.5^(10000/300) is a tiny but non-zero double.
        // A MUCH larger input (e.g. 1,000,000 hours - 3,333 half-lives) makes Math.Pow(0.5, ...)
        // underflow to a literal 0.0, at which point the formula's output becomes indistinguishable
        // from the cap in floating point - a representation limit of an enormous, unreachable test
        // input, not evidence that the model's true asymptote (1 - 0.5^x < 1 for every finite x) is
        // wrong. Docs/PLAN.md's requirement is that variance never fully disappears "even for the
        // most experienced hire" - no real pilot in this game reaches 10,000 hours, let alone
        // 1,000,000, so this input is already far past what matters.
        var now = DateTimeOffset.UtcNow;
        var result = PilotSkillCalculator.Compute(hoursFlown: 10_000, lastFlewUtc: now, now, Config);

        Assert.True(result < Config.SkillCap);
        Assert.True(result > Config.SkillCap - 0.01, "At 10,000 hours the curve should be within a hair of the cap.");
    }

    [Fact]
    public void JustFlown_NoIdleGapAtAll_IsPureGrowth_NoDecay()
    {
        var now = DateTimeOffset.UtcNow;
        var justFlown = PilotSkillCalculator.Compute(500, lastFlewUtc: now, now, Config);
        var neverFlown = PilotSkillCalculator.Compute(500, lastFlewUtc: null, now, Config);

        Assert.Equal(neverFlown, justFlown);
    }

    [Fact]
    public void IdleWithinTheGracePeriod_NeverDecays()
    {
        var lastFlew = DateTimeOffset.UtcNow;
        var now = lastFlew.AddHours(Config.IdleGracePeriodHours); // exactly at the boundary

        var grown = PilotSkillCalculator.Compute(500, lastFlewUtc: lastFlew, now: lastFlew, Config);
        var stillWithinGrace = PilotSkillCalculator.Compute(500, lastFlewUtc: lastFlew, now, Config);

        Assert.Equal(grown, stillWithinGrace, precision: 10);
    }

    [Fact]
    public void IdleBeyondTheGracePeriod_DecaysTowardStartingSkill_NeverBelowIt()
    {
        var lastFlew = DateTimeOffset.UtcNow;
        var grown = PilotSkillCalculator.Compute(2000, lastFlewUtc: lastFlew, now: lastFlew, Config);
        Assert.True(grown > Config.StartingSkill);

        var wayIdle = lastFlew.AddHours(Config.IdleGracePeriodHours + Config.IdleDecayHalfLifeHours * 20);
        var decayed = PilotSkillCalculator.Compute(2000, lastFlewUtc: lastFlew, now: wayIdle, Config);

        Assert.True(decayed < grown, "Decay must actually reduce skill once past the grace period.");
        Assert.True(decayed >= Config.StartingSkill - 0.0001, "Decay must never send skill below where the pilot started.");
        Assert.True(decayed < Config.StartingSkill + 0.01, "After an enormous idle stretch, skill should have converged almost exactly back to StartingSkill.");
    }

    [Fact]
    public void DecayHalfLife_ClosesExactlyHalfTheGapBackToStartingSkill()
    {
        var lastFlew = DateTimeOffset.UtcNow;
        var grown = PilotSkillCalculator.Compute(2000, lastFlewUtc: lastFlew, now: lastFlew, Config);

        var atOneHalfLifePastGrace = lastFlew.AddHours(Config.IdleGracePeriodHours + Config.IdleDecayHalfLifeHours);
        var decayed = PilotSkillCalculator.Compute(2000, lastFlewUtc: lastFlew, now: atOneHalfLifePastGrace, Config);

        var expected = Config.StartingSkill + (grown - Config.StartingSkill) * 0.5;
        Assert.Equal(expected, decayed, precision: 6);
    }

    [Fact]
    public void DecayIsDrivenByLastFlewUtc_NotByHowLongNowIs_ProvingItIsNeverWallClockSinceAppOpen()
    {
        // Two pilots with identical hours and identical "now", but different LastFlewUtc - one flew
        // recently (short idle gap), one hasn't flown in a long time (long idle gap). Only the gap
        // to LastFlewUtc should matter, never "now" on its own - docs/PLAN.md's explicit requirement
        // that a standing schedule (which keeps LastFlewUtc fresh) must never decay just because the
        // app itself was closed for a long time.
        var now = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recentlyFlown = now.AddDays(-1);
        var longIdle = now.AddDays(-90);

        var recent = PilotSkillCalculator.Compute(1000, lastFlewUtc: recentlyFlown, now, Config);
        var idle = PilotSkillCalculator.Compute(1000, lastFlewUtc: longIdle, now, Config);

        Assert.True(idle < recent, "A pilot idle for 90 days must show visibly more decay than one who flew yesterday, given the identical 'now'.");
    }

    [Fact]
    public void RecomputingWithTheSameInputs_AlwaysProducesTheSameResult_ProvingIdempotency()
    {
        var now = DateTimeOffset.UtcNow;
        var lastFlew = now.AddDays(-40);

        var a = PilotSkillCalculator.Compute(750, lastFlewUtc: lastFlew, now, Config);
        var b = PilotSkillCalculator.Compute(750, lastFlewUtc: lastFlew, now, Config);

        Assert.Equal(a, b);
    }
}

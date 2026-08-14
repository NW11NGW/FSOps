using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Scheduling;

namespace FSOps.Core.Tests.Scheduling;

/// <summary>
/// The three-way status rule, exercised as the pure function it is. The interesting cases are all
/// about what must NOT happen: a pilot on a standing schedule never going Inactive however long the
/// gap, the player never going Inactive at all, and "Inactive" agreeing exactly with idle skill
/// decay rather than using a threshold of its own.
/// </summary>
public class PilotStatusCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static PilotSkillConfig Config => EconomyConfigCatalog.Default().Get(AirlinePlaystyle.Casual).PilotSkill;

    [Fact]
    public void AFlightInProgress_ReadsFlying_WhoeverTheyAreAndWhateverElseIsTrue()
    {
        // Every other input pushed towards Inactive: no schedule, and idle for a year. The flight
        // in the air still wins - it is the strongest fact available about a pilot right now.
        var status = PilotStatusCalculator.Resolve(
            isPlayer: false,
            hasFlightInProgress: true,
            hasScheduledLegs: false,
            lastFlewUtc: Now.AddDays(-365),
            createdUtc: Now.AddDays(-400),
            Now,
            Config);

        Assert.Equal(PilotStatus.Flying, status);
    }

    [Fact]
    public void APilotWithAStandingSchedule_IsNeverInactive_HoweverLongSinceTheyLastFlew()
    {
        // A weekly pattern keeps rolling forever, so a long gap here means the pattern is between
        // occurrences (or its aircraft is out of position - which is item 4's advisory, not a
        // status). Calling them Inactive would tell the player their schedule had stopped, which is
        // exactly the thing that is not true.
        var status = PilotStatusCalculator.Resolve(
            isPlayer: false,
            hasFlightInProgress: false,
            hasScheduledLegs: true,
            lastFlewUtc: Now.AddDays(-365),
            createdUtc: Now.AddDays(-400),
            Now,
            Config);

        Assert.Equal(PilotStatus.Available, status);
    }

    [Fact]
    public void APilotWithNoScheduleAndNoRecentFlying_ReadsInactive()
    {
        var status = PilotStatusCalculator.Resolve(
            isPlayer: false,
            hasFlightInProgress: false,
            hasScheduledLegs: false,
            lastFlewUtc: Now.AddHours(-(Config.IdleGracePeriodHours + 1)),
            createdUtc: Now.AddDays(-400),
            Now,
            Config);

        Assert.Equal(PilotStatus.Inactive, status);
    }

    [Fact]
    public void InactiveUsesTheSameThresholdAsIdleSkillDecay_SoTheTwoCanNeverDisagree()
    {
        // The roster shows the status badge and the decay line side by side. One hour either side
        // of the grace period must flip both, or the row contradicts itself: "Available" next to
        // "skill is decaying" is a bug the moment anyone reads it.
        const double hoursFlown = 500;

        var justInsideGrace = Now.AddHours(-(Config.IdleGracePeriodHours - 1));
        var justOutsideGrace = Now.AddHours(-(Config.IdleGracePeriodHours + 1));

        var insideStatus = PilotStatusCalculator.Resolve(false, false, false, justInsideGrace, Now.AddDays(-400), Now, Config);
        var outsideStatus = PilotStatusCalculator.Resolve(false, false, false, justOutsideGrace, Now.AddDays(-400), Now, Config);

        var insideIsDecaying = PilotSkillCalculator.Compute(hoursFlown, justInsideGrace, Now, Config)
            < PilotSkillCalculator.ComputeEarnedSkill(hoursFlown, Config);
        var outsideIsDecaying = PilotSkillCalculator.Compute(hoursFlown, justOutsideGrace, Now, Config)
            < PilotSkillCalculator.ComputeEarnedSkill(hoursFlown, Config);

        Assert.Equal(PilotStatus.Available, insideStatus);
        Assert.False(insideIsDecaying);

        Assert.Equal(PilotStatus.Inactive, outsideStatus);
        Assert.True(outsideIsDecaying);
    }

    [Fact]
    public void ThePlayerPilotIsNeverInactive_HoweverLongTheyHaveBeenAwayFromTheSim()
    {
        // Idle skill decay already skips the player deliberately. A label carries the same weight:
        // "Inactive" against the human's own name is the app telling them off for not playing.
        var status = PilotStatusCalculator.Resolve(
            isPlayer: true,
            hasFlightInProgress: false,
            hasScheduledLegs: false,
            lastFlewUtc: Now.AddDays(-365),
            createdUtc: Now.AddDays(-400),
            Now,
            Config);

        Assert.Equal(PilotStatus.Available, status);
    }

    [Fact]
    public void ANeverFlownPilotIsJudgedFromWhenTheyWereHired_NotTreatedAsInfinitelyIdle()
    {
        // LastFlewUtc is null for a pilot who has never flown a sector. Measuring idleness from
        // "never" would make every new hire Inactive the instant they arrived.
        var freshHire = PilotStatusCalculator.Resolve(
            isPlayer: false, hasFlightInProgress: false, hasScheduledLegs: false,
            lastFlewUtc: null, createdUtc: Now.AddHours(-1), Now, Config);

        var hiredAndForgotten = PilotStatusCalculator.Resolve(
            isPlayer: false, hasFlightInProgress: false, hasScheduledLegs: false,
            lastFlewUtc: null, createdUtc: Now.AddHours(-(Config.IdleGracePeriodHours + 1)), Now, Config);

        Assert.Equal(PilotStatus.Available, freshHire);
        Assert.Equal(PilotStatus.Inactive, hiredAndForgotten);
    }

    [Fact]
    public void AClockThatRunsBackwards_NeverProducesInactive()
    {
        // A last-flew timestamp in the future (clock change, restored backup) must not underflow
        // into a large idle figure. Math.Max(0, ...) in the calculator is what stops it.
        var status = PilotStatusCalculator.Resolve(
            isPlayer: false, hasFlightInProgress: false, hasScheduledLegs: false,
            lastFlewUtc: Now.AddDays(5), createdUtc: Now.AddDays(-400), Now, Config);

        Assert.Equal(PilotStatus.Available, status);
    }
}

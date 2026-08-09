using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

/// <summary>
/// "Perform maintenance now" - docs/PLAN.md "A 'perform maintenance now' button on the Fleet page":
/// bringing a check forward must charge the FULL cost and downtime for that check type (never
/// pro-rated down for however many hours were left on the cycle) and must forfeit whatever hours
/// remained - otherwise bringing a check forward would be a way to dodge cost rather than a real
/// trade-off. Pure - exactly like MaintenanceSchedulerTests, no database.
/// </summary>
public class MaintenanceSchedulerForcedCheckTests
{
    private static FleetAircraft NewAircraft(double hoursSinceACheck, double hoursSinceCCheck, double conditionPercent) => new()
    {
        Id = Guid.NewGuid(),
        Registration = "G-TEST",
        HoursSinceACheck = hoursSinceACheck,
        HoursSinceCCheck = hoursSinceCCheck,
        ConditionPercent = conditionPercent,
    };

    [Fact]
    public void ForcedACheck_ChargesFullCostAndDowntime_RegardlessOfHoursRemaining()
    {
        var config = EconomyConfig.Default();
        // Only 50 hours into the 500-hour cycle - 450 hours of accrual are about to be forfeited.
        var aircraft = NewAircraft(hoursSinceACheck: 50, hoursSinceCCheck: 50, conditionPercent: 90);

        var outcome = MaintenanceScheduler.ApplyForced(aircraft, MaintenanceEventType.ACheck, config);

        Assert.True(outcome.CheckTriggered);
        Assert.Equal(MaintenanceEventType.ACheck, outcome.Type);
        // Full cost - NOT scaled down for the 450 hours of the cycle that were still unused.
        Assert.Equal(config.Maintenance.ACheckCost, outcome.Cost);
        Assert.Equal(config.Maintenance.ACheckDowntimeHours, outcome.DowntimeHours);
        // The forfeiture: the cycle resets to zero exactly as if it had run its full natural course.
        Assert.Equal(0, outcome.NewHoursSinceACheck);
        // C-check clock is untouched by a forced A-check, same as a natural one.
        Assert.Equal(50, outcome.NewHoursSinceCCheck);
    }

    [Fact]
    public void ForcedCCheck_ChargesFullCostAndDowntime_ResetsBothCyclesAndRestoresFullCondition()
    {
        var config = EconomyConfig.Default();
        var aircraft = NewAircraft(hoursSinceACheck: 10, hoursSinceCCheck: 10, conditionPercent: 80);

        var outcome = MaintenanceScheduler.ApplyForced(aircraft, MaintenanceEventType.CCheck, config);

        Assert.True(outcome.CheckTriggered);
        Assert.Equal(MaintenanceEventType.CCheck, outcome.Type);
        Assert.Equal(config.Maintenance.CCheckCost, outcome.Cost);
        Assert.Equal(config.Maintenance.CCheckDowntimeHours, outcome.DowntimeHours);
        Assert.Equal(0, outcome.NewHoursSinceACheck);
        Assert.Equal(0, outcome.NewHoursSinceCCheck);
        Assert.Equal(100, outcome.NewConditionPercent);
    }

    [Fact]
    public void ForcedACheck_RestoresConditionByTheConfiguredAmount_CappedAtFull()
    {
        var config = EconomyConfig.Default();
        var aircraft = NewAircraft(hoursSinceACheck: 5, hoursSinceCCheck: 5, conditionPercent: 95);

        var outcome = MaintenanceScheduler.ApplyForced(aircraft, MaintenanceEventType.ACheck, config);

        // 95 + 35 restore would overshoot 100 without the cap.
        Assert.Equal(100, outcome.NewConditionPercent);
    }

    [Fact]
    public void ForcedCheck_UnscheduledType_Throws()
    {
        var config = EconomyConfig.Default();
        var aircraft = NewAircraft(hoursSinceACheck: 0, hoursSinceCCheck: 0, conditionPercent: 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => MaintenanceScheduler.ApplyForced(aircraft, MaintenanceEventType.Unscheduled, config));
    }

    [Fact]
    public void ForcedCheck_CostNeverDependsOnHowManyHoursWereAlreadyAccrued()
    {
        var config = EconomyConfig.Default();
        var freshlyChecked = NewAircraft(hoursSinceACheck: 1, hoursSinceCCheck: 1, conditionPercent: 100);
        var almostDue = NewAircraft(hoursSinceACheck: 499, hoursSinceCCheck: 499, conditionPercent: 60);

        var freshOutcome = MaintenanceScheduler.ApplyForced(freshlyChecked, MaintenanceEventType.ACheck, config);
        var almostDueOutcome = MaintenanceScheduler.ApplyForced(almostDue, MaintenanceEventType.ACheck, config);

        // Same cost and downtime either way - bringing a check forward is never cheaper just
        // because there happened to be little left to forfeit.
        Assert.Equal(freshOutcome.Cost, almostDueOutcome.Cost);
        Assert.Equal(freshOutcome.DowntimeHours, almostDueOutcome.DowntimeHours);
    }
}

using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

/// <summary>
/// Chunk E1's own stated verification (the E1 task brief): a check fires at the right hours,
/// grounds the aircraft (via the outcome the caller applies), posts the right cost, and downtime
/// differs between playstyles. Pure - no database, no server, exactly what MaintenanceScheduler is
/// for.
/// </summary>
public class MaintenanceSchedulerTests
{
    private static FleetAircraft NewAircraft(double hoursSinceACheck = 0, double hoursSinceCCheck = 0, double conditionPercent = 100) => new()
    {
        Id = Guid.NewGuid(),
        Registration = "G-TEST",
        HoursSinceACheck = hoursSinceACheck,
        HoursSinceCCheck = hoursSinceCCheck,
        ConditionPercent = conditionPercent,
    };

    [Fact]
    public void BelowThreshold_NoCheckFires_HoursAndConditionStillAccumulate()
    {
        var config = EconomyConfig.Default();
        var aircraft = NewAircraft(hoursSinceACheck: 100, hoursSinceCCheck: 100, conditionPercent: 90);

        var outcome = MaintenanceScheduler.Apply(aircraft, flightHours: 50, config);

        Assert.False(outcome.CheckTriggered);
        Assert.Null(outcome.Type);
        Assert.Equal(0m, outcome.Cost);
        Assert.Equal(150, outcome.NewHoursSinceACheck);
        Assert.Equal(150, outcome.NewHoursSinceCCheck);
        // 50 hours * 0.12/hour decay = 6 points.
        Assert.Equal(84, outcome.NewConditionPercent, precision: 6);
    }

    [Fact]
    public void CrossingFiveHundredHours_TriggersACheck_WithConfiguredCostAndDowntime()
    {
        var config = EconomyConfig.Default();
        var aircraft = NewAircraft(hoursSinceACheck: 498, hoursSinceCCheck: 498, conditionPercent: 40);

        var outcome = MaintenanceScheduler.Apply(aircraft, flightHours: 3, config);

        Assert.True(outcome.CheckTriggered);
        Assert.Equal(MaintenanceEventType.ACheck, outcome.Type);
        Assert.Equal(config.Maintenance.ACheckCost, outcome.Cost);
        Assert.Equal(config.Maintenance.ACheckDowntimeHours, outcome.DowntimeHours);
        Assert.Equal(0, outcome.NewHoursSinceACheck);
        // C-check clock is NOT reset by an A-check - only the A-check cycle resets.
        Assert.Equal(501, outcome.NewHoursSinceCCheck);
        // Condition after decay (40 - 3*0.12=39.64) plus the A-check's restore (35), capped at 100.
        Assert.Equal(74.64, outcome.NewConditionPercent, precision: 6);
    }

    [Fact]
    public void ACheckRestoreNeverExceedsFullCondition()
    {
        var config = EconomyConfig.Default();
        // Condition is already high (95) - restoring 35 points would overshoot 100 without the cap.
        var aircraft = NewAircraft(hoursSinceACheck: 499.9, hoursSinceCCheck: 100, conditionPercent: 95);

        var outcome = MaintenanceScheduler.Apply(aircraft, flightHours: 1, config);

        Assert.True(outcome.CheckTriggered);
        Assert.Equal(MaintenanceEventType.ACheck, outcome.Type);
        Assert.Equal(100, outcome.NewConditionPercent);
    }

    [Fact]
    public void CrossingFourThousandHours_TriggersCCheck_NotARedundantACheck()
    {
        var config = EconomyConfig.Default();
        // Both cycles cross their threshold on the same flight (4000 is a whole multiple of 500) -
        // the C-check must take priority and reset both, not fire an A-check alongside it.
        var aircraft = NewAircraft(hoursSinceACheck: 499, hoursSinceCCheck: 3999, conditionPercent: 50);

        var outcome = MaintenanceScheduler.Apply(aircraft, flightHours: 2, config);

        Assert.True(outcome.CheckTriggered);
        Assert.Equal(MaintenanceEventType.CCheck, outcome.Type);
        Assert.Equal(config.Maintenance.CCheckCost, outcome.Cost);
        Assert.Equal(config.Maintenance.CCheckDowntimeHours, outcome.DowntimeHours);
        Assert.Equal(0, outcome.NewHoursSinceACheck);
        Assert.Equal(0, outcome.NewHoursSinceCCheck);
        // A C-check is a full overhaul - condition always resets to exactly 100, regardless of how
        // worn the airframe was going in.
        Assert.Equal(100, outcome.NewConditionPercent);
    }

    [Fact]
    public void ConditionNeverDropsBelowZero()
    {
        var config = EconomyConfig.Default();
        var aircraft = NewAircraft(hoursSinceACheck: 0, hoursSinceCCheck: 0, conditionPercent: 1);

        var outcome = MaintenanceScheduler.Apply(aircraft, flightHours: 100, config);

        Assert.Equal(0, outcome.NewConditionPercent);
    }

    [Fact]
    public void NegativeFlightHours_Throws()
    {
        var config = EconomyConfig.Default();
        Assert.Throws<ArgumentOutOfRangeException>(() => MaintenanceScheduler.Apply(NewAircraft(), -1, config));
    }

    [Fact]
    public void Downtime_DiffersBetweenPlaystyles_SameCycleAndCost()
    {
        var catalog = EconomyConfigCatalog.Default();
        var casual = catalog.Get(AirlinePlaystyle.Casual);
        var trueLife = catalog.Get(AirlinePlaystyle.TrueLife);

        var casualOutcome = MaintenanceScheduler.Apply(NewAircraft(hoursSinceACheck: 500), 0, casual);
        var trueLifeOutcome = MaintenanceScheduler.Apply(NewAircraft(hoursSinceACheck: 500), 0, trueLife);

        Assert.True(casualOutcome.CheckTriggered);
        Assert.True(trueLifeOutcome.CheckTriggered);

        // Same cycle (both triggered at exactly 500h) and same cost - what differs between the
        // playstyles is how long a check grounds the aircraft, never what the work costs.
        Assert.Equal(casualOutcome.Cost, trueLifeOutcome.Cost);
        Assert.Equal(casual.Maintenance.ACheckDowntimeHours, casualOutcome.DowntimeHours);
        Assert.Equal(trueLife.Maintenance.ACheckDowntimeHours, trueLifeOutcome.DowntimeHours);
        Assert.True(trueLifeOutcome.DowntimeHours > casualOutcome.DowntimeHours);

        // Casual: hours, a nuisance and a bill. True-life: about a day.
        Assert.True(casualOutcome.DowntimeHours <= 24);
        Assert.Equal(24, trueLifeOutcome.DowntimeHours);
    }

    [Fact]
    public void CCheckDowntime_TrueLifeIsAboutAFortnight_CasualIsAboutADay()
    {
        var catalog = EconomyConfigCatalog.Default();
        var casual = catalog.Get(AirlinePlaystyle.Casual);
        var trueLife = catalog.Get(AirlinePlaystyle.TrueLife);

        Assert.Equal(24, casual.Maintenance.CCheckDowntimeHours);
        Assert.Equal(336, trueLife.Maintenance.CCheckDowntimeHours); // 14 days
    }

    [Fact]
    public void ResolveUsedAircraftState_IsCheaperAndFurtherIntoBothCycles()
    {
        var config = EconomyConfig.Default();
        const decimal newPrice = 40_000_000m;

        var used = MaintenanceScheduler.ResolveUsedAircraftState(newPrice, config);

        Assert.True(used.PurchasePrice < newPrice);
        Assert.Equal(newPrice * config.UsedAircraft.PriceMultiplier, used.PurchasePrice);
        Assert.True(used.HoursSinceACheck > 0);
        Assert.True(used.HoursSinceCCheck > 0);
        Assert.True(used.ConditionPercent < 100);
        // 70% of the way to the next A-check (350 of 500).
        Assert.Equal(350, used.HoursSinceACheck);
        Assert.Equal(2800, used.HoursSinceCCheck);
        Assert.Equal(70, used.ConditionPercent);
    }
}

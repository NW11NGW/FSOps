using FSOps.Core.Economy;
using FSOps.Core.Finance;

namespace FSOps.Core.Tests.Finance;

/// <summary>
/// Exact-value coverage for docs/PLAN.md "Returning a lease early must cost something" - proof the
/// "lease, fly for free, return before the billing tick" exploit is closed. Pure arithmetic, no
/// database.
/// </summary>
public class LeaseTerminationCalculatorTests
{
    private static readonly LeaseEarlyTerminationConfig Config = new() { FeeMonths = 0.5 };
    private static readonly TimeSpan PeriodLength = TimeSpan.FromDays(30);

    [Fact]
    public void ReturnedTheInstantItWasTaken_StillCostsTheFullTerminationFee()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        // lastProcessedUtc == now: zero elapsed time in the current billing period - the exact
        // "lease and immediately return" exploit scenario docs/PLAN.md calls out.
        var settlement = LeaseTerminationCalculator.ComputeSettlement(
            monthlyRate: 30_000m, lastProcessedUtc: now, now: now, PeriodLength, Config);

        Assert.Equal(0.0, settlement.DaysIntoCurrentPeriod, precision: 6);
        Assert.Equal(0.00m, settlement.ProRataAmount);
        Assert.Equal(15_000.00m, settlement.EarlyTerminationFee); // 30,000 * 0.5 months
        Assert.Equal(15_000.00m, settlement.TotalCharge);
        // The exploit test: even with zero rent owed, termination is never free.
        Assert.True(settlement.TotalCharge > 0m);
    }

    [Fact]
    public void PartwayThroughThePeriod_ChargesProRataRentPlusTheFee()
    {
        var lastProcessed = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var now = lastProcessed.AddDays(8);

        var settlement = LeaseTerminationCalculator.ComputeSettlement(
            monthlyRate: 30_000m, lastProcessed, now, PeriodLength, Config);

        Assert.Equal(8.0, settlement.DaysIntoCurrentPeriod, precision: 6);
        Assert.Equal(8_000.00m, settlement.ProRataAmount); // 30,000 * 8/30
        Assert.Equal(15_000.00m, settlement.EarlyTerminationFee);
        Assert.Equal(23_000.00m, settlement.TotalCharge);
    }

    [Fact]
    public void ElapsedBeyondOnePeriod_ProRataClampsAtOneFullMonth()
    {
        var lastProcessed = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var now = lastProcessed.AddDays(45); // overdue - EconomyClockService should have ticked by now

        var settlement = LeaseTerminationCalculator.ComputeSettlement(
            monthlyRate: 30_000m, lastProcessed, now, PeriodLength, Config);

        Assert.Equal(30.0, settlement.DaysIntoCurrentPeriod, precision: 6);
        Assert.Equal(30_000.00m, settlement.ProRataAmount);
        Assert.Equal(45_000.00m, settlement.TotalCharge);
    }

    [Fact]
    public void ClockAppearsToMoveBackwards_ProRataClampsAtZero_NeverNegative()
    {
        var lastProcessed = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var now = lastProcessed.AddDays(-5); // defensive: should never happen, but must not go negative

        var settlement = LeaseTerminationCalculator.ComputeSettlement(
            monthlyRate: 30_000m, lastProcessed, now, PeriodLength, Config);

        Assert.Equal(0.0, settlement.DaysIntoCurrentPeriod, precision: 6);
        Assert.Equal(0.00m, settlement.ProRataAmount);
        Assert.Equal(15_000.00m, settlement.TotalCharge);
    }

    [Fact]
    public void TrueLifeFeeIsHarsherThanCasual()
    {
        var now = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var casual = LeaseTerminationCalculator.ComputeSettlement(30_000m, now, now, PeriodLength, new LeaseEarlyTerminationConfig { FeeMonths = 0.5 });
        var trueLife = LeaseTerminationCalculator.ComputeSettlement(30_000m, now, now, PeriodLength, new LeaseEarlyTerminationConfig { FeeMonths = 1.0 });

        Assert.Equal(15_000.00m, casual.TotalCharge);
        Assert.Equal(30_000.00m, trueLife.TotalCharge);
        Assert.True(trueLife.TotalCharge > casual.TotalCharge);
    }
}

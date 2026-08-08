using FSOps.Core.Finance;

namespace FSOps.Core.Tests.Finance;

public class LoanEligibilityCalculatorTests
{
    [Fact]
    public void MaxMonthlyPayment_IsThirtyPercentOfTrailingNetCashFlow()
    {
        Assert.Equal(13_500m, LoanEligibilityCalculator.MaxMonthlyPayment(45_000m));
    }

    [Fact]
    public void MaxMonthlyPayment_NegativeCashFlow_IsZero_NotNegative()
    {
        Assert.Equal(0m, LoanEligibilityCalculator.MaxMonthlyPayment(-10_000m));
    }

    [Fact]
    public void Evaluate_AffordableLoan_IsEligible()
    {
        // 200,000 over 60 months at 6% -> ~3,866.56/month, well under 30% of 45,000 (13,500).
        var result = LoanEligibilityCalculator.Evaluate(principal: 200_000m, annualRatePct: 6, termMonths: 60, trailing30DayNetOperatingCashFlow: 45_000m);

        Assert.True(result.IsEligible);
        Assert.Equal(13_500m, result.MaxMonthlyPayment);
        Assert.True(result.MonthlyPayment < result.MaxMonthlyPayment);
    }

    [Fact]
    public void Evaluate_UnaffordableLoan_IsRejected_ButStillReportsBothFigures()
    {
        // A used A320-scale loan against a casual single-aircraft airline's cash flow - this is
        // the exact scenario LoanEligibilityCalculator's class doc explains is meant to fail.
        var result = LoanEligibilityCalculator.Evaluate(principal: 20_000_000m, annualRatePct: 6, termMonths: 60, trailing30DayNetOperatingCashFlow: 45_000m);

        Assert.False(result.IsEligible);
        Assert.Equal(13_500m, result.MaxMonthlyPayment);
        Assert.True(result.MonthlyPayment > result.MaxMonthlyPayment);
    }

    [Fact]
    public void Evaluate_NoCashFlowHistory_AllowsNoNewBorrowing()
    {
        var result = LoanEligibilityCalculator.Evaluate(principal: 1_000m, annualRatePct: 6, termMonths: 12, trailing30DayNetOperatingCashFlow: 0m);

        Assert.False(result.IsEligible);
        Assert.Equal(0m, result.MaxMonthlyPayment);
    }
}

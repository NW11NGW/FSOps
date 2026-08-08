using FSOps.Core.Finance;

namespace FSOps.Core.Tests.Finance;

public class LoanCalculatorTests
{
    [Fact]
    public void MonthlyPayment_ZeroInterest_IsPrincipalDividedByTerm()
    {
        var payment = LoanCalculator.MonthlyPayment(principal: 12000m, annualRatePct: 0, termMonths: 12);

        Assert.Equal(1000m, payment);
    }

    [Fact]
    public void MonthlyPayment_WithInterest_MatchesStandardAmortizationFormula()
    {
        // 10,000,000 over 60 months at 6% APR - standard amortizing loan payment.
        var payment = LoanCalculator.MonthlyPayment(principal: 10_000_000m, annualRatePct: 6, termMonths: 60);

        Assert.Equal(193328.02m, payment);
    }

    [Fact]
    public void MonthlyPayment_InvalidTerm_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoanCalculator.MonthlyPayment(1000m, 5, 0));
    }

    [Fact]
    public void ApplyMonthlyPayment_SplitsInterestAndPrincipal_AndReducesBalance()
    {
        // 10,000,000 at 6% over 60 months - same loan as the amortization test above, so the
        // level payment (193,328.02) is a known-good figure to split.
        var payment = LoanCalculator.MonthlyPayment(10_000_000m, 6, 60);

        var breakdown = LoanCalculator.ApplyMonthlyPayment(10_000_000m, 6, payment);

        // First month's interest on 10,000,000 at 6% APR = 10,000,000 * 0.06/12 = 50,000 exactly.
        Assert.Equal(50_000m, breakdown.InterestPortion);
        Assert.Equal(payment - 50_000m, breakdown.PrincipalPortion);
        Assert.Equal(10_000_000m - breakdown.PrincipalPortion, breakdown.NewRemainingBalance);
        Assert.Equal(payment, breakdown.ActualPayment);
        Assert.False(breakdown.IsPaidOff);
    }

    [Fact]
    public void ApplyMonthlyPayment_FinalPayment_ClampsToRemainingBalance_AndMarksPaidOff()
    {
        // A small remaining balance smaller than the level payment - the last month of a loan.
        var breakdown = LoanCalculator.ApplyMonthlyPayment(remainingBalance: 500m, annualRatePct: 6, monthlyPayment: 1000m);

        Assert.Equal(0m, breakdown.NewRemainingBalance);
        Assert.True(breakdown.IsPaidOff);
        // Never pays more than what's actually owed (interest on the small remaining balance plus
        // the remaining balance itself), even though the level payment would allow more.
        Assert.True(breakdown.ActualPayment <= 500m + 500m * 0.06m / 12);
    }

    [Fact]
    public void ApplyMonthlyPayment_ZeroRemainingBalance_IsAlreadyPaidOff()
    {
        var breakdown = LoanCalculator.ApplyMonthlyPayment(remainingBalance: 0m, annualRatePct: 6, monthlyPayment: 1000m);

        Assert.Equal(0m, breakdown.NewRemainingBalance);
        Assert.Equal(0m, breakdown.ActualPayment);
        Assert.True(breakdown.IsPaidOff);
    }

    [Fact]
    public void ApplyMonthlyPayment_NegativeRemainingBalance_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoanCalculator.ApplyMonthlyPayment(-1m, 6, 100m));
    }
}

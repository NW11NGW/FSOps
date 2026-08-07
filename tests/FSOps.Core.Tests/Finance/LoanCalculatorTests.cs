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
}

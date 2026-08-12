using FSOps.Core.Economy;
using FSOps.Core.Finance;

namespace FSOps.Core.Tests.Finance;

/// <summary>
/// Exact-value coverage for paying a loan back early - the
/// payoff quote, the overpayment arithmetic, and proof "borrow then immediately settle in full" is
/// never free (the fee guarantees a loss). Pure arithmetic, no database.
/// </summary>
public class LoanSettlementCalculatorTests
{
    private static readonly LoanEarlySettlementConfig Config = new() { FeePercentOfRemainingBalance = 0.02m };

    [Fact]
    public void FullPayoffQuote_OneMonthTerm_ExactInterestSavedAndFee()
    {
        // A clean, hand-verifiable case: 1-month term at 12% APR (1%/month) makes
        // MonthlyPayment = principal * (1 + monthlyRate) exactly, by the amortisation formula's own
        // algebra (see LoanCalculator.MonthlyPayment) - 100,000 * 1.01 = 101,000.00.
        var quote = LoanSettlementCalculator.ComputeFullPayoffQuote(
            remainingBalance: 100_000m, annualRatePct: 12.0, monthlyPayment: 101_000m, Config);

        Assert.Equal(100_000m, quote.RemainingBalance);
        Assert.Equal(2_000.00m, quote.EarlySettlementFee); // 2% of 100,000
        Assert.Equal(102_000.00m, quote.TotalPayoff);
        // One month's interest at 1%/month on 100,000 is exactly 1,000.00 - that's what settling
        // now saves versus letting the single remaining payment land.
        Assert.Equal(1_000.00m, quote.InterestSaved);
    }

    [Fact]
    public void TakeALoan_ImmediatelySettleInFull_IsAlwaysALoss_NeverFree()
    {
        // Zero elapsed time: no interest has accrued to "save" at all, so the ONLY thing that could
        // possibly make this cost anything is the fee - which is exactly the point: borrowing and
        // repaying immediately must never be free.
        var quote = LoanSettlementCalculator.ComputeFullPayoffQuote(
            remainingBalance: 100_000m, annualRatePct: 5.0, monthlyPayment: 4_432.06m, Config);

        Assert.True(quote.EarlySettlementFee > 0m);
        Assert.True(quote.TotalPayoff > quote.RemainingBalance, "Paying off a loan the instant it's taken must still cost more than the principal alone.");
        Assert.Equal(2_000.00m, quote.EarlySettlementFee);
        Assert.Equal(102_000.00m, quote.TotalPayoff);
    }

    [Fact]
    public void AlreadyPaidOff_QuoteHasNoFeeAndNoInterestSaved()
    {
        var quote = LoanSettlementCalculator.ComputeFullPayoffQuote(remainingBalance: 0m, annualRatePct: 5.0, monthlyPayment: 4_432.06m, Config);

        Assert.Equal(0.00m, quote.EarlySettlementFee);
        Assert.Equal(0.00m, quote.TotalPayoff);
        Assert.Equal(0.00m, quote.InterestSaved);
    }

    [Fact]
    public void TrueLifeFeeIsHarsherThanCasual()
    {
        var casual = LoanSettlementCalculator.ComputeFullPayoffQuote(100_000m, 5.0, 4_432.06m, new LoanEarlySettlementConfig { FeePercentOfRemainingBalance = 0.02m });
        var trueLife = LoanSettlementCalculator.ComputeFullPayoffQuote(100_000m, 8.0, 4_522.11m, new LoanEarlySettlementConfig { FeePercentOfRemainingBalance = 0.04m });

        Assert.Equal(2_000.00m, casual.EarlySettlementFee);
        Assert.Equal(4_000.00m, trueLife.EarlySettlementFee);
    }

    [Fact]
    public void ApplyOverpayment_ReducesBalance_ByExactlyTheAmountPaid()
    {
        var newBalance = LoanSettlementCalculator.ApplyOverpayment(remainingBalance: 82_500m, overpaymentAmount: 10_000m);

        Assert.Equal(72_500m, newBalance);
    }

    [Fact]
    public void ApplyOverpayment_ExceedingBalance_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoanSettlementCalculator.ApplyOverpayment(50_000m, 50_000.01m));
    }

    [Fact]
    public void ApplyOverpayment_NonPositiveAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoanSettlementCalculator.ApplyOverpayment(50_000m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoanSettlementCalculator.ApplyOverpayment(50_000m, -1m));
    }

    [Fact]
    public void SimulateRemainingAmortization_AlreadyZero_ReturnsNoMonthsNoInterest()
    {
        var (months, interest) = LoanSettlementCalculator.SimulateRemainingAmortization(0m, 5.0, 4_432.06m);

        Assert.Equal(0, months);
        Assert.Equal(0.00m, interest);
    }

    [Fact]
    public void SimulateRemainingAmortization_BrandNew36MonthLoan_ReportsExactly36MonthsRemaining_NeverThirtySeven()
    {
        // The exact defect this test guards: FinanceEndpoints.LoansAsync feeds a brand-new loan's
        // RemainingBalance/AnnualInterestRate/MonthlyPayment straight into this method to compute
        // remainingTermMonths for the Finances page. Before the LoanCalculator rounding-remainder
        // fix, 10,000 at 3% APR over 36 months (level payment 290.81) reported 37 months remaining -
        // a brand-new loan appearing to already be behind schedule on the day it was taken out.
        const decimal principal = 10_000m;
        const double annualRatePct = 3.0;
        var payment = LoanCalculator.MonthlyPayment(principal, annualRatePct, termMonths: 36);

        var (remainingTermMonths, totalInterestRemaining) =
            LoanSettlementCalculator.SimulateRemainingAmortization(principal, annualRatePct, payment);

        Assert.Equal(36, remainingTermMonths);
        Assert.Equal(469.25m, totalInterestRemaining);
    }

    [Fact]
    public void SimulateRemainingAmortization_BrandNew36MonthLoanWherePaymentRoundsUp_AlsoReportsExactly36Months()
    {
        // Companion to the 3% case above, at a rate where the level payment rounds UP from the
        // exact theoretical figure instead of down. Both directions matter: an earlier, incorrect
        // version of the underlying fix collapsed this exact case to 35 months instead of 36.
        const decimal principal = 10_000m;
        const double annualRatePct = 5.0;
        var payment = LoanCalculator.MonthlyPayment(principal, annualRatePct, termMonths: 36);

        var (remainingTermMonths, totalInterestRemaining) =
            LoanSettlementCalculator.SimulateRemainingAmortization(principal, annualRatePct, payment);

        Assert.Equal(36, remainingTermMonths);
        Assert.Equal(789.54m, totalInterestRemaining);
    }

    [Fact]
    public void SimulateRemainingAmortization_PathologicalRate_StopsAtTheSafetyCap()
    {
        // A payment that barely covers interest never pays down principal (ApplyMonthlyPayment
        // clamps PrincipalPortion to 0 rather than letting the balance grow) - this must terminate
        // via the defensive MaxSimulatedMonths cap, not loop forever.
        var (months, interest) = LoanSettlementCalculator.SimulateRemainingAmortization(100_000m, 100.0, 1m);

        Assert.Equal(1200, months);
        Assert.True(interest > 0m);
    }
}

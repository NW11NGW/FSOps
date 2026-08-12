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

    [Fact]
    public void ApplyMonthlyPayment_TailRemainder_IsAbsorbedIntoThisPayment_NotLeftForAnother()
    {
        // The exact repro behind the "37 of 36 mo remaining" bug: 10,000 over 36 months at 3% APR
        // gives a level payment of 290.81, which - applied unchanged for 35 months - leaves exactly
        // 0.09 owed going into what should be the final (36th) period. Confirmed by hand simulation
        // against this same formula before the fix existed.
        var breakdown = LoanCalculator.ApplyMonthlyPayment(remainingBalance: 0.09m, annualRatePct: 3.0, monthlyPayment: 290.81m);

        // The remainder is folded into this payment - the loan closes here rather than needing a
        // pathological extra period for 9 pence. Interest on 0.09 at 3%/12 rounds to zero, so the
        // whole 0.09 becomes principal.
        Assert.Equal(0m, breakdown.InterestPortion);
        Assert.Equal(0.09m, breakdown.PrincipalPortion);
        Assert.Equal(0.09m, breakdown.ActualPayment);
        Assert.Equal(0m, breakdown.NewRemainingBalance);
        Assert.True(breakdown.IsPaidOff);
    }

    [Fact]
    public void ApplyMonthlyPayment_MidTermPayment_IsNeverAlteredByTailAbsorption()
    {
        // Sanity check on the tail-absorption logic added for the rounding-remainder fix: a normal
        // mid-loan payment, with plenty of balance left to run, must come out completely unchanged.
        var breakdown = LoanCalculator.ApplyMonthlyPayment(remainingBalance: 10_000m, annualRatePct: 3.0, monthlyPayment: 290.81m);

        Assert.Equal(25.00m, breakdown.InterestPortion);
        Assert.Equal(265.81m, breakdown.PrincipalPortion);
        Assert.Equal(290.81m, breakdown.ActualPayment);
        Assert.Equal(9_734.19m, breakdown.NewRemainingBalance);
        Assert.False(breakdown.IsPaidOff);
    }

    [Fact]
    public void ApplyMonthlyPayment_TwoPeriodsFromPayoff_IsNeverMistakenForTheFinalPeriod()
    {
        // The exact shape of a regression caught before this fix shipped: an earlier version of the
        // tail-absorption logic compared this period's own principal capacity against next period's,
        // which is satisfied whenever the balance merely drops to roughly one payment's size - a
        // full period before the loan is actually done - and wrongly closed the loan a whole
        // instalment early, skipping real principal and interest that should still have been
        // charged. 10,000 at 5% APR over 36 months (level payment 299.71) reaches a balance of
        // 595.68 with exactly two ordinary periods left to run; this must come out as a completely
        // normal payment, not the final one.
        var breakdown = LoanCalculator.ApplyMonthlyPayment(remainingBalance: 595.68m, annualRatePct: 5.0, monthlyPayment: 299.71m);

        Assert.Equal(2.48m, breakdown.InterestPortion);
        Assert.Equal(297.23m, breakdown.PrincipalPortion);
        Assert.Equal(299.71m, breakdown.ActualPayment);
        Assert.Equal(298.45m, breakdown.NewRemainingBalance);
        Assert.False(breakdown.IsPaidOff);
    }

    [Fact]
    public void FullSchedule_36MonthLoanAt5Percent_ClosesInExactly36Payments_NeitherEarlyNorLate()
    {
        // Companion to the 3% case below: at 5% APR the level payment (299.71) is rounded UP from
        // the exact theoretical figure rather than down, which - before this fix - was the exact
        // scenario an earlier, incorrect version of the tail-absorption logic collapsed into 35
        // payments instead of 36 (see ApplyMonthlyPayment_TwoPeriodsFromPayoff_IsNeverMistakenForTheFinalPeriod).
        // Runs the whole schedule end to end to prove the fixed version gets both directions right.
        const decimal principal = 10_000m;
        const double annualRatePct = 5.0;
        const int termMonths = 36;

        var payment = LoanCalculator.MonthlyPayment(principal, annualRatePct, termMonths);
        Assert.Equal(299.71m, payment);

        var balance = principal;
        var totalInterest = 0m;
        var totalPaid = 0m;
        var months = 0;

        while (balance > 0m && months < 100)
        {
            var breakdown = LoanCalculator.ApplyMonthlyPayment(balance, annualRatePct, payment);
            totalInterest += breakdown.InterestPortion;
            totalPaid += breakdown.ActualPayment;
            balance = breakdown.NewRemainingBalance;
            months++;

            if (breakdown.IsPaidOff)
            {
                break;
            }
        }

        Assert.Equal(36, months);
        Assert.Equal(0m, balance);
        Assert.Equal(principal + totalInterest, totalPaid);
        Assert.Equal(789.54m, totalInterest);
    }

    [Fact]
    public void FullSchedule_36MonthLoan_ClosesInExactly36Payments_WithNothingOverOrUnderCharged()
    {
        // The end-to-end proof for the rounding-remainder fix: run the entire schedule of a real
        // loan (10,000 at 3% APR over 36 months - the exact figures behind the "37 of 36 mo
        // remaining" bug report) via the same per-period step EconomyClockService uses for actual
        // billing, and check the loan closes on schedule with the books balancing exactly.
        const decimal principal = 10_000m;
        const double annualRatePct = 3.0;
        const int termMonths = 36;

        var payment = LoanCalculator.MonthlyPayment(principal, annualRatePct, termMonths);
        Assert.Equal(290.81m, payment);

        var balance = principal;
        var totalInterest = 0m;
        var totalPaid = 0m;
        var months = 0;

        while (balance > 0m && months < 100)
        {
            var breakdown = LoanCalculator.ApplyMonthlyPayment(balance, annualRatePct, payment);
            totalInterest += breakdown.InterestPortion;
            totalPaid += breakdown.ActualPayment;
            balance = breakdown.NewRemainingBalance;
            months++;

            if (breakdown.IsPaidOff)
            {
                break;
            }
        }

        // Closes exactly on the scheduled term - no pathological 37th month for a few pence.
        Assert.Equal(36, months);
        Assert.Equal(0m, balance);

        // The books balance exactly: every penny of interest and principal paid across the whole
        // life of the loan accounts for the entire principal plus every penny of interest charged -
        // nothing was created or destroyed by rounding.
        Assert.Equal(principal + totalInterest, totalPaid);
        Assert.Equal(469.25m, totalInterest);
        Assert.Equal(10_469.25m, totalPaid);
    }
}

namespace FSOps.Core.Finance;

/// <summary>Standard amortizing-loan maths - pure, no I/O.</summary>
public static class LoanCalculator
{
    public static decimal MonthlyPayment(decimal principal, double annualRatePct, int termMonths)
    {
        if (termMonths <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(termMonths), "Term must be at least one month.");
        }

        var monthlyRate = annualRatePct / 100.0 / 12.0;

        if (monthlyRate <= 0)
        {
            return Math.Round(principal / termMonths, 2, MidpointRounding.AwayFromZero);
        }

        var factor = monthlyRate / (1 - Math.Pow(1 + monthlyRate, -termMonths));
        var payment = (double)principal * factor;
        return Math.Round((decimal)payment, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Splits one level monthly payment into interest and principal against a loan's current
    /// remaining balance, and returns the resulting new balance - standard amortising-loan
    /// bookkeeping, used every billing period by EconomyClockService to keep
    /// <see cref="FSOps.Core.Entities.Loan.RemainingBalance"/> accurate rather than just
    /// decrementing it by the flat monthly payment (which would ignore interest entirely and drift
    /// the balance wrong over the loan's life). The final payment of a loan's term is clamped so
    /// the principal portion never exceeds what's left - a loan pays down to exactly zero, never
    /// negative.
    /// </summary>
    public static LoanPaymentBreakdown ApplyMonthlyPayment(decimal remainingBalance, double annualRatePct, decimal monthlyPayment)
    {
        if (remainingBalance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingBalance), "remainingBalance cannot be negative.");
        }

        var monthlyRate = annualRatePct / 100.0 / 12.0;
        var interestPortion = Math.Round(remainingBalance * (decimal)monthlyRate, 2, MidpointRounding.AwayFromZero);
        var principalPortion = monthlyPayment - interestPortion;

        // Guard against pathological configuration (a rate so high the payment doesn't even cover
        // interest) rather than let the balance grow - this method only ever pays a loan down.
        if (principalPortion < 0)
        {
            principalPortion = 0;
        }

        if (principalPortion > remainingBalance)
        {
            principalPortion = remainingBalance;
        }

        var newRemainingBalance = remainingBalance - principalPortion;
        var actualPayment = interestPortion + principalPortion;
        var isPaidOff = newRemainingBalance <= 0.01m;

        return new LoanPaymentBreakdown(interestPortion, principalPortion, actualPayment, isPaidOff ? 0m : newRemainingBalance, isPaidOff);
    }
}

/// <summary>One period's amortised payment - see <see cref="LoanCalculator.ApplyMonthlyPayment"/>.</summary>
public sealed record LoanPaymentBreakdown(
    decimal InterestPortion,
    decimal PrincipalPortion,
    decimal ActualPayment,
    decimal NewRemainingBalance,
    bool IsPaidOff);

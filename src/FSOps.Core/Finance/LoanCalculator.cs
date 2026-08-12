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
    /// negative - and the genuine tail payment absorbs whatever cent-rounding remainder is left
    /// rather than spilling into an extra period of its own (see the tail-absorption comment
    /// inline below).
    /// </summary>
    public static LoanPaymentBreakdown ApplyMonthlyPayment(decimal remainingBalance, double annualRatePct, decimal monthlyPayment)
    {
        if (remainingBalance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingBalance), "remainingBalance cannot be negative.");
        }

        var monthlyRate = annualRatePct / 100.0 / 12.0;
        var interestPortion = Math.Round(remainingBalance * (decimal)monthlyRate, 2, MidpointRounding.AwayFromZero);
        decimal principalPortion;

        if (IsFinalPeriod(remainingBalance, monthlyRate, monthlyPayment))
        {
            // The level payment is rounded to the nearest cent, so it will not, in general, amortise
            // a loan to exactly zero by its scheduled final period - a small remainder is typically
            // still owed even after what was meant to be the last instalment. Left alone, that
            // spills into a pathological extra period of its own (a 36-month loan that actually
            // takes 37 payments to close). This IS that final period (see IsFinalPeriod), so it
            // absorbs whatever is left, however much that turns out to be, rather than paying the
            // fixed level amount and leaving a remainder.
            principalPortion = remainingBalance;
        }
        else
        {
            principalPortion = monthlyPayment - interestPortion;

            // Guard against pathological configuration (a rate so high the payment doesn't even
            // cover interest) rather than let the balance grow - this method only ever pays a loan
            // down.
            if (principalPortion < 0)
            {
                principalPortion = 0;
            }

            if (principalPortion > remainingBalance)
            {
                principalPortion = remainingBalance;
            }
        }

        var newRemainingBalance = remainingBalance - principalPortion;
        var actualPayment = interestPortion + principalPortion;
        var isPaidOff = newRemainingBalance <= 0.01m;

        return new LoanPaymentBreakdown(interestPortion, principalPortion, actualPayment, isPaidOff ? 0m : newRemainingBalance, isPaidOff);
    }

    /// <summary>
    /// Whether paying <paramref name="monthlyPayment"/> against <paramref name="remainingBalance"/>
    /// this period is the loan's genuine last instalment - i.e. whether the standard "number of
    /// payments remaining" formula, evaluated fresh from THIS balance, resolves to (at most) one
    /// more period. Recomputed every period from current state alone, rather than tracking elapsed
    /// periods against the original term, so this works identically for a brand-new loan and one
    /// that is already partway through, and for <see cref="LoanSettlementCalculator"/>'s simulation
    /// as much as <c>EconomyClockService</c>'s real billing.
    /// <para>
    /// Rounded to the NEAREST whole period, not up - this is what makes the check robust against
    /// the very rounding drift it exists to fix. <paramref name="monthlyPayment"/> is rounded to the
    /// nearest cent when it is computed (<see cref="MonthlyPayment"/>), so evaluating the exact
    /// closed-form period count against it lands a hair off a whole number (e.g. 36.0003 when the
    /// payment was rounded down a fraction of a cent, or 35.9998 when rounded up) even on the
    /// loan's actual last period. Rounding UP would misread the first case as needing a pathological
    /// 37th period for a few pence - exactly the bug this exists to prevent. Rounding to nearest
    /// reads both as what they are: one period left.
    /// </para>
    /// </summary>
    private static bool IsFinalPeriod(decimal remainingBalance, double monthlyRate, decimal monthlyPayment)
    {
        if (remainingBalance <= 0m || monthlyPayment <= 0m)
        {
            return remainingBalance <= 0m;
        }

        double periodsRemaining;
        if (monthlyRate <= 0)
        {
            periodsRemaining = (double)(remainingBalance / monthlyPayment);
        }
        else
        {
            // Standard "number of payments" (NPER) formula, solved for n from the level-payment
            // amortisation identity. When the fixed payment doesn't even cover this period's
            // interest, the balance would never shrink under it at all (see the pathological-rate
            // guard in ApplyMonthlyPayment) - the formula is undefined there (log of a non-positive
            // number), so this reports "not the final period" and leaves that case to the normal,
            // unclamped path.
            var x = 1 - monthlyRate * (double)remainingBalance / (double)monthlyPayment;
            if (x <= 0)
            {
                return false;
            }

            periodsRemaining = -Math.Log(x) / Math.Log(1 + monthlyRate);
        }

        return Math.Round(periodsRemaining, MidpointRounding.AwayFromZero) <= 1;
    }
}

/// <summary>One period's amortised payment - see <see cref="LoanCalculator.ApplyMonthlyPayment"/>.</summary>
public sealed record LoanPaymentBreakdown(
    decimal InterestPortion,
    decimal PrincipalPortion,
    decimal ActualPayment,
    decimal NewRemainingBalance,
    bool IsPaidOff);

using FSOps.Core.Economy;

namespace FSOps.Core.Finance;

/// <summary>
/// Pure early-settlement and overpayment arithmetic for a <see cref="Entities.Loan"/>. Clearing
/// debt early to stop the interest is one of the more satisfying moves available, and it gives
/// surplus cash somewhere useful to go. Builds on
/// <see cref="LoanCalculator.ApplyMonthlyPayment"/>, the same amortisation step
/// <c>EconomyClockService</c> already runs every billing period, so a simulated "what if we kept
/// paying on schedule" projection can never disagree with what the monthly cycle would actually
/// charge.
/// </summary>
public static class LoanSettlementCalculator
{
    /// <summary>
    /// Upper bound on how many months <see cref="SimulateRemainingAmortization"/> will project
    /// forward - a defensive cap only. Every loan this app creates is bounded to a 360-month term at
    /// origination (see FleetEndpoints.TakeLoanAsync/GetLoanQuoteAsync), so remaining-term
    /// projections never come close to this; it exists purely so a pathological/corrupted row (a
    /// rate so low the payment barely covers interest) can't spin forever.
    /// </summary>
    private const int MaxSimulatedMonths = 1200;

    /// <summary>
    /// What paying off <paramref name="remainingBalance"/> right now costs, plus what it saves - see
    /// both the payoff figure and the interest saved are shown before the player confirms. The fee is
    /// the ONLY thing that makes early settlement cost anything when the loan was only just taken
    /// out (zero elapsed time means zero interest has accrued to "save" yet) - see
    /// <see cref="LoanEarlySettlementConfig"/>'s own doc on why that's exactly the point.
    /// </summary>
    public static LoanPayoffQuote ComputeFullPayoffQuote(
        decimal remainingBalance, double annualRatePct, decimal monthlyPayment, LoanEarlySettlementConfig config)
    {
        if (remainingBalance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingBalance), "remainingBalance cannot be negative.");
        }

        var (_, totalInterestRemaining) = SimulateRemainingAmortization(remainingBalance, annualRatePct, monthlyPayment);

        var earlySettlementFee = Math.Round(remainingBalance * config.FeePercentOfRemainingBalance, 2, MidpointRounding.AwayFromZero);
        var totalPayoff = remainingBalance + earlySettlementFee;

        return new LoanPayoffQuote(remainingBalance, earlySettlementFee, totalPayoff, totalInterestRemaining);
    }

    /// <summary>
    /// Applies a partial overpayment directly against principal - no fee (see
    /// <see cref="LoanEarlySettlementConfig"/>'s doc for why only a full settlement carries one).
    /// The monthly payment amount is left unchanged; the loan simply finishes sooner, since
    /// <see cref="LoanCalculator.ApplyMonthlyPayment"/> already clamps the final payment to whatever
    /// is left. Which of the two an overpayment does - shorten the term, or reduce the monthly
    /// payment - must be stated plainly and applied consistently, because ambiguity here reads as
    /// a bug. This app shortens the term.
    /// </summary>
    public static decimal ApplyOverpayment(decimal remainingBalance, decimal overpaymentAmount)
    {
        if (overpaymentAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(overpaymentAmount), "overpaymentAmount must be greater than zero.");
        }

        if (overpaymentAmount > remainingBalance)
        {
            throw new ArgumentOutOfRangeException(nameof(overpaymentAmount), "overpaymentAmount cannot exceed the remaining balance - use full settlement instead.");
        }

        return remainingBalance - overpaymentAmount;
    }

    /// <summary>
    /// Projects <see cref="LoanCalculator.ApplyMonthlyPayment"/> forward from
    /// <paramref name="remainingBalance"/> until paid off, assuming the level
    /// <paramref name="monthlyPayment"/> continues unchanged - used both for the payoff quote's
    /// "interest saved" figure and for reporting a loan's remaining term on the Finances page.
    /// </summary>
    public static (int RemainingTermMonths, decimal TotalInterestRemaining) SimulateRemainingAmortization(
        decimal remainingBalance, double annualRatePct, decimal monthlyPayment)
    {
        var balance = remainingBalance;
        var totalInterest = 0m;
        var months = 0;

        while (balance > 0.01m && months < MaxSimulatedMonths)
        {
            var breakdown = LoanCalculator.ApplyMonthlyPayment(balance, annualRatePct, monthlyPayment);
            totalInterest += breakdown.InterestPortion;
            balance = breakdown.NewRemainingBalance;
            months++;

            if (breakdown.ActualPayment == 0m)
            {
                // Pathological configuration (the payment doesn't even cover interest) - bail
                // rather than loop for MaxSimulatedMonths doing nothing.
                break;
            }
        }

        return (months, Math.Round(totalInterest, 2, MidpointRounding.AwayFromZero));
    }
}

/// <summary>Result of <see cref="LoanSettlementCalculator.ComputeFullPayoffQuote"/>.</summary>
public sealed record LoanPayoffQuote(
    decimal RemainingBalance,
    decimal EarlySettlementFee,
    decimal TotalPayoff,
    decimal InterestSaved);

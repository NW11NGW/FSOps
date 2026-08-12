using FSOps.Core.Economy;

namespace FSOps.Core.Finance;

/// <summary>
/// Computes the ONLY interest rate a loan is ever priced at. Interest is set by the simulation and
/// never by the player: a rate they control can be set to zero, which makes borrowing free and
/// turns loans from a strategic trade-off into an exploit. The player never supplies a rate; every loan entry
/// point (AirlineEndpoints.CreateAsync's starting loan, FleetEndpoints.TakeLoanAsync's mid-game
/// loan) calls this instead of trusting anything arriving on the request, so a request-supplied
/// rate can never reach a <see cref="FSOps.Core.Entities.Loan"/> row.
/// <para>
/// <b>Risk-based pricing.</b> The rate rises with how much of the airline's borrowing capacity
/// (<see cref="LoanEligibilityCalculator.MaxMonthlyPayment"/>) the loan consumes. "How much" is
/// measured by pricing the requested principal/term at the playstyle's own BASE rate first - this
/// sidesteps a circular definition (the final rate cannot depend on a payment computed at the final
/// rate) while still giving a stable, monotonic proxy for loan size relative to capacity: a bigger
/// principal or a shorter term produces a bigger base-rate payment, which consumes more of the
/// capacity band, which prices in more risk premium. The consumed-capacity proportion is clamped to
/// [0,1] before it scales the gap between base and cap, so the result always lands in
/// [<see cref="LoanConfig.BaseAnnualRatePct"/>, <see cref="LoanConfig.CapAnnualRatePct"/>] by
/// construction - the playstyle's hard cap (5% Casual / 8% True-life) can never be
/// exceeded regardless of how large a principal or how thin the airline's cash flow is. See
/// LoanRateCalculatorTests' boundary tests, which assert this at and beyond full capacity.
/// </para>
/// <para>
/// <b>Zero cash flow is not a special case in the formula</b> - it falls out of the same maths. A
/// brand-new airline (AirlineEndpoints.CreateAsync's starting loan) has no trading history yet, so
/// its trailing 30-day net operating cash flow is always exactly 0 at the point the starting loan
/// is priced (the airline and its ledger do not exist until the same request creates them). Zero
/// capacity means any positive principal consumes the whole band, so a starting loan is always
/// priced at the playstyle's cap rate - the correct outcome for an unproven borrower with no track
/// record, not a bug.
/// </para>
/// </summary>
public static class LoanRateCalculator
{
    public static double ComputeAnnualRatePct(
        decimal principal, int termMonths, decimal trailing30DayNetOperatingCashFlow, LoanConfig rateConfig)
    {
        if (rateConfig.CapAnnualRatePct <= rateConfig.BaseAnnualRatePct)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rateConfig), "CapAnnualRatePct must be greater than BaseAnnualRatePct - this should already have been caught by EconomyConfig.Validate().");
        }

        // A non-positive principal or term can't be priced (and both are rejected upstream by the
        // endpoints' own validation before this is ever called) - the base rate is a harmless,
        // well-defined fallback rather than dividing by zero or throwing here too.
        if (principal <= 0 || termMonths <= 0)
        {
            return rateConfig.BaseAnnualRatePct;
        }

        var maxMonthlyPayment = LoanEligibilityCalculator.MaxMonthlyPayment(trailing30DayNetOperatingCashFlow);
        var paymentAtBaseRate = LoanCalculator.MonthlyPayment(principal, rateConfig.BaseAnnualRatePct, termMonths);

        var proportionOfCapacity = maxMonthlyPayment > 0m
            ? (double)(paymentAtBaseRate / maxMonthlyPayment)
            : (paymentAtBaseRate > 0m ? 1.0 : 0.0);
        proportionOfCapacity = Math.Clamp(proportionOfCapacity, 0.0, 1.0);

        var rate = rateConfig.BaseAnnualRatePct + (rateConfig.CapAnnualRatePct - rateConfig.BaseAnnualRatePct) * proportionOfCapacity;

        // Belt-and-braces: the formula above is already bounded by construction (the proportion is
        // clamped to [0,1] before it scales the base-to-cap gap), but the cap is a hard product
        // requirement asserted by a dedicated boundary test, not just inferred from the formula's
        // shape - so it is enforced explicitly here too rather than trusted to the arithmetic alone.
        return Math.Clamp(rate, rateConfig.BaseAnnualRatePct, rateConfig.CapAnnualRatePct);
    }
}

namespace FSOps.Core.Finance;

/// <summary>
/// Sane borrowing limits for a mid-game loan. Borrowing capacity is tied to cash flow rather than
/// being unlimited, so a loan accelerates progression rather than trivialising it - the whole point
/// of leverage here is a real trade-off, not a shortcut past the second-aircraft milestone.
/// <para>
/// <b>Chosen basis: trailing 30-day net operating cash flow, not fleet value.</b> Fleet value can't
/// work for the moment this feature actually targets - a brand-new airline leases its only aircraft
/// (see AirlineEndpoints.CreateAsync), so it owns nothing to use as collateral until well after this
/// feature would already need to matter. Cash flow is also the standard real-world basis for an
/// unsecured/character loan to a young, asset-light operator, and it scales naturally as the airline
/// grows: more aircraft and pilots (Chunk E2) mean more revenue, which means more borrowing capacity,
/// which is exactly the intended shape - debt service capacity should grow with the airline, not be
/// unlocked once and then ignored.
/// </para>
/// <para>
/// <b>30% of trailing 30-day net operating cash flow</b> is a standard debt-service-to-income
/// affordability heuristic (comparable to a real lender's DTI cap). "Net operating" deliberately
/// excludes <c>LedgerCategory.StartingCapital</c> and <c>LedgerCategory.LoanProceeds</c> - both are
/// one-off cash injections, not repeatable income, and counting either would let a brand-new airline
/// borrow against its own starting capital, or let one loan's proceeds be used to justify taking out
/// a second, larger one on the same day. Only genuine trading cash flow counts.
/// </para>
/// <para>
/// Under Casual's current balance (~£45,485/month net for one aircraft flown ~1 leg/day, against
/// ~£45,000 of fixed monthly cost per aircraft), this caps a solo single-aircraft
/// airline's serviceable new debt at roughly £15,900/month, which (at a moderate rate over a long
/// term) affords on the order of £1-2m of principal - nowhere near even a discounted used airframe
/// (tens of millions; see AircraftTypeSeeder's realistic PurchasePrice figures, which are
/// deliberately NOT cut for game balance). That is intentional, not a bug: a loan alone cannot buy a
/// single casual player an aircraft on day one. It becomes meaningful once the airline's revenue has
/// scaled up (more aircraft and pilots flying), at which point it genuinely accelerates a purchase
/// that would otherwise take years of retained profit to reach - "accelerates progression" rather
/// than "trivialises it", exactly as the plan asks for.
/// </para>
/// </summary>
public static class LoanEligibilityCalculator
{
    public const double MaxDebtServiceFraction = 0.30;

    /// <summary>The most new monthly debt service the airline may take on, given its trailing
    /// 30-day net operating cash flow (see the class doc for what "net operating" excludes). Never
    /// negative - a loss-making trailing period allows no new borrowing at all.</summary>
    public static decimal MaxMonthlyPayment(decimal trailing30DayNetOperatingCashFlow)
        => Math.Max(0m, trailing30DayNetOperatingCashFlow) * (decimal)MaxDebtServiceFraction;

    /// <summary>Whether a proposed loan's level monthly payment fits within the airline's current
    /// borrowing capacity. Returns the computed payment either way so the caller can report it
    /// (accepted or not) without a second calculation.</summary>
    public static LoanEligibilityResult Evaluate(
        decimal principal, double annualRatePct, int termMonths, decimal trailing30DayNetOperatingCashFlow)
    {
        var monthlyPayment = LoanCalculator.MonthlyPayment(principal, annualRatePct, termMonths);
        var maxMonthlyPayment = MaxMonthlyPayment(trailing30DayNetOperatingCashFlow);
        return new LoanEligibilityResult(monthlyPayment <= maxMonthlyPayment, monthlyPayment, maxMonthlyPayment);
    }
}

/// <summary>Result of <see cref="LoanEligibilityCalculator.Evaluate"/> - both figures are always
/// populated so a rejected request can still tell the player exactly what they asked for and what
/// they're allowed.</summary>
public sealed record LoanEligibilityResult(bool IsEligible, decimal MonthlyPayment, decimal MaxMonthlyPayment);

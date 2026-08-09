using FSOps.Core.Entities;

namespace FSOps.Core.Finance;

/// <summary>
/// Pure fixed-vs-variable classification of a <see cref="LedgerCategory"/> - see docs/PLAN.md "Full
/// breakdown of airline costs": "Split fixed (leases, salaries, insurance, loan repayments) from
/// variable (fuel, landing, handling, parking, passenger charges, turnaround, maintenance, crew),
/// because they behave completely differently - one is owed whether or not you fly."
/// <para>
/// Classification is by <see cref="LedgerCategory"/> alone, never by <see cref="LedgerTransaction.Description"/>
/// text - a category is a stable, typed fact; description wording can change at any time without
/// being a breaking change to anything, so building financial categorisation on it would silently
/// misreport the one page whose entire job is being accurate. <see cref="LedgerCategory.CrewCost"/>,
/// <see cref="LedgerCategory.ParkingFees"/>, <see cref="LedgerCategory.PassengerCharges"/> and
/// <see cref="LedgerCategory.TurnaroundFees"/> exist specifically so this split can be built this
/// way - before they were added, per-sector crew cost was indistinguishable from the monthly wage
/// (both <see cref="LedgerCategory.Salary"/>), and parking/passenger/turnaround were indistinguishable
/// from the handling fee itself (all <see cref="LedgerCategory.Handling"/>).
/// </para>
/// <para>
/// <b>Rows posted before that split existed</b> still carry the old bundled categories - the ledger
/// is append-only and history is never rewritten. This is harmless for <see cref="LedgerCategory.Handling"/>:
/// every sub-fee it used to bundle (handling/parking/passenger/turnaround) is variable regardless of
/// era, so an old bundled row is still classified correctly as variable, just not split further by
/// sub-type. It is NOT harmless for <see cref="LedgerCategory.Salary"/>: an old row could be either
/// the (fixed) monthly wage or the (variable) per-sector crew cost, and the category alone can no
/// longer tell them apart. This classifier resolves that by treating every <see cref="LedgerCategory.Salary"/>
/// row as fixed - correct for 100% of rows posted after the split, and the same "counts as the
/// wage line" choice a reasonable person would make for a row that predates crew cost having its
/// own category at all. The caller (FinanceEndpoints) is responsible for surfacing a caveat when
/// pre-split data is present, so this potential undercount of variable cost is disclosed rather than
/// silent - see FinanceEndpoints.CountLegacySalaryRows, which finds these rows by an exact
/// structural discriminator (Category == Salary with a non-null FlightId - a legacy per-sector
/// crew-cost row - versus the monthly wage, which is always airline-level with no FlightId), not by
/// description text.
/// </para>
/// </summary>
public static class LedgerCostClassifier
{
    public static bool IsFixedCost(LedgerCategory category) => category switch
    {
        LedgerCategory.LeasePayment => true,
        LedgerCategory.Salary => true,
        LedgerCategory.Insurance => true,
        LedgerCategory.LoanPayment => true,
        _ => false,
    };

    public static bool IsVariableCost(LedgerCategory category) => category switch
    {
        LedgerCategory.Fuel => true,
        LedgerCategory.LandingFees => true,
        LedgerCategory.Handling => true,
        LedgerCategory.ParkingFees => true,
        LedgerCategory.PassengerCharges => true,
        LedgerCategory.TurnaroundFees => true,
        LedgerCategory.Maintenance => true,
        LedgerCategory.CrewCost => true,
        LedgerCategory.CancellationFee => true,
        LedgerCategory.GsxServices => true,
        _ => false,
    };

    /// <summary>Neither fixed nor variable operating cost - a capital/one-off transaction
    /// (aircraft purchase or sale, loan proceeds, starting capital) or ticket revenue. Excluded from
    /// both buckets so the fixed/variable split only ever describes running costs, matching the
    /// plan's own framing ("owed whether or not you fly" only makes sense for operating costs).</summary>
    public static bool IsCapitalOrRevenue(LedgerCategory category) =>
        !IsFixedCost(category) && !IsVariableCost(category);
}

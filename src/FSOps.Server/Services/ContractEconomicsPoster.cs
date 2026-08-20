using FSOps.Core.Contracts;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>What posting a contract leg did, for the caller's own logging and DTOs.</summary>
/// <param name="FeePosted">The amount actually posted. Zero when the sector was not payable.</param>
/// <param name="ContractCompleted">True if this leg was the last one outstanding.</param>
public sealed record ContractLegPostResult(decimal FeePosted, bool ContractCompleted);

/// <summary>
/// The only place contract money becomes real, append-only <see cref="LedgerTransaction"/> rows.
///
/// <para><b>A contract flight posts the fee and nothing else.</b> No fuel, no landing fee, no
/// handling, no parking, no passenger charges, no turnaround, no maintenance accrual, no crew cost.
/// The player is flying somebody else's aeroplane and every one of those costs belongs to the
/// business that offered the job - that is the whole arrangement, and it is what makes contract
/// flying worth doing at all.</para>
///
/// <para><b>And that is structural, not a rule this class remembers to follow.</b> The code that
/// posts those other lines lives in <see cref="FlightEconomicsPoster"/> and is only reached from the
/// airline branch of <c>FlightLifecycleService.FinalizeFlightAsync</c>, which a contract flight never
/// enters. There is no path from here to any of it. This class could not post a landing fee if it
/// tried.</para>
///
/// <para><b>Pay is per leg actually flown.</b> Fly three of five and stop, and the three are yours -
/// each was posted as it landed, from the share stamped on it when the contract was generated. There
/// is no lump sum at the end to lose, and no running tally that could disagree with the ledger.</para>
/// </summary>
public static class ContractEconomicsPoster
{
    /// <summary>
    /// Posts one flown leg's fee and advances the contract. Idempotent on
    /// <see cref="Flight.RevenuePosted"/>, exactly like the airline path: a retry, a reconnect or a
    /// crash rehydration that finalises the same flight twice posts once.
    /// </summary>
    public static async Task<ContractLegPostResult> PostLegCompletionAsync(
        FsOpsDbContext db,
        Flight flight,
        Contract contract,
        ContractLeg leg,
        DateTimeOffset utc,
        CancellationToken ct)
    {
        if (flight.RevenuePosted)
        {
            return new ContractLegPostResult(0m, contract.Status == ContractStatus.Completed);
        }

        // The same structural gate the airline path applies, for the same reason and with the same
        // force: teleporting the aircraft is unambiguous, and a sector flown that way is not paid
        // for. Unlike an airline sector, there is not even a fuel line left behind - a contract
        // flight has no costs of its own - so an invalid contract sector posts literally nothing.
        var payable = !(flight.SlewDetected || flight.PositionJumpDetected);

        // Marked flown either way. The leg WAS flown, whatever the integrity monitor thinks of how,
        // and leaving it outstanding would mean the player could never finish the contract and would
        // eventually be charged for abandoning it - punishing them twice for one refusal.
        leg.FlightId = flight.Id;
        leg.FlownUtc = utc;

        var fee = 0m;
        if (payable && leg.FeeShare > 0)
        {
            fee = leg.FeeShare;
            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = flight.AirlineId,
                Utc = utc,
                Category = LedgerCategory.ContractFee,
                Amount = fee,
                FlightId = flight.Id,
                Description =
                    $"Contract fee: {contract.OperatorName}, {leg.DepartureIcao}-{leg.ArrivalIcao} " +
                    $"(leg {leg.Sequence} of {await LegCountAsync(db, contract.Id, ct)})",
            });

            flight.Revenue += fee;
        }

        // TotalCost stays at zero, and that is the assertion rather than an omission: a contract
        // sector genuinely costs the player nothing.
        var outstanding = await db.ContractLegs
            .Where(l => l.ContractId == contract.Id && l.FlightId == null && l.Id != leg.Id)
            .CountAsync(ct);

        var completed = outstanding == 0;
        if (completed)
        {
            contract.Status = ContractStatus.Completed;
            contract.ClosedUtc = utc;
        }

        flight.RevenuePosted = true;
        return new ContractLegPostResult(fee, completed);
    }

    /// <summary>
    /// Raises the single charge for walking away from an accepted contract with legs outstanding,
    /// and closes it. Returns the charge actually posted, which is legitimately zero when nothing was
    /// ever flown - see
    /// <see cref="ContractPayCalculator.CalculateAbandonCharge"/> for why handing an untouched job
    /// back is free.
    /// </summary>
    public static async Task<ContractAbandonCharge> PostAbandonAsync(
        FsOpsDbContext db,
        Contract contract,
        ContractConfig config,
        DateTimeOffset utc,
        string reason,
        CancellationToken ct)
    {
        var legs = await db.ContractLegs.Where(l => l.ContractId == contract.Id).ToListAsync(ct);
        var flown = legs.Where(l => l.FlightId is not null).ToList();
        var unflown = legs.Where(l => l.FlightId is null).ToList();

        var charge = ContractPayCalculator.CalculateAbandonCharge(
            config,
            flown.Select(l => l.PlannedBlockMinutes).ToList(),
            unflown.Select(l => l.FeeShare).ToList(),
            unflown.Select(l => l.PlannedBlockMinutes).ToList());

        if (charge.Charge > 0)
        {
            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = contract.AirlineId,
                Utc = utc,
                Category = LedgerCategory.ContractFee,
                // Negative, in the same category the fees were paid into, so "what did contract
                // flying do to my balance" stays a single sum rather than two that have to be
                // remembered together.
                Amount = -charge.Charge,
                FlightId = null,
                Description = $"Contract abandoned: {contract.OperatorName} - {charge.Reason}",
            });
        }

        contract.Status = ContractStatus.Abandoned;
        contract.ClosedUtc = utc;
        contract.ClosedReason = reason;

        return charge;
    }

    private static Task<int> LegCountAsync(FsOpsDbContext db, Guid contractId, CancellationToken ct) =>
        db.ContractLegs.CountAsync(l => l.ContractId == contractId, ct);
}

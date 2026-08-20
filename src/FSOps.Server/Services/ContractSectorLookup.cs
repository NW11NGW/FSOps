using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>
/// Everything a screen needs to describe a contract sector, for one <see cref="ContractLeg"/>.
/// The shape deliberately mirrors what a route gives an airline sector - two airports and a label -
/// so a consumer can render either kind of flight without a second code path.
/// </summary>
/// <param name="Sequence">Which leg of the job this was, 1-based.</param>
/// <param name="LegCount">How many legs the whole job has, so a screen can say "leg 3 of 5".</param>
/// <param name="AircraftName">
/// The aeroplane as a person would say it. Null if the contract names a designator the catalogue no
/// longer carries, which is survivable: the designator itself is still shown.
/// </param>
public sealed record ContractSectorInfo(
    Guid ContractId,
    Guid ContractLegId,
    ContractKind Kind,
    ContractStatus ContractStatus,
    string OperatorName,
    string AircraftTypeDesignator,
    string? AircraftName,
    int Sequence,
    int LegCount,
    string DepartureIcao,
    string ArrivalIcao,
    decimal FeeShare);

/// <summary>
/// Resolves contract sectors for a page of flights in a fixed number of queries, whatever the row
/// count - the same discipline the logbook already keeps for routes, aircraft and ledger lines.
///
/// <para>Shared rather than reimplemented per endpoint because more than one screen now has to
/// answer "where did this sector go?" for a flight that has no route: the logbook, the report card
/// and the VATSIM history all do. A contract flight showing as a blank origin and a blank
/// destination is the shape of bug that reads as data loss.</para>
/// </summary>
public static class ContractSectorLookup
{
    /// <summary>
    /// Keyed by <see cref="ContractLeg.Id"/> - which is what <see cref="Flight.ContractLegId"/>
    /// holds. Returns an empty dictionary for an empty request without touching the database.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, ContractSectorInfo>> ByLegIdAsync(
        FsOpsDbContext db, IReadOnlyCollection<Guid> contractLegIds, CancellationToken ct)
    {
        if (contractLegIds.Count == 0)
        {
            return new Dictionary<Guid, ContractSectorInfo>();
        }

        var ids = contractLegIds.Distinct().ToList();
        var legs = await db.ContractLegs.Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        if (legs.Count == 0)
        {
            return new Dictionary<Guid, ContractSectorInfo>();
        }

        var contractIds = legs.Select(l => l.ContractId).Distinct().ToList();
        var contracts = await db.Contracts.Where(c => contractIds.Contains(c.Id)).ToListAsync(ct);
        var contractsById = contracts.ToDictionary(c => c.Id);

        // One grouped count rather than loading every sibling leg: "leg 3 of 5" needs the 5, not the
        // other four rows.
        var legCounts = (await db.ContractLegs
                .Where(l => contractIds.Contains(l.ContractId))
                .GroupBy(l => l.ContractId)
                .Select(g => new { ContractId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.ContractId, x => x.Count);

        var result = new Dictionary<Guid, ContractSectorInfo>();
        foreach (var leg in legs)
        {
            if (!contractsById.TryGetValue(leg.ContractId, out var contract))
            {
                continue;
            }

            result[leg.Id] = new ContractSectorInfo(
                contract.Id,
                leg.Id,
                contract.Kind,
                contract.Status,
                contract.OperatorName,
                contract.AircraftTypeDesignator,
                ContractAircraftCatalogue.Find(contract.AircraftTypeDesignator)?.Name,
                leg.Sequence,
                legCounts.GetValueOrDefault(leg.ContractId, 1),
                leg.DepartureIcao,
                leg.ArrivalIcao,
                leg.FeeShare);
        }

        return result;
    }

    /// <summary>The contract-leg ids among a page of flights, ready to hand to <see cref="ByLegIdAsync"/>.</summary>
    public static List<Guid> LegIdsOf(IEnumerable<Flight> flights) =>
        flights.Where(f => f.ContractLegId is not null).Select(f => f.ContractLegId!.Value).Distinct().ToList();

    /// <summary>
    /// What each leg has <b>actually been paid</b>, keyed by <see cref="ContractLeg.Id"/>, summed from
    /// the posted <see cref="LedgerCategory.ContractFee"/> rows.
    ///
    /// <para><b>The ledger is the only source of truth for money here, and it has to be.</b> A leg's
    /// stamped <see cref="ContractLeg.FeeShare"/> is what it <i>would</i> pay, not what it did:
    /// completing a leg with estimates marks it flown and pays nothing, and so does a sector
    /// invalidated by slew or a position jump. Summing the shares of flown legs therefore reported
    /// money the player never received - a screen reading "Earned so far $2,009.53" over a cash
    /// balance that had not moved.</para>
    ///
    /// <para>Only credits are counted. The abandon charge shares the same category but is raised
    /// against the airline with no <see cref="LedgerTransaction.FlightId"/>, so it cannot be picked up
    /// here anyway - the flight-id filter is what keeps "what this job has paid me" from quietly
    /// netting off "what handing it back cost me".</para>
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, decimal>> PostedFeeByLegIdAsync(
        FsOpsDbContext db, IEnumerable<ContractLeg> legs, CancellationToken ct)
    {
        // Only legs that produced a flight can have been paid for anything.
        var flownLegs = legs.Where(l => l.FlightId is not null).ToList();
        if (flownLegs.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var flightIds = flownLegs.Select(l => l.FlightId!.Value).Distinct().ToList();

        // Materialised before summing: the SQLite provider cannot translate SumAsync over decimal.
        var rows = await db.LedgerTransactions
            .Where(t => t.Category == LedgerCategory.ContractFee
                        && t.Amount > 0
                        && t.FlightId != null
                        && flightIds.Contains(t.FlightId.Value))
            .ToListAsync(ct);

        var paidByFlightId = rows
            .GroupBy(t => t.FlightId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));

        var result = new Dictionary<Guid, decimal>();
        foreach (var leg in flownLegs)
        {
            // Zero is a real answer, and the one that matters: a leg that flew and paid nothing.
            result[leg.Id] = paidByFlightId.GetValueOrDefault(leg.FlightId!.Value, 0m);
        }

        return result;
    }
}

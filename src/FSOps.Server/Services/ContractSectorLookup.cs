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
}

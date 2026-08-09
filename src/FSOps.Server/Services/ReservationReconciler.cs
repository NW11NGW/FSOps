using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSOps.Server.Services;

/// <summary>
/// One-time-per-boot repair for a contradiction the OLD soft-preference reservation model could
/// produce: a <see cref="FleetAircraft"/> with <see cref="FleetAircraft.ReservedForPlayer"/> true
/// that ALSO has active <see cref="PilotScheduleEntry"/> rows referencing it - see docs/PLAN.md
/// "3a" and the reservation hard-invariant work (2026-08-09). Before 3a, the old
/// <c>FleetEndpoints.SetReservationAsync</c> set the flag with no check against an aircraft's
/// existing schedule, and <c>PilotScheduleValidator</c> only ever rejected saving NEW legs onto an
/// aircraft reserved AT THAT MOMENT - neither stopped a player from building a schedule on an
/// unreserved aircraft and reserving it afterward. A database written under that regime can carry
/// this contradiction forward indefinitely; nothing before today ever re-checked it.
///
/// <para><b>Why this cannot wait for the player to notice.</b>
/// <see cref="VirtualFlightResolverService"/> never consults <c>ReservedForPlayer</c> at all - it
/// resolves every <see cref="PilotScheduleEntry"/> row it finds, and its first pass runs
/// synchronously the moment the host starts, before the player does anything.
/// <c>FlightEndpoints.OptionsAsync</c>/<c>StartAsync</c> never consult
/// <see cref="PilotScheduleEntry"/> - they offer any <c>ReservedForPlayer</c> aircraft to the
/// player regardless of what else claims it. Left unreconciled, both actors would claim the same
/// airframe on the very first boot after this ships - exactly what the reservation invariant exists
/// to make unrepresentable. This is why reconciliation must run after the schema migration and
/// strictly before any hosted service starts (see the call site in <c>Program.cs</c>).</para>
///
/// <para><b>Resolution: the schedule wins, always.</b> A saved weekly schedule is many deliberate,
/// individually-validated decisions (routes, times, an aircraft chosen for the day);
/// <c>ReservedForPlayer</c> is one click, and easy to have drifted under the old soft-preference
/// regime. So a contradictory aircraft is RELEASED (<c>ReservedForPlayer</c> set false) - its
/// schedule is never touched. This mirrors <c>FleetEndpoints.SetReservationAsync</c>'s own new rule
/// going forward (reserving an aircraft with active legs is refused unless the player explicitly
/// confirms clearing them); this reconciler applies the same preference retroactively, without a
/// player there to confirm anything, because silently deleting a schedule they built is not this
/// code's decision to make.</para>
///
/// <para><b>The zero-reserved case.</b> Releasing can leave an airline with no reserved aircraft at
/// all - e.g. a single-aircraft airline, or the only reserved airframe was the contradictory one.
/// Auto-reserving ANY replacement is unsafe: reserving one that ALSO has scheduled legs would
/// recreate the exact contradiction just fixed. So: if a release leaves an airline with zero
/// reserved aircraft, this reserves one aircraft that has NO scheduled legs at all, preferring
/// whichever is parked at the airline's home base then the oldest - the same fallback ordering the
/// E2 migration (<c>20260808203636_AddVirtualPilotScheduling</c>) used for its own initial backfill.
/// If every aircraft in the fleet has legs, the airline is left at zero deliberately - the only
/// alternatives are recreating the bug or silently deleting a schedule, and neither belongs to this
/// code. That case is recorded in <see cref="ReservationReconciliationResult.AirlinesLeftWithNoReservedAircraft"/>
/// so whoever owns player-facing surfacing (a "while you were away" summary, a Fleet-page banner)
/// can make sure the player actually sees it rather than opening the app to find nothing flyable
/// with no explanation.</para>
///
/// <para>Idempotent by construction: a database with no contradiction produces an empty result and
/// writes nothing. Runs on EVERY startup (not once, like a migration) so it also stands as a
/// permanent safety net against any other path to this contradiction, not just the one known cause
/// above.</para>
/// </summary>
public static class ReservationReconciler
{
    public static async Task<ReservationReconciliationResult> ReconcileAsync(FsOpsDbContext db, ILogger? logger = null, CancellationToken ct = default)
    {
        var fleet = await db.FleetAircraft.ToListAsync(ct);
        var entries = await db.PilotScheduleEntries.ToListAsync(ct);
        var legCountByAircraftId = entries
            .GroupBy(e => e.FleetAircraftId)
            .ToDictionary(g => g.Key, g => g.Count());

        var released = new List<ReleasedAircraft>();
        foreach (var aircraft in fleet)
        {
            if (!aircraft.ReservedForPlayer)
            {
                continue;
            }

            if (!legCountByAircraftId.TryGetValue(aircraft.Id, out var legCount) || legCount == 0)
            {
                continue;
            }

            aircraft.ReservedForPlayer = false;
            released.Add(new ReleasedAircraft(aircraft.Id, aircraft.Registration, aircraft.AirlineId, legCount));
            logger?.LogWarning(
                "Reservation reconciliation: released {Registration} (airline {AirlineId}) - it was marked reserved for the player but has {LegCount} active scheduled leg(s). The schedule was kept; only the reservation flag changed.",
                aircraft.Registration, aircraft.AirlineId, legCount);
        }

        var fallbackReserved = new List<FallbackReservedAircraft>();
        var leftAtZero = new List<Guid>();

        if (released.Count > 0)
        {
            // Only airlines that just lost a reservation could possibly now have zero - no need to
            // re-check every airline in the database.
            var affectedAirlineIds = released.Select(r => r.AirlineId).Distinct().ToList();
            var airlinesById = await db.Airlines.Where(a => affectedAirlineIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

            foreach (var airlineId in affectedAirlineIds)
            {
                var airlineFleet = fleet.Where(f => f.AirlineId == airlineId).ToList();
                if (airlineFleet.Any(f => f.ReservedForPlayer))
                {
                    // Still has (or already has, from a different aircraft untouched by the release
                    // above) a reserved airframe - releasing the contradictory one didn't zero this
                    // airline out.
                    continue;
                }

                airlinesById.TryGetValue(airlineId, out var airline);

                // Never a candidate with scheduled legs - see the class doc's "zero-reserved case"
                // for why that would just recreate the bug.
                var candidate = airlineFleet
                    .Where(f => !legCountByAircraftId.TryGetValue(f.Id, out var legs) || legs == 0)
                    .OrderBy(f => airline is not null && string.Equals(f.LocationIcao, airline.HomeAirportIcao, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(f => f.CreatedUtc)
                    .FirstOrDefault();

                if (candidate is not null)
                {
                    candidate.ReservedForPlayer = true;
                    fallbackReserved.Add(new FallbackReservedAircraft(candidate.Id, candidate.Registration, airlineId));
                    logger?.LogWarning(
                        "Reservation reconciliation: reserved {Registration} for the player (airline {AirlineId}) - releasing the contradictory aircraft above left this airline with no reserved aircraft, and this one has no scheduled legs of its own.",
                        candidate.Registration, airlineId);
                }
                else
                {
                    leftAtZero.Add(airlineId);
                    logger?.LogError(
                        "Reservation reconciliation: airline {AirlineId} has NO reserved aircraft after reconciliation - every aircraft in its fleet has scheduled legs, so none could be reserved without recreating the same contradiction. The player must choose one explicitly.",
                        airlineId);
                }
            }
        }

        if (released.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return new ReservationReconciliationResult(released, fallbackReserved, leftAtZero);
    }
}

/// <summary>Full, structured outcome of one <see cref="ReservationReconciler.ReconcileAsync"/> pass
/// - deliberately not just a count, so whoever builds the player-facing surface (e.g. a "while you
/// were away" summary) has everything needed to say exactly what happened, per aircraft, without a
/// second query.</summary>
public sealed record ReservationReconciliationResult(
    IReadOnlyList<ReleasedAircraft> Released,
    IReadOnlyList<FallbackReservedAircraft> FallbackReserved,
    IReadOnlyList<Guid> AirlinesLeftWithNoReservedAircraft)
{
    public static ReservationReconciliationResult Empty { get; } =
        new(Array.Empty<ReleasedAircraft>(), Array.Empty<FallbackReservedAircraft>(), Array.Empty<Guid>());

    /// <summary>True if this pass found and fixed anything at all - lets a caller skip surfacing an
    /// empty "nothing happened" notice.</summary>
    public bool HasFindings => Released.Count > 0;
}

/// <summary>One aircraft whose contradictory <see cref="FleetAircraft.ReservedForPlayer"/> = true
/// was released because it had active scheduled legs - the schedule was kept, only the flag
/// changed.</summary>
public sealed record ReleasedAircraft(Guid FleetAircraftId, string Registration, Guid AirlineId, int ScheduledLegCount);

/// <summary>One aircraft auto-reserved to replace a release above, because its airline would
/// otherwise have been left with none - chosen because it has no scheduled legs of its own.</summary>
public sealed record FallbackReservedAircraft(Guid FleetAircraftId, string Registration, Guid AirlineId);

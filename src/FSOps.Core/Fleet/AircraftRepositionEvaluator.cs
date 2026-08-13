namespace FSOps.Core.Fleet;

/// <summary>
/// Why a repositioning move is refused, or <see cref="None"/> when it is allowed. Declared in the
/// order the checks run in - see <see cref="AircraftRepositionEvaluator.Evaluate"/>'s own doc for
/// why that order is deliberate rather than incidental.
/// </summary>
public enum RepositionRefusal
{
    None,

    /// <summary>The aircraft is airborne on a tracked sector. Physically impossible, and also
    /// pointless state-wise: a completing flight writes its own arrival into
    /// <see cref="Entities.FleetAircraft.LocationIcao"/> afterwards, so a move landed mid-flight
    /// would simply be overwritten and the money spent for nothing.</summary>
    InFlight,

    /// <summary>Grounded for a maintenance check. An aircraft that cannot be flown should not be
    /// teleportable either - the same rule the Fly screen already enforces
    /// (FlightEndpoints.OptionsAsync), reused rather than reinvented.</summary>
    GroundedForMaintenance,

    /// <summary>
    /// The aircraft is not <see cref="Entities.FleetAircraft.ReservedForPlayer"/>. Repositioning is
    /// a player-only action (user's decision, 2026-08-13): an aircraft available to virtual pilots
    /// is theirs to fly, and moving it out from under them is not the player's to do without first
    /// taking it back. Reservation is already the app's single gate for "this airframe is the
    /// human's, not the schedule's" (see FleetAircraft.ReservedForPlayer's own doc), so this reuses
    /// it rather than inventing a second notion of ownership.
    /// </summary>
    NotReservedForPlayer,

    /// <summary>The airline has no routes at all, so there is nowhere a reposition could legally
    /// send the aircraft - destinations are restricted to airports the airline already serves.</summary>
    NoRoutesAtAll,

    /// <summary>The airline's routes only ever touch the airport the aircraft is already parked at,
    /// so there is nowhere else to go.</summary>
    NowhereElseToGo,

    /// <summary>A destination was named, but it is where the aircraft already is.</summary>
    AlreadyThere,

    /// <summary>A destination was named, but the airline has no route to or from it. Destinations
    /// are deliberately restricted to the airline's own network rather than every airport in the
    /// world.</summary>
    DestinationNotServed,

    /// <summary>The move costs more than the airline's cash balance.</summary>
    InsufficientCash,
}

/// <summary>
/// One repositioning move's arithmetic and refusal rules, kept pure and deterministic (no clock, no
/// database, no randomness) so it is unit-testable with exact expected values, per the project's
/// economy conventions. Every figure is in the stored base unit - formatting for display happens
/// only at the edges.
/// <para>
/// A standing weekly schedule is deliberately <b>not</b> a separate check here, and its absence is
/// not an oversight: <see cref="RepositionRefusal.NotReservedForPlayer"/> already excludes every
/// aircraft a virtual pilot could be scheduled on. Reserving an aircraft that carries active
/// scheduled legs is itself refused (FleetEndpoints.SetReservationAsync returns 409 unless the
/// player explicitly clears them), so "reserved for the player" and "on a virtual pilot's schedule"
/// cannot both be true - a second schedule check would be unreachable code pretending to be a rule.
/// </para>
/// </summary>
public static class AircraftRepositionEvaluator
{
    /// <summary>
    /// The airports a given aircraft may be repositioned to: every airport the airline has a route
    /// to <b>or</b> from (both directions count - a route is stored as a directional row, so the
    /// arrival end of one leg is just as much "an airport the player serves" as the departure end),
    /// minus wherever the aircraft already is. Returned sorted so the picker's order is stable
    /// between calls rather than following database insertion order.
    /// </summary>
    public static IReadOnlyList<string> DestinationsFor(
        IEnumerable<(string DepartureIcao, string ArrivalIcao)> routes, string currentIcao)
    {
        var served = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (departure, arrival) in routes)
        {
            if (!string.IsNullOrWhiteSpace(departure))
            {
                served.Add(Normalise(departure));
            }

            if (!string.IsNullOrWhiteSpace(arrival))
            {
                served.Add(Normalise(arrival));
            }
        }

        served.Remove(Normalise(currentIcao));
        return served.ToList();
    }

    /// <summary>
    /// Assesses a repositioning move. <paramref name="destinationIcao"/> may be null, which is how
    /// the options endpoint asks "could this aircraft be repositioned at all, before the player has
    /// picked anywhere" - the destination-specific checks are skipped in that case and everything
    /// else still runs.
    /// <para>
    /// Check order is deliberate, following the same rule the Fly screen's unflyable reasons follow:
    /// report the blocker the player can do least about first, so the reason shown always ends in an
    /// action that actually works. Being airborne or grounded outranks
    /// <see cref="RepositionRefusal.NotReservedForPlayer"/> for exactly the reason the Fly screen
    /// orders those two the same way - leading with "reserve it first" on an aircraft that is ALSO
    /// in maintenance sends the player to reserve it and then straight back here to find it still
    /// cannot move. Cash is checked last precisely because it is the most transient blocker and the
    /// most actionable.
    /// </para>
    /// </summary>
    public static AircraftRepositionAssessment Evaluate(
        string currentIcao,
        string? destinationIcao,
        bool isInFlight,
        bool isGroundedForMaintenance,
        bool isReservedForPlayer,
        IReadOnlyCollection<string> destinations,
        bool airlineHasRoutes,
        decimal cost,
        decimal cashBalance)
    {
        var cashAfter = cashBalance - cost;

        AircraftRepositionAssessment Refuse(RepositionRefusal refusal) => new(false, refusal, cost, cashAfter);

        if (isInFlight)
        {
            return Refuse(RepositionRefusal.InFlight);
        }

        if (isGroundedForMaintenance)
        {
            return Refuse(RepositionRefusal.GroundedForMaintenance);
        }

        if (!isReservedForPlayer)
        {
            return Refuse(RepositionRefusal.NotReservedForPlayer);
        }

        if (!airlineHasRoutes)
        {
            return Refuse(RepositionRefusal.NoRoutesAtAll);
        }

        if (destinations.Count == 0)
        {
            return Refuse(RepositionRefusal.NowhereElseToGo);
        }

        if (destinationIcao is not null)
        {
            var destination = Normalise(destinationIcao);

            if (destination.Length == 0 || destination == Normalise(currentIcao))
            {
                return Refuse(RepositionRefusal.AlreadyThere);
            }

            if (!destinations.Contains(destination, StringComparer.OrdinalIgnoreCase))
            {
                return Refuse(RepositionRefusal.DestinationNotServed);
            }
        }

        // Strictly less than, so a move that spends the airline's last penny is allowed - the same
        // stance every other purchase in the app takes (see FleetEndpoints.BuyAsync/LeaseAsync).
        if (cashBalance < cost)
        {
            return Refuse(RepositionRefusal.InsufficientCash);
        }

        return new AircraftRepositionAssessment(true, RepositionRefusal.None, cost, cashAfter);
    }

    private static string Normalise(string? icao) => (icao ?? string.Empty).Trim().ToUpperInvariant();
}

/// <summary>
/// The outcome of <see cref="AircraftRepositionEvaluator.Evaluate"/>. <see cref="CashAfter"/> is
/// always <c>cashBalance - Cost</c>, computed even for a refusal so the confirmation UI has one
/// figure to show and never re-derives it (and therefore can never disagree with what the commit
/// posts).
/// </summary>
public sealed record AircraftRepositionAssessment(
    bool CanReposition,
    RepositionRefusal Refusal,
    decimal Cost,
    decimal CashAfter);

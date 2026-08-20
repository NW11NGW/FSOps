using FSOps.Core.Entities;

namespace FSOps.Core.Flights;

/// <summary>
/// The one rule every <see cref="Flight"/> row must satisfy: <b>exactly one of (route AND fleet
/// aircraft) or (contract leg) is set. Never both, never neither.</b>
///
/// <para>A flight is either the player's own airline operating its own aeroplane on its own route,
/// or a job flown for another operator in an aeroplane that operator supplied. Those are the only two
/// things a sector can be, and the schema alone cannot say so - three nullable columns can express
/// four combinations, two of which are nonsense.</para>
///
/// <para><b>What each nonsense case would actually do.</b> A row with <i>both</i> claims to be two
/// different sectors at once, and every consumer would resolve it differently: the logbook would show
/// a route, the completion path would take the contract branch, and the money would come out of
/// whichever one happened to be checked first. A row with <i>neither</i> is a sector with no origin,
/// no destination and no aircraft - nothing downstream can render it, price it or explain it, and it
/// would sit in the logbook for ever as a blank line nobody can account for.</para>
///
/// <para>Enforced at the boundary that writes flights rather than trusted to callers, and thrown
/// rather than logged: a malformed flight row is permanent history the moment it is saved, and this
/// app has no way to un-write one.</para>
/// </summary>
public static class FlightWriteInvariant
{
    /// <summary>Whether this flight is a well-formed airline sector.</summary>
    public static bool IsAirlineFlight(Flight flight) =>
        flight.RouteId is not null && flight.FleetAircraftId is not null && flight.ContractLegId is null;

    /// <summary>Whether this flight is a well-formed contract sector.</summary>
    public static bool IsContractFlight(Flight flight) =>
        flight.ContractLegId is not null && flight.RouteId is null && flight.FleetAircraftId is null;

    /// <summary>
    /// Throws unless the flight is exactly one of the two legitimate shapes. Call this immediately
    /// before adding a Flight to the context - after that point the row is fact.
    /// </summary>
    public static void Validate(Flight flight)
    {
        if (IsAirlineFlight(flight) || IsContractFlight(flight))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Flight {flight.Id} is neither a well-formed airline sector nor a well-formed contract sector " +
            $"(RouteId={Describe(flight.RouteId)}, FleetAircraftId={Describe(flight.FleetAircraftId)}, " +
            $"ContractLegId={Describe(flight.ContractLegId)}). Exactly one of (route AND fleet aircraft) or " +
            "(contract leg) must be set - see FlightWriteInvariant for why both and neither are each broken.");
    }

    private static string Describe(Guid? value) => value is null ? "null" : "set";
}

using FSOps.Core.Entities;
using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

/// <summary>
/// The invariant that keeps three nullable columns from expressing four things when only two of them
/// are real: <b>exactly one of (route AND fleet aircraft) or (contract leg), never both, never
/// neither.</b>
///
/// <para>These assertions look almost trivially small, and that is the point of having them. The
/// nullability was introduced deliberately so that the compiler would enumerate every consumer; the
/// invariant is the other half of that bargain, and without a test it is only a comment. A row
/// written in a malformed shape is permanent history the moment it is saved - this app has no way to
/// un-write one - so the guard has to fail loudly at the boundary rather than be discovered later by
/// a screen rendering something impossible.</para>
/// </summary>
public class FlightWriteInvariantTests
{
    private static Flight Flight(Guid? routeId, Guid? fleetAircraftId, Guid? contractLegId) => new()
    {
        Id = Guid.NewGuid(),
        AirlineId = Guid.NewGuid(),
        RouteId = routeId,
        FleetAircraftId = fleetAircraftId,
        ContractLegId = contractLegId,
        PilotId = Guid.NewGuid(),
    };

    [Fact]
    public void AnAirlineSector_IsValid()
    {
        var flight = Flight(Guid.NewGuid(), Guid.NewGuid(), null);

        FlightWriteInvariant.Validate(flight);
        Assert.True(FlightWriteInvariant.IsAirlineFlight(flight));
        Assert.False(FlightWriteInvariant.IsContractFlight(flight));
    }

    [Fact]
    public void AContractSector_IsValid()
    {
        var flight = Flight(null, null, Guid.NewGuid());

        FlightWriteInvariant.Validate(flight);
        Assert.True(FlightWriteInvariant.IsContractFlight(flight));
        Assert.False(FlightWriteInvariant.IsAirlineFlight(flight));
    }

    /// <summary>
    /// A row claiming to be both is the worse of the two malformed shapes, because it looks fine.
    /// Every consumer would resolve it differently - the logbook would show a route, the completion
    /// path would take the contract branch, and the money would come from whichever was checked
    /// first.
    /// </summary>
    [Theory]
    // Both a route/aircraft AND a contract leg.
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    // Half an airline sector: a route with no aeroplane, or an aeroplane with no route.
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    // Nothing at all: a sector with no origin, no destination and no aircraft.
    [InlineData(false, false, false)]
    public void EveryOtherCombination_IsRefusedLoudly(bool hasRoute, bool hasAircraft, bool hasContractLeg)
    {
        var flight = Flight(
            hasRoute ? Guid.NewGuid() : null,
            hasAircraft ? Guid.NewGuid() : null,
            hasContractLeg ? Guid.NewGuid() : null);

        var ex = Assert.Throws<InvalidOperationException>(() => FlightWriteInvariant.Validate(flight));

        // The message has to name what was actually wrong. A guard that throws "invalid flight" sends
        // whoever hits it looking in the wrong place.
        Assert.Contains("RouteId=", ex.Message);
        Assert.Contains("FleetAircraftId=", ex.Message);
        Assert.Contains("ContractLegId=", ex.Message);

        Assert.False(FlightWriteInvariant.IsAirlineFlight(flight));
        Assert.False(FlightWriteInvariant.IsContractFlight(flight));
    }
}

using System.Text.Json;
using FSOps.Core.Entities;
using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

public class FlightPhaseRehydrationTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FlightEvent PhaseChangeEvent(double t, FlightPhase from, FlightPhase to, bool wasGoAround = false) => new()
    {
        Id = Guid.NewGuid(),
        FlightId = Guid.NewGuid(),
        Utc = Base + TimeSpan.FromSeconds(t),
        Type = FlightEventType.PhaseChange,
        PayloadJson = JsonSerializer.Serialize(new PhaseChangePayload(from.ToString(), to.ToString(), wasGoAround)),
    };

    private static FlightEvent TouchdownEvent(double t, double fpm, double gForce, int bounceIndex) => new()
    {
        Id = Guid.NewGuid(),
        FlightId = Guid.NewGuid(),
        Utc = Base + TimeSpan.FromSeconds(t),
        Type = FlightEventType.Touchdown,
        PayloadJson = JsonSerializer.Serialize(new TouchdownPayload(51.3, 2.08, 250, fpm, gForce, bounceIndex)),
    };

    [Fact]
    public void RestoreFrom_MidFlightEventStream_RebuildsPhaseAndOooiTimes()
    {
        var events = new List<FlightEvent>
        {
            PhaseChangeEvent(60, FlightPhase.Preflight, FlightPhase.TaxiOut),
            PhaseChangeEvent(400, FlightPhase.TaxiOut, FlightPhase.TakeoffRoll),
            PhaseChangeEvent(420, FlightPhase.TakeoffRoll, FlightPhase.Climb),
            PhaseChangeEvent(1200, FlightPhase.Climb, FlightPhase.Cruise),
        };

        var machine = FlightPhaseStateMachine.RestoreFrom(events);

        Assert.Equal(FlightPhase.Cruise, machine.CurrentPhase);
        Assert.Equal(Base + TimeSpan.FromSeconds(60), machine.OutUtc);
        Assert.Equal(Base + TimeSpan.FromSeconds(420), machine.OffUtc);
        Assert.Null(machine.OnUtc);
        Assert.Null(machine.InUtc);
        Assert.Empty(machine.Touchdowns);
    }

    [Fact]
    public void RestoreFrom_CompletedFlightWithTouchdown_RebuildsLandingFigures()
    {
        var events = new List<FlightEvent>
        {
            PhaseChangeEvent(60, FlightPhase.Preflight, FlightPhase.TaxiOut),
            PhaseChangeEvent(420, FlightPhase.TakeoffRoll, FlightPhase.Climb),
            PhaseChangeEvent(5270, FlightPhase.Descent, FlightPhase.Approach),
            TouchdownEvent(5398, fpm: 138, gForce: 1.3, bounceIndex: 0),
            PhaseChangeEvent(5398, FlightPhase.Approach, FlightPhase.Landed),
            PhaseChangeEvent(5442, FlightPhase.Landed, FlightPhase.TaxiIn),
            PhaseChangeEvent(5550, FlightPhase.TaxiIn, FlightPhase.Shutdown),
        };

        var machine = FlightPhaseStateMachine.RestoreFrom(events);

        Assert.Equal(FlightPhase.Shutdown, machine.CurrentPhase);
        Assert.Equal(Base + TimeSpan.FromSeconds(5398), machine.OnUtc);
        Assert.Equal(Base + TimeSpan.FromSeconds(5550), machine.InUtc);
        Assert.NotNull(machine.FirstTouchdown);
        Assert.Equal(138, machine.FirstTouchdown!.Fpm);
        Assert.Equal(0, machine.BounceCount);
    }

    [Fact]
    public void RestoreFrom_EventsCarryingAGoAround_ClearsTheAbortedTouchdown()
    {
        var events = new List<FlightEvent>
        {
            PhaseChangeEvent(5398, FlightPhase.Approach, FlightPhase.Landed),
            TouchdownEvent(5398, fpm: 90, gForce: 1.4, bounceIndex: 0),
            PhaseChangeEvent(5406, FlightPhase.Landed, FlightPhase.Climb, wasGoAround: true),
        };

        var machine = FlightPhaseStateMachine.RestoreFrom(events);

        Assert.Equal(FlightPhase.Climb, machine.CurrentPhase);
        Assert.Null(machine.OnUtc);
        Assert.Empty(machine.Touchdowns);
    }

    [Fact]
    public void RestoreFrom_EventsOutOfOrder_StillReplaysByTimestamp()
    {
        var events = new List<FlightEvent>
        {
            PhaseChangeEvent(420, FlightPhase.TakeoffRoll, FlightPhase.Climb),
            PhaseChangeEvent(60, FlightPhase.Preflight, FlightPhase.TaxiOut),
        };

        var machine = FlightPhaseStateMachine.RestoreFrom(events);

        Assert.Equal(FlightPhase.Climb, machine.CurrentPhase);
        Assert.Equal(Base + TimeSpan.FromSeconds(60), machine.OutUtc);
        Assert.Equal(Base + TimeSpan.FromSeconds(420), machine.OffUtc);
    }

    [Fact]
    public void RestoreFrom_EmptyEventStream_StaysAtPreflight()
    {
        var machine = FlightPhaseStateMachine.RestoreFrom(new List<FlightEvent>());

        Assert.Equal(FlightPhase.Preflight, machine.CurrentPhase);
        Assert.Null(machine.OutUtc);
    }
}

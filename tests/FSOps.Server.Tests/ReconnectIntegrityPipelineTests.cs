using System.Threading.Channels;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Core.Planning;
using FSOps.Data;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using FSOps.Sim;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// A mid-sector SimConnect outage, driven through the real wiring rather than by calling the monitor
/// directly: a real <see cref="SimTelemetryService"/> pumping a source that genuinely drops and
/// reconnects, into a real <see cref="FlightLifecycleService"/>.
/// <para>
/// The unit tests for this live in FSOps.Core.Tests and reset the monitor by hand. That proves the
/// monitor does the right thing when it is TOLD; it cannot prove it is ever told. This does - and
/// being told is the entire fix, because the acquisition gate was already being rebuilt on a
/// reconnect and the monitor was not, which is how a clean sector could be voided by the sim link
/// blinking.
/// </para>
/// </summary>
public class ReconnectIntegrityPipelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Bristol's stand - where this flight begins, and what the monitor is given as the
    /// position it should expect to start from.</summary>
    private const double StandLat = 51.38526;
    private const double StandLon = -2.71770;

    private const double CruiseKt = 450.0;
    private const int TrackedSecondsBeforeOutage = 1200;
    private const int OutageSeconds = 120;
    private const int StaleSecondsAfterReconnect = 30;
    private const int TrackedSecondsAfterOutage = 600;

    [Fact]
    public async Task SimLinkDropsMidSectorAndComesBackStale_TheSectorIsStillPayable()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            DistanceNm = 300,
            CreatedUtc = Start,
        };
        ctx.Db.Routes.Add(route);

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = Start,
            PlannedBlockMinutes = 60,
            PaxBooked = 150,
            FuelPlannedKg = 4983,
            TitleFlown = string.Empty,
            CreatedUtc = Start,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        var source = new InterruptibleSimSource();
        await using var telemetry = new SimTelemetryService(source, new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = CreateLifecycle(ctx, telemetry);

        await lifecycle.StartAsync(CancellationToken.None);

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = 60,
            Machine = new FlightPhaseStateMachine(),
            IntegrityMonitor = new FlightIntegrityMonitor((StandLat, StandLon)),
        };
        lifecycle.SetActiveTrackerForTests(tracker);

        // Twenty minutes of ordinary tracked flight, one sample a second.
        var lastBeforeOutage = Start;
        for (var t = 0; t < TrackedSecondsBeforeOutage; t++)
        {
            lastBeforeOutage = Start + TimeSpan.FromSeconds(t);
            source.Emit(At(lastBeforeOutage, LatitudeAfter(t)));
        }

        await telemetry.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => telemetry.LastSampleUtc == lastBeforeOutage, TimeSpan.FromSeconds(30));
        Assert.Equal(lastBeforeOutage, telemetry.LastSampleUtc);
        Assert.False(tracker.IntegrityMonitor.PositionJumpDetected);

        // The link goes away for two minutes and comes back.
        source.InterruptAndReconnect();

        // It resumes by replaying the position it last had, which is what hides the outage: the
        // timestamps run straight on and nothing in the data says there was ever a gap.
        var staleLatitude = LatitudeAfter(TrackedSecondsBeforeOutage - 1);
        for (var t = 0; t < StaleSecondsAfterReconnect; t++)
        {
            source.Emit(At(lastBeforeOutage + TimeSpan.FromSeconds(1 + OutageSeconds + t), staleLatitude));
        }

        // Then the truth: the aircraft really did keep flying for those two minutes.
        var resumeOffset = TrackedSecondsBeforeOutage + OutageSeconds + StaleSecondsAfterReconnect;
        var lastSampleUtc = Start;
        for (var t = 0; t < TrackedSecondsAfterOutage; t++)
        {
            lastSampleUtc = Start + TimeSpan.FromSeconds(resumeOffset + t + 1);
            source.Emit(At(lastSampleUtc, LatitudeAfter(resumeOffset + t)));
        }

        source.Complete();
        await WaitUntilAsync(() => telemetry.LastSampleUtc == lastSampleUtc, TimeSpan.FromSeconds(30));
        Assert.Equal(lastSampleUtc, telemetry.LastSampleUtc);

        await telemetry.StopAsync(CancellationToken.None);
        await lifecycle.StopAsync(CancellationToken.None);

        // Guard first, so this cannot pass for the wrong reason: the correction really is a jump
        // the speed check on its own has no answer to.
        var jumpNm = GreatCircle.DistanceNm(staleLatitude, StandLon, LatitudeAfter(resumeOffset), StandLon);
        Assert.True(jumpNm * 3600 > FlightIntegrityMonitor.ImpossibleGroundSpeedKt,
            $"the stale-to-true correction has to imply an impossible speed for this to prove anything; it was {jumpNm:F1} nm in a second");
        Assert.True(jumpNm > FlightIntegrityMonitor.NegligibleJumpDistanceNm,
            $"the correction has to be further than the negligible-jump floor, or this proves only that floor; it was {jumpNm:F1} nm");

        Assert.False(tracker.IntegrityMonitor.PositionJumpDetected);
        Assert.False(tracker.IntegrityMonitor.SlewDetected);
        Assert.False(tracker.IntegrityMonitor.SectorInvalidForPayment);
    }

    /// <summary>Latitude a northbound aircraft at <see cref="CruiseKt"/> reaches this many seconds
    /// after leaving the stand - roughly 60 nm per degree.</summary>
    private static double LatitudeAfter(double seconds) => StandLat + CruiseKt / 3600.0 * seconds / 60.0;

    private static TelemetrySample At(DateTimeOffset utc, double latitudeDeg) => new(
        utc, latitudeDeg, StandLon, 30000, 30000, 280, CruiseKt, 0, 0, 0,
        false, true, false, 1.0, 0, 5000, "Test Aircraft", "TEST", "Test", 1.0, false);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private static FlightLifecycleService CreateLifecycle(RouteTestContext ctx, SimTelemetryService telemetry)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        return new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            telemetry,
            new NoOpHubContext(),
            EconomyConfigCatalog.Default(),
            // No corroboration service at all, rather than one wired to an offline feed. The
            // network is not part of what this test is about either way, but a service that EXISTS
            // makes ProcessSample fire its VATSIM cycle off the hot path, and that fire-and-forget
            // task opens a DB scope on the shared in-memory connection which StopAsync never awaits.
            // The test then tears the connection down underneath it and fails, about one run in
            // twelve, inside SqliteConnection.Close() - with every integrity assertion having
            // passed. A one-in-twelve red on the reconnect fix's own verification is exactly the
            // test people learn to re-run, at which point it can no longer warn anyone about a real
            // regression.
            vatsimCorroboration: null,
            NullLogger<FlightLifecycleService>.Instance);
    }

    /// <summary>
    /// A sim source a test can push samples into and, crucially, take offline and bring back -
    /// raising <see cref="ISimSource.ConnectionStateChanged"/> exactly as the real SimConnect source
    /// does from its own connection loop, and delivering no telemetry at all in between, which is
    /// what makes an outage invisible to anything that only inspects the connection state once per
    /// sample.
    /// </summary>
    private sealed class InterruptibleSimSource : ISimSource
    {
        private readonly Channel<TelemetrySample> _channel = Channel.CreateUnbounded<TelemetrySample>();

        public string Kind => "Interruptible";

        public SimConnectionState ConnectionState { get; private set; } = SimConnectionState.Connected;

        public event EventHandler<SimConnectionState>? ConnectionStateChanged;

        public AircraftIdentity? CurrentAircraft => new("Test Aircraft", "TEST", "Test");

        public ChannelReader<TelemetrySample> Telemetry => _channel.Reader;

        public void Emit(TelemetrySample sample) => _channel.Writer.TryWrite(sample);

        public void Complete() => _channel.Writer.Complete();

        public void InterruptAndReconnect()
        {
            ConnectionState = SimConnectionState.Disconnected;
            ConnectionStateChanged?.Invoke(this, SimConnectionState.Disconnected);
            ConnectionState = SimConnectionState.Connecting;
            ConnectionStateChanged?.Invoke(this, SimConnectionState.Connecting);
            ConnectionState = SimConnectionState.Connected;
            ConnectionStateChanged?.Invoke(this, SimConnectionState.Connected);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}


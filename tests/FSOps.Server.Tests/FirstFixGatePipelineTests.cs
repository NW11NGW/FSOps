using System.Text.Json;
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
/// The first-fix defect proven end to end, through the real pipeline rather than against any one
/// component: a real <see cref="SimTelemetryService"/> pumping the real recorded telemetry of the
/// EGGD-EGPH flight into a real <see cref="FlightLifecycleService"/>.
/// <para>
/// The point of fixing this at the source was that THREE separate features independently trusted the
/// opening fix, and fixing them one at a time would leave the fourth to rediscover the bug. So this
/// asserts all of them at once, on the actual data, rather than reasoning about the code: the
/// recorded track, the VATSIM corroboration check, and what a rehydrate after a crash would read
/// back. The integrity monitor is checked here too, since its own unit tests exercise it in
/// isolation and this is the only place the whole chain runs together.
/// </para>
/// </summary>
public class FirstFixGatePipelineTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Bristol, where the aircraft actually was - its own second recorded fix.</summary>
    private const double BristolLat = 51.38526;
    private const double BristolLon = -2.71770;

    /// <summary>The uninitialised opening fix: roughly the Bay of Bengal, 5,505 nm away.</summary>
    private const double BadFixLat = 0.0;
    private const double BadFixLon = 90.0;

    private const int VatsimCid = 123456;

    [Fact]
    public async Task RealFlight_ThroughTheWholePipeline_NoConsumerIsEverHandedTheBadOpeningFix()
    {
        var snapshots = LoadRecordedFlight();

        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ctx.CurrentUser.UserId,
            VatsimCid = VatsimCid.ToString(),
        });
        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            DistanceNm = 300,
            CreatedUtc = snapshots[0].Utc,
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
            PlannedDepartureUtc = snapshots[0].Utc,
            PlannedBlockMinutes = 60,
            PaxBooked = 150,
            FuelPlannedKg = 4983,
            TitleFlown = string.Empty,
            CreatedUtc = snapshots[0].Utc,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        // A VATSIM feed that turns "was this consumer ever handed the bad fix?" into an observable
        // match - see BadFixTripwireVatsimClient.
        var vatsimClient = new BadFixTripwireVatsimClient();

        // The real wiring: the lifecycle service subscribes to the telemetry service's own
        // SampleReceived in its StartAsync, exactly as it does in the running app.
        var source = new ReplaySimSource(snapshots.Select(ToTelemetrySample));
        await using var telemetry = new SimTelemetryService(source, new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = CreateLifecycle(ctx, telemetry, new VatsimFlightCorroborationService(vatsimClient));

        await lifecycle.StartAsync(CancellationToken.None);

        // Installed AFTER StartAsync, which rehydrates an in-progress flight into a tracker of its
        // own - this replaces it with one the test holds a reference to.
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = 60,
            Machine = new FlightPhaseStateMachine(),
            IntegrityMonitor = new FlightIntegrityMonitor((BristolLat, BristolLon)),
        };
        lifecycle.SetActiveTrackerForTests(tracker);

        // StopAsync CANCELS the pump rather than draining it, so the replay has to be waited out
        // first - otherwise the test would tear down mid-flight and assert against a partial run.
        await telemetry.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => telemetry.LastSampleUtc == snapshots[^1].Utc, TimeSpan.FromSeconds(30));
        Assert.Equal(snapshots[^1].Utc, telemetry.LastSampleUtc);

        // The VATSIM cycle is deliberately fired off the hot path, so it is the one thing here that
        // cannot be joined - wait for it rather than racing it.
        await WaitUntilAsync(() => tracker.VatsimChecksTotal > 0, TimeSpan.FromSeconds(10));

        await telemetry.StopAsync(CancellationToken.None);

        // Flushes the batched flight-event writer.
        await lifecycle.StopAsync(CancellationToken.None);

        // ---- Consumer 0: nothing outside the flight tracker sees it either ----
        Assert.NotNull(telemetry.LastSample);
        AssertNotTheBadFix(telemetry.LastSample!.LatitudeDeg, telemetry.LastSample.LongitudeDeg, "SimTelemetryService.LastSample");

        // ---- Consumer 1: the flight's own recorded track ----
        var recorded = await ctx.Db.FlightEvents
            .Where(e => e.FlightId == flight.Id && e.Type == FlightEventType.PositionSnapshot)
            .ToListAsync();
        Assert.NotEmpty(recorded);

        var recordedPositions = recorded
            .Select(e => JsonDocument.Parse(e.PayloadJson).RootElement)
            .Select(root => (Lat: root.GetProperty("lat").GetDouble(), Lon: root.GetProperty("lon").GetDouble()))
            .ToList();
        foreach (var position in recordedPositions)
        {
            AssertNotTheBadFix(position.Lat, position.Lon, "a persisted PositionSnapshot");
        }

        // ---- Consumer 2: VATSIM corroboration ----
        // Guard first: if no check ever ran, everything below would pass for the wrong reason.
        Assert.Equal(VatsimCid, tracker.VatsimCid);
        Assert.True(tracker.VatsimChecksTotal > 0, "no VATSIM check ran at all, so this proves nothing");

        // The tripwire is parked on the bad fix. Not one of the checks was run from there - which is
        // exactly what used to happen, on the very first sample of every flight.
        Assert.Equal(0, tracker.VatsimChecksMatched);
        Assert.Null(tracker.VatsimLastCallsign);

        // ---- Consumer 3: what a rehydrate after a crash would read back ----
        // RehydrateInProgressFlightAsync rebuilds LastKnownPosition from these rows. The hazard was a
        // restart inside the first fifteen seconds, when the ONLY row was the bad fix: nothing could
        // then be within the 5 nm resume radius and the flight was marked Interrupted. The earliest
        // row is now the aircraft on its stand, so even the earliest possible restart resumes.
        var earliest = recorded.OrderBy(e => e.Utc).First();
        var earliestRoot = JsonDocument.Parse(earliest.PayloadJson).RootElement;
        Assert.True(
            GreatCircle.DistanceNm(BristolLat, BristolLon, earliestRoot.GetProperty("lat").GetDouble(), earliestRoot.GetProperty("lon").GetDouble()) < 1.0,
            "the earliest recorded position must be somewhere a reconnect can actually resume from");

        // ---- Consumer 4: the integrity monitor, unchanged and still clean ----
        Assert.False(tracker.IntegrityMonitor.PositionJumpDetected);
        Assert.False(tracker.IntegrityMonitor.SectorInvalidForPayment);

        // ---- And the flight still tracked normally: withholding two opening samples cost nothing ----
        // The two samples that were withheld were both twelve minutes before the aircraft moved, so
        // OOOI is entirely unaffected. (In is not reachable from the recorded snapshots alone - the
        // shutdown happened after the last one was written - so this asserts as far as the data goes.)
        Assert.NotNull(tracker.Machine.OutUtc);
        Assert.NotNull(tracker.Machine.OffUtc);
        Assert.NotNull(tracker.Machine.OnUtc);
        Assert.True(tracker.Machine.OutUtc < tracker.Machine.OffUtc);
        Assert.True(tracker.Machine.OffUtc < tracker.Machine.OnUtc);
        Assert.True(tracker.Machine.CurrentPhase is FlightPhase.Landed or FlightPhase.TaxiIn or FlightPhase.Shutdown);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private static void AssertNotTheBadFix(double latitudeDeg, double longitudeDeg, string what)
    {
        var fromBadFixNm = GreatCircle.DistanceNm(BadFixLat, BadFixLon, latitudeDeg, longitudeDeg);
        Assert.True(fromBadFixNm > 1000, $"{what} was handed the uninitialised opening fix ({latitudeDeg}, {longitudeDeg})");
    }

    private static List<RecordedSnapshot> LoadRecordedFlight()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Flights", "Fixtures", "eggd-egph-20260812-snapshots.json");
        return JsonSerializer.Deserialize<RecordedFlight>(File.ReadAllText(path), JsonOptions)!.Snapshots.ToList();
    }

    /// <summary>
    /// Rebuilds a full telemetry sample from a recorded snapshot. The recorded rows carry position,
    /// altitude, speeds and phase; the on-ground/engine/brake flags are reconstructed from them so
    /// the phase machine walks the same path it walked on the day.
    /// </summary>
    private static TelemetrySample ToTelemetrySample(RecordedSnapshot s)
    {
        var onGround = s.AltAglFt < 15;
        var engineRunning = s.Phase != "Preflight" || s.GsKt > 0.1;
        var parkingBrakeSet = s.Phase == "Shutdown";
        return new TelemetrySample(
            s.Utc, s.Lat, s.Lon, s.AltAglFt + 100, s.AltAglFt,
            s.GsKt, s.GsKt, s.VsFpm, 0, 0,
            onGround, engineRunning, parkingBrakeSet, 1.0, 0, 5000,
            "Test Aircraft", "TEST", "Test", 1.0, false);
    }

    private static FlightLifecycleService CreateLifecycle(
        RouteTestContext ctx, SimTelemetryService telemetry, VatsimFlightCorroborationService vatsim)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        return new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            telemetry,
            new NoOpHubContext(),
            EconomyConfigCatalog.Default(),
            vatsim,
            NullLogger<FlightLifecycleService>.Instance);
    }

    /// <summary>An ISimSource whose whole telemetry stream is written and closed up front, so the
    /// pump drains it and ends deterministically instead of the test having to sleep.</summary>
    private sealed class ReplaySimSource : ISimSource
    {
        private readonly Channel<TelemetrySample> _channel = Channel.CreateUnbounded<TelemetrySample>();

        public ReplaySimSource(IEnumerable<TelemetrySample> samples)
        {
            foreach (var sample in samples)
            {
                _channel.Writer.TryWrite(sample);
            }

            _channel.Writer.Complete();
        }

        public string Kind => "Replay";

        public SimConnectionState ConnectionState => SimConnectionState.Connected;

        public event EventHandler<SimConnectionState>? ConnectionStateChanged { add { } remove { } }

        public AircraftIdentity? CurrentAircraft => new("Test Aircraft", "TEST", "Test");

        public ChannelReader<TelemetrySample> Telemetry => _channel.Reader;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A feed reporting the flight's own CID parked exactly on the bad opening fix. The corroboration
    /// service matches on distance between the position FSOps hands it and the position the feed
    /// reports, so this turns "was consumer #2 ever given the Bay of Bengal?" into an observable
    /// outcome: a check run from there matches, and a check run from anywhere the aircraft really
    /// was is 5,505 nm away and cannot. A tripwire rather than a spy - it needs no access to the
    /// service's internals and exercises entirely real code.
    /// </summary>
    private sealed class BadFixTripwireVatsimClient : IVatsimNetworkClient
    {
        private readonly VatsimSnapshot _snapshot = new(
            true,
            DateTimeOffset.UtcNow,
            Array.Empty<VatsimController>(),
            new[]
            {
                new VatsimPilot(
                    "TRIPWIRE", VatsimCid, "Test Pilot", BadFixLat, BadFixLon,
                    AltitudeFt: 0, GroundSpeedKt: 0, HeadingDeg: 0,
                    DepartureIcao: "EGGD", ArrivalIcao: "EGPH",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            });

        public Task<VatsimSnapshot> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(_snapshot);
    }

    private sealed record RecordedFlight(string Source, IReadOnlyList<RecordedSnapshot> Snapshots);

    private sealed record RecordedSnapshot(
        DateTimeOffset Utc, double Lat, double Lon, double AltAglFt, double GsKt, double VsFpm, string Phase);
}

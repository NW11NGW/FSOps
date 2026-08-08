using System.Text.Json;
using System.Threading.Channels;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Core.Planning;
using FSOps.Data;
using FSOps.Server.Hubs;
using FSOps.Sim;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>Live numbers for the flight currently being tracked - what <c>GET /flights/active</c> and the <c>flightUpdate</c> hub event carry.</summary>
public sealed record LiveFlightSnapshot(
    Guid FlightId,
    string Phase,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeMslFt,
    double AltitudeAglFt,
    double IndicatedAirspeedKt,
    double GroundSpeedKt,
    double VerticalSpeedFpm,
    double FuelRemainingKg,
    double ElapsedBlockMinutes,
    int PlannedBlockMinutes,
    bool AwaitingSimReconnect,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Owns the flight currently being tracked: advances its <see cref="FlightPhaseStateMachine"/> off
/// every full-rate telemetry sample, queues the append-only FlightEvent rows that come out of that
/// (batched, off the hot path), throttles a live SignalR update, and finalises the Flight row when
/// the state machine reaches Shutdown. The app tracks one flight at a time, so there is a single
/// tracker slot rather than a dictionary.
/// <para>
/// A SimConnect disconnection mid-flight is handled by doing nothing: <see cref="OnSample"/> simply
/// stops being called while the sim is down and picks back up once it reconnects, so tracking is
/// paused, never abandoned - see the "NEVER destroy the user's data" project rule.
/// </para>
/// </summary>
public sealed class FlightLifecycleService : IHostedService
{
    private static readonly TimeSpan PositionSnapshotInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconnectResolutionTimeout = TimeSpan.FromSeconds(30);
    private const double ResumeDistanceNm = 5.0;
    private const int WriteBatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SimTelemetryService _telemetry;
    private readonly IHubContext<LiveHub> _hub;
    private readonly EconomyConfig _economyConfig;
    private readonly ILogger<FlightLifecycleService> _logger;

    private readonly Channel<FlightEvent> _eventQueue = Channel.CreateBounded<FlightEvent>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });

    private readonly object _lock = new();
    private ActiveFlightTracker? _active;

    private CancellationTokenSource? _cts;
    private Task? _writerTask;

    public FlightLifecycleService(
        IServiceScopeFactory scopeFactory, SimTelemetryService telemetry, IHubContext<LiveHub> hub,
        EconomyConfig economyConfig, ILogger<FlightLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _telemetry = telemetry;
        _hub = hub;
        _economyConfig = economyConfig;
        _logger = logger;
    }

    public Guid? ActiveFlightId
    {
        get { lock (_lock) { return _active?.FlightId; } }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token), CancellationToken.None);
        await RehydrateInProgressFlightAsync(cancellationToken);
        _telemetry.SampleReceived += OnSample;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _telemetry.SampleReceived -= OnSample;
        _cts?.Cancel();
        _eventQueue.Writer.TryComplete();

        if (_writerTask is not null)
        {
            try
            {
                await _writerTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    /// <summary>Starts live tracking for a flight the caller has already created and saved.</summary>
    public void BeginTracking(Guid flightId, Guid airlineId, Guid fleetAircraftId, string arrivalIcao, int plannedBlockMinutes)
    {
        var tracker = new ActiveFlightTracker
        {
            FlightId = flightId,
            AirlineId = airlineId,
            FleetAircraftId = fleetAircraftId,
            ArrivalIcao = arrivalIcao,
            PlannedBlockMinutes = plannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };

        lock (_lock)
        {
            _active = tracker;
        }
    }

    /// <summary>Detaches live tracking without touching the Flight row - the caller (abandon/complete-manual) owns that.</summary>
    public void StopTracking(Guid flightId)
    {
        lock (_lock)
        {
            if (_active?.FlightId == flightId)
            {
                _active = null;
            }
        }
    }

    public LiveFlightSnapshot? GetActiveSnapshot(Guid flightId)
    {
        lock (_lock)
        {
            return _active?.FlightId == flightId ? _active.LatestSnapshot : null;
        }
    }

    private void OnSample(object? sender, TelemetrySample sample)
    {
        ActiveFlightTracker? tracker;
        lock (_lock)
        {
            tracker = _active;
        }

        if (tracker is null)
        {
            return;
        }

        try
        {
            ProcessSample(tracker, sample);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error advancing the flight phase machine for flight {FlightId}.", tracker.FlightId);
        }
    }

    private void ProcessSample(ActiveFlightTracker tracker, TelemetrySample sample)
    {
        if (tracker.PendingReconnect)
        {
            if (!TryResolveReconnect(tracker, sample))
            {
                return;
            }
        }

        tracker.StartFuelKg ??= sample.TotalFuelKg;
        tracker.LastFuelKg = sample.TotalFuelKg;

        var flightSample = MapSample(sample);
        var result = tracker.Machine.Advance(flightSample);
        tracker.IntegrityMonitor.Observe(flightSample);
        var now = sample.TimestampUtc;

        if (result.PhaseChanged)
        {
            var payload = new PhaseChangePayload(result.PreviousPhase!.Value.ToString(), result.Phase.ToString(), result.IsGoAround);
            Enqueue(tracker.FlightId, now, FlightEventType.PhaseChange, JsonSerializer.Serialize(payload));
        }

        if (result.NewTouchdown is { } touchdown)
        {
            var bounceIndex = tracker.Machine.Touchdowns.Count - 1;
            var payload = new TouchdownPayload(touchdown.LatitudeDeg, touchdown.LongitudeDeg, touchdown.TrueHeadingDeg, touchdown.Fpm, touchdown.GForce, bounceIndex);
            Enqueue(tracker.FlightId, now, FlightEventType.Touchdown, JsonSerializer.Serialize(payload));
        }

        if (now - tracker.LastSnapshotUtc >= PositionSnapshotInterval)
        {
            tracker.LastSnapshotUtc = now;
            var payload = JsonSerializer.Serialize(new
            {
                lat = sample.LatitudeDeg,
                lon = sample.LongitudeDeg,
                altMslFt = sample.AltitudeMslFt,
                altAglFt = sample.AltitudeAglFt,
                iasKt = sample.IndicatedAirspeedKt,
                gsKt = sample.GroundSpeedKt,
                vsFpm = sample.VerticalSpeedFpm,
                headingTrue = sample.TrueHeadingDeg,
                fuelKg = sample.TotalFuelKg,
                phase = result.Phase.ToString(),
            });
            Enqueue(tracker.FlightId, now, FlightEventType.PositionSnapshot, payload);
        }

        UpdateLiveSnapshot(tracker, sample, result.Phase);

        if (now - tracker.LastBroadcastUtc >= BroadcastInterval)
        {
            tracker.LastBroadcastUtc = now;
            var snapshot = tracker.LatestSnapshot;
            if (snapshot is not null)
            {
                FireAndForget(() => _hub.Clients.All.SendAsync("flightUpdate", snapshot), "broadcast flightUpdate");
            }
        }

        if (result.PhaseChanged && result.Phase == FlightPhase.Shutdown)
        {
            lock (_lock)
            {
                if (_active == tracker)
                {
                    _active = null;
                }
            }

            FireAndForget(() => FinalizeFlightAsync(tracker), $"finalize flight {tracker.FlightId}");
        }
    }

    /// <summary>
    /// While waiting for the sim to reconnect after a rehydrate, checks whether the newest sample
    /// puts the aircraft close to where the flight last knew it was. Returns true once tracking
    /// should resume normally for this sample.
    /// </summary>
    private bool TryResolveReconnect(ActiveFlightTracker tracker, TelemetrySample sample)
    {
        tracker.PendingReconnectSinceUtc ??= DateTimeOffset.UtcNow;

        if (tracker.LastKnownPosition is { } last)
        {
            var distanceNm = GreatCircle.DistanceNm(last.Lat, last.Lon, sample.LatitudeDeg, sample.LongitudeDeg);
            if (distanceNm <= ResumeDistanceNm)
            {
                tracker.PendingReconnect = false;
                _logger.LogInformation("Flight {FlightId} resumed tracking near its last known position ({DistanceNm:F1} nm away).", tracker.FlightId, distanceNm);
                return true;
            }
        }
        else
        {
            // No recorded position to compare against - nothing sensible to check, so just resume.
            tracker.PendingReconnect = false;
            return true;
        }

        if (DateTimeOffset.UtcNow - tracker.PendingReconnectSinceUtc > ReconnectResolutionTimeout)
        {
            lock (_lock)
            {
                if (_active == tracker)
                {
                    _active = null;
                }
            }

            FireAndForget(() => MarkInterruptedAsync(tracker.FlightId), $"mark flight {tracker.FlightId} interrupted");
        }

        return false;
    }

    private async Task RehydrateInProgressFlightAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();

        var flight = await db.Flights.FirstOrDefaultAsync(f => f.Status == FlightStatus.InProgress, ct);
        if (flight is null)
        {
            return;
        }

        // Materialise first - the SQLite provider can't translate ORDER BY over DateTimeOffset.
        var events = await db.FlightEvents.Where(e => e.FlightId == flight.Id).ToListAsync(ct);
        var machine = FlightPhaseStateMachine.RestoreFrom(events);

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == flight.RouteId, ct);
        var lastKnown = events
            .Where(e => e.Type is FlightEventType.PositionSnapshot or FlightEventType.Touchdown)
            .OrderByDescending(e => e.Utc)
            .Select(TryExtractPosition)
            .FirstOrDefault(p => p is not null);

        var tracker = new ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = flight.AirlineId,
            FleetAircraftId = flight.FleetAircraftId,
            ArrivalIcao = route?.ArrivalIcao ?? string.Empty,
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = machine,
            PendingReconnect = true,
            LastKnownPosition = lastKnown,
        };

        lock (_lock)
        {
            _active = tracker;
        }

        _logger.LogInformation(
            "Rehydrated in-progress flight {FlightId} at phase {Phase} from {EventCount} events; waiting for the sim to reconnect.",
            flight.Id, machine.CurrentPhase, events.Count);
    }

    private async Task MarkInterruptedAsync(Guid flightId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();

        var flight = await db.Flights.FirstOrDefaultAsync(f => f.Id == flightId);
        if (flight is null || flight.Status != FlightStatus.InProgress)
        {
            return;
        }

        flight.Status = FlightStatus.Interrupted;
        await db.SaveChangesAsync();

        _logger.LogWarning("Flight {FlightId} could not be resumed automatically and needs user resolution.", flightId);
        await _hub.Clients.All.SendAsync("flightNeedsResolution", new { flightId });
    }

    /// <summary>Internal (rather than private) purely so tests can drive it directly with a
    /// synthetic <see cref="ActiveFlightTracker"/> instead of needing a live telemetry pump.</summary>
    internal async Task FinalizeFlightAsync(ActiveFlightTracker tracker)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();

        var flight = await db.Flights.FirstOrDefaultAsync(f => f.Id == tracker.FlightId);
        if (flight is null)
        {
            return;
        }

        // Idempotency: a retry, reconnect, or crash rehydration must never finalise (or re-post
        // the ledger for) the same flight twice - see Flight.RevenuePosted. A flight already
        // Completed has necessarily already run this whole method once.
        if (flight.Status == FlightStatus.Completed || flight.RevenuePosted)
        {
            _logger.LogWarning("Flight {FlightId} was already completed; ignoring a duplicate finalize call.", flight.Id);
            return;
        }

        var machine = tracker.Machine;

        // The moment the flight actually completed - used to timestamp its ledger lines and as
        // the day the demand model (season/day-of-week) resolves against, falling back to now if
        // the state machine somehow never captured an In time.
        var completionUtc = machine.InUtc ?? DateTimeOffset.UtcNow;

        flight.OutUtc = machine.OutUtc;
        flight.OffUtc = machine.OffUtc;
        flight.OnUtc = machine.OnUtc;
        flight.InUtc = machine.InUtc;
        flight.FuelUsedKg = Math.Max(0, (tracker.StartFuelKg ?? tracker.LastFuelKg) - tracker.LastFuelKg);
        // Overwritten below by FlightEconomicsPoster.PostCompletionAsync when the sector is
        // payable (real demand-modelled booking, not every seat sold) - this is just the fallback
        // for a sector that can't be priced (see the guard around that call).
        flight.PaxFlown = flight.PaxBooked;

        if (machine.FirstTouchdown is { } first)
        {
            flight.LandingFpmFirst = first.Fpm;
            flight.LandingGForce = first.GForce;

            var runways = await db.Runways.Where(r => r.AirportIcao == tracker.ArrivalIcao).ToListAsync();
            flight.CentrelineDeviationM = LandingQualityCalculator.CentrelineDeviationMetres(
                runways, first.LatitudeDeg, first.LongitudeDeg, first.TrueHeadingDeg);
        }

        if (machine.HardestTouchdown is { } hardest)
        {
            flight.LandingFpmHardest = hardest.Fpm;
        }

        flight.SimRateElevated = tracker.IntegrityMonitor.ElevatedSimRateDetected;
        flight.MaxSimulationRateObserved = tracker.IntegrityMonitor.MaxSimulationRateObserved;
        flight.SlewDetected = tracker.IntegrityMonitor.SlewDetected;
        flight.PositionJumpDetected = tracker.IntegrityMonitor.PositionJumpDetected;

        flight.Status = FlightStatus.Completed;

        var fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == tracker.FleetAircraftId);
        if (fleetAircraft is not null)
        {
            // The last position telemetry ever reported for this flight - where it actually ended
            // up, which is not always the planned arrival (see the diversion rule in docs/PLAN.md).
            (double LatitudeDeg, double LongitudeDeg)? finalPosition = tracker.LatestSnapshot is { } snapshot
                ? (snapshot.LatitudeDeg, snapshot.LongitudeDeg)
                : null;

            var candidateAirports = finalPosition is { } position
                ? await AirportProximityQueries.NearbyAsync(db, position.LatitudeDeg, position.LongitudeDeg, CancellationToken.None)
                : [];

            var landing = LandingAirportResolver.Resolve(candidateAirports, finalPosition, tracker.ArrivalIcao);
            fleetAircraft.LocationIcao = landing.Icao;

            if (fleetAircraft.Status == FleetAircraftStatus.InFlight)
            {
                fleetAircraft.Status = FleetAircraftStatus.Active;
            }

            var flightHours = BlockTimeCalculator.BlockHours(machine.OutUtc, machine.InUtc);
            fleetAircraft.AirframeHours += flightHours;

            // Landing/handling/parking/passenger/turnaround fees are charged at wherever the
            // aircraft actually landed (landing.Icao), not necessarily the planned arrival - a
            // diversion still incurs real ground-service costs at the airport it used. Fuel was
            // already charged at uplift (flight start); this posts every other line, or nothing
            // at all if the sector isn't payable (see FlightEconomicsPoster.PostCompletionAsync).
            // Quietly skips if any of the data it needs can't be resolved - better to post
            // nothing than guess.
            var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == flight.RouteId);
            var airline = await db.Airlines.FirstOrDefaultAsync(a => a.Id == flight.AirlineId);
            var arrivalAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == landing.Icao);
            var aircraftType = await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId]);

            if (route is not null && airline is not null && arrivalAirport is not null && aircraftType is not null)
            {
                await FlightEconomicsPoster.PostCompletionAsync(
                    db, flight, airline, route, aircraftType, arrivalAirport, _economyConfig, flightHours, completionUtc, CancellationToken.None);
            }

            if (landing.Decision == LandingAirportDecision.Diverted)
            {
                _logger.LogInformation(
                    "Flight {FlightId} diverted: planned arrival was {PlannedArrival}, actually landed at {ActualArrival} ({DistanceNm:F1} nm from the final tracked position).",
                    flight.Id, tracker.ArrivalIcao, landing.Icao, landing.DistanceFromFinalPositionNm);
            }
            else if (landing.Decision == LandingAirportDecision.UnresolvedFallbackToPlanned)
            {
                _logger.LogWarning(
                    "Flight {FlightId} finished with a final position that matched no known airport within {RadiusNm} nm - falling back to the planned arrival {PlannedArrival}.",
                    flight.Id, LandingAirportResolver.SearchRadiusNm, tracker.ArrivalIcao);
            }
        }

        await db.SaveChangesAsync();

        await _hub.Clients.All.SendAsync("flightCompleted", new { flightId = flight.Id, status = flight.Status.ToString() });
        _logger.LogInformation("Flight {FlightId} completed.", flight.Id);
    }

    private void Enqueue(Guid flightId, DateTimeOffset utc, FlightEventType type, string payloadJson)
    {
        var evt = new FlightEvent { Id = Guid.NewGuid(), FlightId = flightId, Utc = utc, Type = type, PayloadJson = payloadJson };
        if (!_eventQueue.Writer.TryWrite(evt))
        {
            _logger.LogWarning("Flight event queue is full; dropping a {Type} event for flight {FlightId}.", type, flightId);
        }
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        var batch = new List<FlightEvent>(WriteBatchSize);

        try
        {
            while (await _eventQueue.Reader.WaitToReadAsync(ct))
            {
                while (batch.Count < WriteBatchSize && _eventQueue.Reader.TryRead(out var evt))
                {
                    batch.Add(evt);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
                db.FlightEvents.AddRange(batch);
                await db.SaveChangesAsync(ct);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flight event writer loop stopped unexpectedly.");
        }
    }

    private static void UpdateLiveSnapshot(ActiveFlightTracker tracker, TelemetrySample sample, FlightPhase phase)
    {
        var elapsedMinutes = tracker.Machine.OutUtc is { } outUtc ? (sample.TimestampUtc - outUtc).TotalMinutes : 0;
        tracker.LatestSnapshot = new LiveFlightSnapshot(
            tracker.FlightId, phase.ToString(), sample.LatitudeDeg, sample.LongitudeDeg,
            sample.AltitudeMslFt, sample.AltitudeAglFt, sample.IndicatedAirspeedKt, sample.GroundSpeedKt,
            sample.VerticalSpeedFpm, sample.TotalFuelKg, elapsedMinutes, tracker.PlannedBlockMinutes,
            tracker.PendingReconnect, sample.TimestampUtc);
    }

    private static FlightTelemetrySample MapSample(TelemetrySample s) => new(
        s.TimestampUtc, s.LatitudeDeg, s.LongitudeDeg, s.AltitudeMslFt, s.AltitudeAglFt,
        s.IndicatedAirspeedKt, s.GroundSpeedKt, s.VerticalSpeedFpm, s.TrueHeadingDeg, s.MagneticHeadingDeg,
        s.OnGround, s.EngineRunning, s.ParkingBrakeSet, s.GForce, s.TouchdownNormalVelocityFps, s.TotalFuelKg,
        s.AircraftTitle, s.AtcModel, s.AtcType, s.SimulationRate, s.IsSlewActive);

    private static (double Lat, double Lon)? TryExtractPosition(FlightEvent evt)
    {
        try
        {
            if (evt.Type == FlightEventType.Touchdown)
            {
                var payload = JsonSerializer.Deserialize<TouchdownPayload>(evt.PayloadJson);
                return payload is null ? null : (payload.LatitudeDeg, payload.LongitudeDeg);
            }

            using var doc = JsonDocument.Parse(evt.PayloadJson);
            if (doc.RootElement.TryGetProperty("lat", out var latEl) && doc.RootElement.TryGetProperty("lon", out var lonEl))
            {
                return (latEl.GetDouble(), lonEl.GetDouble());
            }
        }
        catch (JsonException)
        {
            // Malformed payload from an older/different build - just treat as "unknown position".
        }

        return null;
    }

    private void FireAndForget(Func<Task> work, string description)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background flight task failed: {Description}", description);
            }
        });
    }

    /// <summary>Internal (rather than private) purely so tests can construct one directly - see
    /// <see cref="FinalizeFlightAsync"/>.</summary>
    internal sealed class ActiveFlightTracker
    {
        public required Guid FlightId { get; init; }

        public required Guid AirlineId { get; init; }

        public required Guid FleetAircraftId { get; init; }

        public required string ArrivalIcao { get; init; }

        public required int PlannedBlockMinutes { get; init; }

        public required FlightPhaseStateMachine Machine { get; init; }

        public FlightIntegrityMonitor IntegrityMonitor { get; } = new();

        public double? StartFuelKg { get; set; }

        public double LastFuelKg { get; set; }

        public DateTimeOffset LastSnapshotUtc { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset LastBroadcastUtc { get; set; } = DateTimeOffset.MinValue;

        public LiveFlightSnapshot? LatestSnapshot { get; set; }

        public bool PendingReconnect { get; set; }

        public DateTimeOffset? PendingReconnectSinceUtc { get; set; }

        public (double Lat, double Lon)? LastKnownPosition { get; set; }
    }
}

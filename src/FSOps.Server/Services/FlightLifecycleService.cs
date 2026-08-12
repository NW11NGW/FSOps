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
    DateTimeOffset TimestampUtc,
    // Trailing with a default so the existing test call sites that construct this positionally
    // without a heading keep compiling unchanged. True heading (not magnetic) to match what
    // LiveFlightMap already renders for the in-flight marker - see OperationsEndpoints, the only
    // other reader of this field.
    double TrueHeadingDeg = 0);

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

    // Matches VatsimNetworkClient's own ~20s cache refresh, so this never polls faster than the
    // shared snapshot actually changes - a check inside the cache window costs nothing extra (see
    // VatsimFlightCorroborationService's class doc), but there is no reason to ask more often than
    // the answer can change either.
    private static readonly TimeSpan VatsimCheckInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SimTelemetryService _telemetry;
    private readonly IHubContext<LiveHub> _hub;
    private readonly EconomyConfigCatalog _economyConfigCatalog;

    /// <summary>Nullable rather than a `null!` convenience at every test call site that doesn't
    /// exercise G8 - a stated "online detection may be unavailable" contract in the type, not an
    /// unstated fact a future test author would have to rediscover the hard way (a
    /// NullReferenceException from deep inside the tracker) the first time they seed a VATSIM CID
    /// without also wiring this up. Every use below checks it explicitly.</summary>
    private readonly VatsimFlightCorroborationService? _vatsimCorroboration;

    private readonly ILogger<FlightLifecycleService> _logger;

    private readonly Channel<FlightEvent> _eventQueue = Channel.CreateBounded<FlightEvent>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });

    private readonly object _lock = new();
    private ActiveFlightTracker? _active;

    private CancellationTokenSource? _cts;
    private Task? _writerTask;

    public FlightLifecycleService(
        IServiceScopeFactory scopeFactory, SimTelemetryService telemetry, IHubContext<LiveHub> hub,
        EconomyConfigCatalog economyConfigCatalog, VatsimFlightCorroborationService? vatsimCorroboration,
        ILogger<FlightLifecycleService> logger)
    {
        _scopeFactory = scopeFactory;
        _telemetry = telemetry;
        _hub = hub;
        _economyConfigCatalog = economyConfigCatalog;
        _vatsimCorroboration = vatsimCorroboration;
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

    /// <summary>
    /// Starts live tracking for a flight the caller has already created and saved.
    /// <paramref name="startingFuelKg"/> is whatever <c>FlightEndpoints.StartAsync</c> resolved
    /// the aircraft's fuel to be after its own start-of-flight reconciliation - the baseline live
    /// ground-fuel detection (see <see cref="ProcessSample"/>) compares against, so a genuine
    /// uplift is never double-charged (once at start reconciliation, again at the first live
    /// sample) and a quiet gate sit produces no false positive.
    /// </summary>
    public void BeginTracking(Guid flightId, Guid airlineId, Guid fleetAircraftId, string arrivalIcao, int plannedBlockMinutes, double startingFuelKg)
    {
        var tracker = new ActiveFlightTracker
        {
            FlightId = flightId,
            AirlineId = airlineId,
            FleetAircraftId = fleetAircraftId,
            ArrivalIcao = arrivalIcao,
            PlannedBlockMinutes = plannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
            LastGroundFuelKg = startingFuelKg,
        };

        lock (_lock)
        {
            _active = tracker;
        }
    }

    /// <summary>
    /// Resolves the flight's departure airport and the player's configured VATSIM CID (G8) from the
    /// database - purely so <see cref="BeginTracking"/>'s own signature, and every call site that
    /// calls it (including <c>FlightEndpoints.StartAsync</c>), never needed to change to pass them
    /// in. Deliberately NOT fired eagerly from <see cref="BeginTracking"/> itself: every other
    /// background database task in this class (<see cref="HandleGroundFuelChangeAsync"/>, the
    /// broadcast/finalize work) is gated behind <see cref="ProcessSample"/>'s own cadence, driven by
    /// the SAMPLE's timestamp rather than the real wall clock - see <see cref="RunVatsimCycleAsync"/>,
    /// which is the only caller. An eager fire-and-forget the instant tracking begins has no such
    /// gate and no join point a test (or a very short real flight) can ever wait on, which is
    /// exactly the shape of bug that raced a shared SQLite connection against test teardown when
    /// this was tried that way first - see the git history if this comment outlives its usefulness.
    /// Internal (rather than private) so tests can await it directly instead of racing a
    /// fire-and-forget background task.
    /// </summary>
    internal async Task ResolveVatsimContextAsync(ActiveFlightTracker tracker)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();

        var flight = await db.Flights.FirstOrDefaultAsync(f => f.Id == tracker.FlightId);
        var route = flight is not null ? await db.Routes.FirstOrDefaultAsync(r => r.Id == flight.RouteId) : null;
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.Id == tracker.AirlineId);
        var settings = airline is not null ? await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == airline.OwnerUserId) : null;

        tracker.DepartureIcao = route?.DepartureIcao;
        // A blank/non-numeric CID (never set, or a typo) simply disables corroboration for this
        // flight - the same "opt-in, fail soft" rule as an unreachable feed, never a validation
        // error surfaced mid-flight.
        tracker.VatsimCid = int.TryParse(settings?.VatsimCid, out var cid) && cid > 0 ? cid : null;
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

    /// <summary>Internal (rather than private) purely so tests can drive it directly with a
    /// synthetic <see cref="TelemetrySample"/> sequence instead of needing a live telemetry pump -
    /// see FuelGroundDetectionTests.</summary>
    internal void ProcessSample(ActiveFlightTracker tracker, TelemetrySample sample)
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

        // A rise in total fuel while on the ground is a refuelling event; a fall is defuelling -
        // see FuelUpliftDetector's doc for
        // why defuelling is a non-event rather than a credit. Only evaluated on the ground -
        // airborne fuel loss is just normal burn, not a ground event. tracker.LastGroundFuelKg is
        // seeded by BeginTracking from FlightEndpoints.StartAsync's own reconciliation, so the
        // very first ground sample of a flight is never mistaken for a fresh uplift.
        if (sample.OnGround)
        {
            if (tracker.LastGroundFuelKg is { } previousGroundFuelKg)
            {
                var kind = FuelUpliftDetector.Classify(previousGroundFuelKg, sample.TotalFuelKg);
                if (kind != GroundFuelChangeKind.None)
                {
                    var deltaKg = FuelUpliftDetector.MagnitudeKg(previousGroundFuelKg, sample.TotalFuelKg);
                    FireAndForget(
                        () => HandleGroundFuelChangeAsync(tracker, sample, kind, deltaKg),
                        $"post ground fuel change for flight {tracker.FlightId}");
                }
            }

            tracker.LastGroundFuelKg = sample.TotalFuelKg;
        }

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

        // G8: VATSIM online corroboration. Gated on the SAMPLE's own timestamp (not the real wall
        // clock) at VatsimCheckInterval, same cadence discipline as PositionSnapshotInterval above -
        // never faster than the shared feed cache actually changes (see
        // VatsimFlightCorroborationService's class doc), and, just as importantly, never fired
        // eagerly the instant tracking begins - see RunVatsimCycleAsync's own doc for why that
        // distinction matters. Runs off the hot path via FireAndForget, exactly like the
        // ground-fuel-change and broadcast work above, so a slow/unavailable feed can never stall
        // telemetry processing itself. Fires regardless of whether a CID is already known -
        // RunVatsimCycleAsync resolves that (once) on its own first run.
        if (_vatsimCorroboration is not null && now - tracker.VatsimLastCheckUtc >= VatsimCheckInterval)
        {
            tracker.VatsimLastCheckUtc = now;
            FireAndForget(
                () => RunVatsimCycleAsync(tracker, sample.LatitudeDeg, sample.LongitudeDeg),
                $"VATSIM cycle for flight {tracker.FlightId}");
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

    /// <summary>
    /// Posts (or, for a defuel, silently absorbs) a ground fuel change detected live by
    /// <see cref="ProcessSample"/>. Normally reached via <see cref="FireAndForget"/>, exactly like
    /// <see cref="FinalizeFlightAsync"/>, so a slow DB write never stalls telemetry processing -
    /// internal (rather than private) so tests can await it directly instead of racing a
    /// fire-and-forget background task, the same pattern <see cref="FinalizeFlightAsync"/> already
    /// uses. Resolves the airport from the sample's own position (not necessarily the departure or
    /// arrival airport - a turnaround uplift after landing happens at the arrival, and this stays
    /// correct even for a diversion) rather than assuming which leg of the ground phase the
    /// aircraft is in.
    /// </summary>
    internal async Task HandleGroundFuelChangeAsync(ActiveFlightTracker tracker, TelemetrySample sample, GroundFuelChangeKind kind, double deltaKg)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();

        var flight = await db.Flights.FirstOrDefaultAsync(f => f.Id == tracker.FlightId);
        var fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == tracker.FleetAircraftId);
        if (flight is null || fleetAircraft is null || flight.RevenuePosted)
        {
            // Already finalised (a late sample racing Shutdown, most likely) - never touch a
            // flight's fuel line once it's closed out.
            return;
        }

        // The persisted asset always tracks reality exactly, regardless of direction: the tracked
        // figure must never be allowed to drift silently from what the sim actually reports, or
        // every later fuel charge is computed against a number that is quietly wrong.
        fleetAircraft.FuelOnBoardKg = sample.TotalFuelKg;

        if (kind == GroundFuelChangeKind.Uplift)
        {
            var candidateAirports = await AirportProximityQueries.NearbyAsync(db, sample.LatitudeDeg, sample.LongitudeDeg, CancellationToken.None);
            var resolved = LandingAirportResolver.Resolve(candidateAirports, (sample.LatitudeDeg, sample.LongitudeDeg), tracker.ArrivalIcao);
            var upliftAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == resolved.Icao);
            var airline = await db.Airlines.FirstOrDefaultAsync(a => a.Id == tracker.AirlineId);

            if (upliftAirport is not null && airline is not null)
            {
                var economyConfig = _economyConfigCatalog.Get(airline.Playstyle);
                var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, CancellationToken.None);
                var cost = FlightEconomicsPoster.PostFuelUplift(
                    db, flight, economyConfig, upliftAirport, deltaKg, sample.TimestampUtc, worldSeed);
                flight.TotalCost += cost;
            }
        }
        // Defuel: no ledger line is posted (see FuelUpliftDetector's doc on why this is a
        // deliberate non-event) - the FuelOnBoardKg write above already reflects the new reality.

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// One VATSIM cycle for a tracked flight (G8) - gated by <see cref="ProcessSample"/>'s own
    /// sample-timestamp cadence, never fired eagerly. On its first run for a flight, resolves
    /// <see cref="ActiveFlightTracker.DepartureIcao"/>/<see cref="ActiveFlightTracker.VatsimCid"/>
    /// (see <see cref="ResolveVatsimContextAsync"/>); every run after that skips straight to the
    /// corroboration check (or does nothing further at all, once resolution found no CID - there is
    /// nothing to keep asking about for the rest of this flight). Internal so tests can await it
    /// directly, same pattern as <see cref="HandleGroundFuelChangeAsync"/> and
    /// <see cref="FinalizeFlightAsync"/>. Purely an in-memory accumulation onto the tracker -
    /// nothing is written to the Flight row itself until <see cref="FinalizeFlightAsync"/> records
    /// the final tallies.
    /// </summary>
    internal async Task RunVatsimCycleAsync(ActiveFlightTracker tracker, double latitudeDeg, double longitudeDeg)
    {
        if (_vatsimCorroboration is null)
        {
            // Defensive only - every call site already gates on this being non-null (see
            // ProcessSample). Kept as an explicit early return, rather than the null-forgiving
            // operator, so this stays correct even if a future caller forgets that gate.
            return;
        }

        if (!tracker.VatsimContextResolved)
        {
            await ResolveVatsimContextAsync(tracker);
            tracker.VatsimContextResolved = true;
        }

        if (tracker.VatsimCid is not { } cid)
        {
            // No CID configured (or the flight's route/airline couldn't be resolved) - nothing
            // further to check for the rest of this flight. VatsimLastCheckUtc still advances (set
            // by the caller before this ran), so this doesn't retry every single sample either.
            return;
        }

        var result = await _vatsimCorroboration.CheckAsync(
            cid, latitudeDeg, longitudeDeg, tracker.DepartureIcao, tracker.ArrivalIcao, CancellationToken.None);

        tracker.VatsimChecksTotal++;
        if (result.Matched)
        {
            tracker.VatsimChecksMatched++;
            tracker.VatsimLastCallsign = result.Callsign;
        }

        foreach (var controller in result.RelevantControllers)
        {
            tracker.VatsimControllersWorked.Add(controller);
        }
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
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.Id == flight.AirlineId, ct);
        var settings = airline is not null ? await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == airline.OwnerUserId, ct) : null;
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
            DepartureIcao = route?.DepartureIcao,
            VatsimCid = int.TryParse(settings?.VatsimCid, out var rehydratedCid) && rehydratedCid > 0 ? rehydratedCid : null,
            VatsimContextResolved = true,
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

        // G8: record what the VATSIM corroboration checks (if any ran) found. Left as null on every
        // field when VatsimCid was never resolved (no CID configured, or the flight completed
        // before ResolveVatsimContextAsync's background lookup finished) or when it resolved but no
        // check ever actually ran (an extremely short flight) - "we never checked" must never be
        // recorded as "we checked and it was offline", which is what a bare false would silently claim
        // for every flight ever flown before this feature existed too (see the migration's own note).
        if (tracker.VatsimCid is not null && tracker.VatsimChecksTotal > 0)
        {
            flight.VatsimOnline = tracker.VatsimChecksMatched > 0;
            flight.VatsimOnlineFraction = (double)tracker.VatsimChecksMatched / tracker.VatsimChecksTotal;
            flight.VatsimCallsign = tracker.VatsimChecksMatched > 0 ? tracker.VatsimLastCallsign : null;
            flight.VatsimControllersWorked = tracker.VatsimControllersWorked.Count > 0
                ? string.Join(", ", tracker.VatsimControllersWorked.OrderBy(c => c, StringComparer.Ordinal))
                : null;
        }

        flight.Status = FlightStatus.Completed;

        var fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == tracker.FleetAircraftId);
        if (fleetAircraft is not null)
        {
            // The last position telemetry ever reported for this flight - where it actually ended
            // up, which is not always the planned arrival. Landing somewhere else entirely is a
            // diversion, not a payment failure: the flight completes, pays for the sector actually
            // operated, and leaves the aircraft where it really parked.
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

            // Fetched early (rather than alongside route/arrivalAirport/aircraftType below) because
            // MaintenancePoster needs it regardless of whether this sector turns out to be payable -
            // airframe hours and any resulting A/C-check must still be applied even if, say, the
            // route was deleted mid-flight and the ticket revenue posting below has to skip.
            var airline = await db.Airlines.FirstOrDefaultAsync(a => a.Id == flight.AirlineId);

            // Resolved regardless of whether airline lookup succeeds below - a flight's pilot
            // accrues the hours they flew either way, same as the airframe does in the fallback
            // branch. Pilot.HoursFlown once accrued for virtual pilots and never for the player,
            // so the player's own record sat at zero however much they flew; this closes that.
            var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.Id == flight.PilotId);

            if (airline is not null)
            {
                var economyConfigForCompletion = _economyConfigCatalog.Get(airline.Playstyle);
                MaintenancePoster.PostFlightHours(db, fleetAircraft, pilot, airline, economyConfigForCompletion, flightHours, completionUtc);

                // Reputation, from on-time performance and landing quality.
                // Excluded entirely (no event at all) for a slew/position-jump-detected flight, same
                // "structurally invalid sector" gate FlightEconomicsPoster applies to revenue - see
                // Flight.SlewDetected/PositionJumpDetected's own docs. On-time is additionally
                // excluded (landing still counts) when SimRateElevated, since block-time measured
                // under an accelerated clock means nothing - see Flight.SimRateElevated's own doc,
                // which explicitly anticipates exactly this reputation feature not existing yet.
                if (!flight.SlewDetected && !flight.PositionJumpDetected)
                {
                    double? delayMinutes = flight.SimRateElevated ? null : flightHours * 60.0 - flight.PlannedBlockMinutes;
                    ReputationPoster.PostCompletedFlight(airline, economyConfigForCompletion, delayMinutes, flight.LandingFpmFirst);
                }
            }
            else
            {
                fleetAircraft.AirframeHours += flightHours;
                if (pilot is not null)
                {
                    pilot.HoursFlown += flightHours;
                }
            }

            // The persisted fuel asset the NEXT flight (or this aircraft's return leg) starts
            // from: an aircraft that lands with 3,000 kg starts its next sector with 3,000 kg,
            // already paid for. Prefers the live
            // snapshot's last reported reading (what a real telemetry-tracked flight leaves
            // behind); falls back to tracker.LastFuelKg for a synthetic/test tracker built
            // without ever running a sample through ProcessSample.
            var finalFuelKg = tracker.LatestSnapshot?.FuelRemainingKg ?? tracker.LastFuelKg;
            fleetAircraft.FuelOnBoardKg = Math.Max(0, finalFuelKg);

            // Landing/handling/parking/passenger/turnaround fees are charged at wherever the
            // aircraft actually landed (landing.Icao), not necessarily the planned arrival - a
            // diversion still incurs real ground-service costs at the airport it used. Fuel was
            // already charged at uplift (flight start); this posts every other line, or nothing
            // at all if the sector isn't payable (see FlightEconomicsPoster.PostCompletionAsync).
            // Quietly skips if any of the data it needs can't be resolved - better to post
            // nothing than guess.
            var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == flight.RouteId);
            var arrivalAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == landing.Icao);
            var aircraftType = await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId]);

            if (route is not null && airline is not null && arrivalAirport is not null && aircraftType is not null)
            {
                var economyConfig = _economyConfigCatalog.Get(airline.Playstyle);
                var economicsResult = await FlightEconomicsPoster.PostCompletionAsync(
                    db, flight, airline, route, aircraftType, arrivalAirport, economyConfig, flightHours, completionUtc, CancellationToken.None);

                // G12: the modest online-flying bonus. Only for a sector that actually got priced
                // above (a slew/position-jump flight, or one PostCompletionAsync otherwise declined
                // to post revenue for, returns null and earns no bonus - there is nothing to be
                // "extra" on top of), and only once corroborated online for at least the configured
                // minimum fraction of the flight - a CID merely being configured, or briefly logging
                // on, is never enough. Computed and posted entirely server-side from what
                // FlightLifecycleService itself recorded above; nothing here trusts a client-supplied
                // claim of having flown online.
                if (economicsResult is not null && flight.VatsimOnline == true &&
                    flight.VatsimOnlineFraction is { } onlineFraction &&
                    onlineFraction >= economyConfig.VatsimOnlineBonus.MinimumOnlineFraction)
                {
                    var bonus = FlightEconomicsPoster.PostVatsimOnlineBonus(
                        db, flight, economyConfig, economicsResult.TicketRevenue, completionUtc);
                    if (bonus > 0)
                    {
                        flight.Revenue += bonus;
                        ReputationPoster.PostVatsimOnlineBonus(airline, economyConfig);
                    }
                }
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
            tracker.PendingReconnect, sample.TimestampUtc, sample.TrueHeadingDeg);
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

        /// <summary>
        /// Baseline for live ground-fuel-change detection (see
        /// <see cref="FlightLifecycleService.ProcessSample"/>) - the last fuel reading taken while
        /// on the ground. Seeded by <see cref="BeginTracking"/> from
        /// <c>FlightEndpoints.StartAsync</c>'s own reconciliation; null after a rehydrate (see
        /// <see cref="RehydrateInProgressFlightAsync"/>), since the true baseline at that point is
        /// unknown - the first ground sample after reconnecting simply establishes it rather than
        /// firing a (potentially spurious) event.
        /// </summary>
        public double? LastGroundFuelKg { get; set; }

        public DateTimeOffset LastSnapshotUtc { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset LastBroadcastUtc { get; set; } = DateTimeOffset.MinValue;

        public LiveFlightSnapshot? LatestSnapshot { get; set; }

        public bool PendingReconnect { get; set; }

        public DateTimeOffset? PendingReconnectSinceUtc { get; set; }

        public (double Lat, double Lon)? LastKnownPosition { get; set; }

        // --- G8: VATSIM online corroboration - see VatsimFlightCorroborationService ---

        /// <summary>Resolved after construction (see <see cref="ResolveVatsimContextAsync"/> and
        /// the rehydrate path) rather than passed in, so <see cref="BeginTracking"/>'s signature -
        /// and every call site that calls it - never needed to change. Null until resolved, or if
        /// this flight's route couldn't be found.</summary>
        public string? DepartureIcao { get; set; }

        /// <summary>Whether <see cref="DepartureIcao"/>/<see cref="VatsimCid"/> have been resolved
        /// (attempted) at least once - see <see cref="FlightLifecycleService.RunVatsimCycleAsync"/>.
        /// True immediately for a rehydrated flight (resolved synchronously during rehydrate, which
        /// already has a DB scope open) - see <see cref="RehydrateInProgressFlightAsync"/>.</summary>
        public bool VatsimContextResolved { get; set; }

        /// <summary>The player's configured VATSIM CID, or null if none is set - see
        /// <see cref="ResolveVatsimContextAsync"/>. Null disables corroboration entirely for this
        /// flight: <see cref="FlightLifecycleService.ProcessSample"/> never even attempts a check
        /// without this.</summary>
        public int? VatsimCid { get; set; }

        public int VatsimChecksTotal { get; set; }

        public int VatsimChecksMatched { get; set; }

        /// <summary>The callsign last seen for this CID on a MATCHED check - not merely "seen", so a
        /// stale callsign from a miss (CID online, elsewhere) never gets recorded as this flight's own.</summary>
        public string? VatsimLastCallsign { get; set; }

        public HashSet<string> VatsimControllersWorked { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset VatsimLastCheckUtc { get; set; } = DateTimeOffset.MinValue;
    }
}

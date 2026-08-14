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
        _telemetry.TelemetryInterrupted += OnTelemetryInterrupted;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _telemetry.SampleReceived -= OnSample;
        _telemetry.TelemetryInterrupted -= OnTelemetryInterrupted;
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
    /// Starts live tracking for a flight the caller has already created and saved. Fuel burn is
    /// measured from the first sample where the ENGINES are actually running (see
    /// <see cref="ActiveFlightTracker.EngineStartFuelKg"/>), not from flight start - nothing needs
    /// seeding here any more, since billing no longer depends on a ground-uplift baseline.
    /// </summary>
    /// <param name="departurePosition">
    /// Where the aircraft is expected to be sitting as tracking starts - the route's departure
    /// airport. Optional (and defaulted, so existing call sites are unaffected) purely because not
    /// every caller can resolve it; when it IS supplied, the integrity monitor can tell a garbage
    /// opening fix from a real teleport instead of having to fail open on both. See
    /// <see cref="FlightIntegrityMonitor"/>.
    /// </param>
    public void BeginTracking(
        Guid flightId, Guid airlineId, Guid fleetAircraftId, string arrivalIcao, int plannedBlockMinutes,
        (double Lat, double Lon)? departurePosition = null)
    {
        var tracker = new ActiveFlightTracker
        {
            FlightId = flightId,
            AirlineId = airlineId,
            FleetAircraftId = fleetAircraftId,
            ArrivalIcao = arrivalIcao,
            PlannedBlockMinutes = plannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
            IntegrityMonitor = new FlightIntegrityMonitor(departurePosition),
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
    /// background database task in this class (the broadcast/finalize work) is gated behind
    /// <see cref="ProcessSample"/>'s own cadence, driven by
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

    /// <summary>
    /// Installs a hand-built tracker as the actively-tracked flight, bypassing
    /// <see cref="BeginTracking"/>'s own construction - lets a test drive a specific
    /// <see cref="ActiveFlightTracker"/> through <see cref="ProcessSample"/> (with whatever engine
    /// state and fuel readings it wants) and then exercise <c>FlightEndpoints.AbandonAsync</c>
    /// against it, the same way <see cref="SimTelemetryService.SetLastSampleForTests"/> lets a test
    /// script a telemetry reading without a real sim. See FuelBurnBillingTests.
    /// </summary>
    internal void SetActiveTrackerForTests(ActiveFlightTracker tracker)
    {
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

    /// <summary>
    /// What a normal completion would bill this flight's burn as right now, if it is still the
    /// actively tracked one - <see cref="FSOps.Core.Flights.FuelBurnResolver.Measure"/> applied to
    /// whatever this tracker has observed so far. Null when never tracked far enough to receive a
    /// usable reading (or no longer the active flight), exactly like <see cref="GetActiveSnapshot"/>.
    /// Used by <c>FlightEndpoints.AbandonAsync</c> to bill whatever was actually burned up to the
    /// abandon point, through the same <see cref="FSOps.Core.Flights.FuelBurnResolver.Resolve"/>
    /// guard a normal completion uses (with a zero, rather than planned, fallback - see that
    /// method's own doc for why).
    /// </summary>
    public double? GetActiveMeasuredBurnKg(Guid flightId)
    {
        lock (_lock)
        {
            if (_active?.FlightId != flightId)
            {
                return null;
            }

            return FuelBurnResolver.Measure(
                _active.EngineStartFuelKg, _active.AccumulatedBurnKg, _active.FirstSampleFuelKg, _active.LastFuelKg);
        }
    }

    /// <summary>
    /// The sim link dropped and came back mid-sector. The integrity monitor is judging each sample
    /// against the one before it, and those two are now separated by a hole it cannot see - a
    /// reconnecting SimConnect replays the position it last had, which bridges the gap in the
    /// timestamps, so when the truth finally arrives the aircraft has genuinely moved miles and the
    /// transition reads as a teleport with the whole flight's corroboration standing behind it.
    /// Telling the monitor is the same reset the acquisition gate already gets, and for the same
    /// reason: see <see cref="FlightIntegrityMonitor.NotifyTelemetryInterrupted"/>.
    /// </summary>
    private void OnTelemetryInterrupted(object? sender, EventArgs e)
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

        tracker.IntegrityMonitor.NotifyTelemetryInterrupted();
        _logger.LogInformation(
            "Telemetry for flight {FlightId} was interrupted and has resumed; speed across the gap is not measurable and is not being judged.",
            tracker.FlightId);
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

        // Fuel-burn tracking. See FinalizeFlightAsync (a normal completion) and AbandonAsync (an
        // early abandon) for where this turns into an actual bill via FuelBurnResolver. No live
        // ground-event detection is needed any more: a mid-sector refuel is just a rise the
        // accumulation below is naturally immune to, not something that needs catching and billed
        // the moment it happens.
        tracker.FirstSampleFuelKg ??= sample.TotalFuelKg;

        if (tracker.EngineStartFuelKg is null)
        {
            // Nothing is baselined until the engines are genuinely running - between "Start
            // flight" and engine start, the tank can move for reasons that are not burn at all
            // (MSFS's own spawn load, a menu fuel set, a ground-crew uplift before startup), and none of
            // that may ever be read as burn. Locks in exactly once, at whatever this very sample
            // reads - not tracker.LastFuelKg, which could differ if the tank changed between the
            // last pre-start sample and this one.
            if (sample.EngineRunning)
            {
                tracker.EngineStartFuelKg = sample.TotalFuelKg;
            }
        }
        else if (sample.EngineRunning && sample.TotalFuelKg < tracker.LastFuelKg)
        {
            // Accumulate only a decrease seen WHILE THE ENGINES ARE RUNNING - a rise at any point
            // is already excluded (it contributes nothing to the running sum, rather than
            // corrupting a single start-minus-end subtraction), and the mirror case matters just
            // as much: a decrease with the engines OFF is not burn either - it's a defuel, a menu
            // change, or ground-crew activity during a turnaround stop, and billing it as burn
            // would charge the player for fuel that was never actually flown off. The baseline
            // itself is still only ever set once (above) - a later shutdown/restart (a single-
            // engine taxi stop, say) doesn't reset tracking, it just stops contributing while off
            // and resumes once running again, because tracker.LastFuelKg is still updated on every
            // sample regardless of engine state (below), so the comparison at the next
            // engine-running sample starts fresh from wherever the tank was when it restarted,
            // never re-surfacing what happened while shut down.
            tracker.AccumulatedBurnKg += tracker.LastFuelKg - sample.TotalFuelKg;
        }

        tracker.LastFuelKg = sample.TotalFuelKg;

        // Remember the aircraft the sim says is being flown, so a flight that started before
        // SimConnect had delivered an identity can still be matched at finalisation - see
        // ActiveFlightTracker.LastAircraftTitle.
        if (sample.AircraftTitle is { Length: > 0 })
        {
            tracker.LastAircraftTitle = sample.AircraftTitle;
            tracker.LastAtcModel = sample.AtcModel;
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
            var payload = new TouchdownPayload(
                touchdown.LatitudeDeg, touchdown.LongitudeDeg, touchdown.TrueHeadingDeg,
                touchdown.Fpm, touchdown.GForce, bounceIndex, touchdown.FpmSource);
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
    /// One VATSIM cycle for a tracked flight (G8) - gated by <see cref="ProcessSample"/>'s own
    /// sample-timestamp cadence, never fired eagerly. On its first run for a flight, resolves
    /// <see cref="ActiveFlightTracker.DepartureIcao"/>/<see cref="ActiveFlightTracker.VatsimCid"/>
    /// (see <see cref="ResolveVatsimContextAsync"/>); every run after that skips straight to the
    /// corroboration check (or does nothing further at all, once resolution found no CID - there is
    /// nothing to keep asking about for the rest of this flight). Internal so tests can await it
    /// directly, same pattern as
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
            // Where this flight was last actually seen is a better "expected start" than the
            // departure airport for a rehydrated flight - it may already be halfway to its
            // destination - and it is exactly what the reconnect check below compares against too.
            IntegrityMonitor = new FlightIntegrityMonitor(lastKnown),
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
        // A courtesy default in case route/aircraft data can't be resolved below (fleetAircraft
        // deleted mid-flight, say) - overwritten with the actual billed figure once the fuel-burn
        // block further down can compute the sector's own planned fallback properly.
        flight.FuelUsedKg = Math.Max(0, FuelBurnResolver.Measure(tracker.EngineStartFuelKg, tracker.AccumulatedBurnKg, tracker.FirstSampleFuelKg, tracker.LastFuelKg) ?? 0);
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

        // Nothing may mark a sector finished and unpayable in the same breath. The economics block
        // below needs the route (and the airline) and quietly skips if it cannot resolve them - which
        // was survivable while Completed was set afterwards, and became a silent loss once it was set
        // here first: a route deleted mid-flight left a cleanly flown sector Completed with no
        // revenue, no fees, RevenuePosted still false, and manual completion refusing it for no
        // longer being InProgress. There was no way back through the UI.
        //
        // So the flight stays InProgress when the data its pay depends on is missing. That is
        // recoverable - the route is only soft-deleted, and completing manually pays it once it can
        // be resolved again - where Completed-and-unpaid is not. Deleting a route with a flight on it
        // is now refused outright (see RouteEndpoints.DeleteAsync), so this is the second line rather
        // than the first, and it covers the whole class: the airline, the aircraft type and the
        // arrival airport can all fail to resolve the same way.
        var routeForPay = await db.Routes.FirstOrDefaultAsync(r => r.Id == flight.RouteId);
        var airlineForPay = await db.Airlines.FirstOrDefaultAsync(a => a.Id == flight.AirlineId);
        if (routeForPay is null || airlineForPay is null)
        {
            _logger.LogError(
                "Flight {FlightId} finished but its {Missing} could not be resolved, so it cannot be paid. Leaving it in progress rather than completing it unpaid - it can be completed manually once the missing record is restored.",
                flight.Id,
                routeForPay is null ? "route" : "airline");
            await db.SaveChangesAsync();
            return;
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

            // Informational only now (see FleetAircraft.FuelOnBoardKg's own doc) - what the Fleet
            // page and report card show as "fuel on board" for the next sector, carrying no
            // billing weight of its own. Prefers the live snapshot's last reported reading (what a
            // real telemetry-tracked flight leaves behind); falls back to tracker.LastFuelKg for a
            // synthetic/test tracker built without ever running a sample through ProcessSample.
            var finalFuelKg = tracker.LatestSnapshot?.FuelRemainingKg ?? tracker.LastFuelKg;
            fleetAircraft.FuelOnBoardKg = Math.Max(0, finalFuelKg);

            // Landing/handling/parking/passenger/turnaround fees are charged at wherever the
            // aircraft actually landed (landing.Icao), not necessarily the planned arrival - a
            // diversion still incurs real ground-service costs at the airport it used. Fuel is
            // billed separately below, at the DEPARTURE airport's price, on what was actually
            // burned. Quietly skips if any of the data it needs can't be resolved - better to
            // post nothing than guess.
            var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == flight.RouteId);
            var arrivalAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == landing.Icao);
            var aircraftType = await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId]);

            // Aircraft identity is captured once, when the flight starts - and SimConnect very often
            // has not delivered it by then, because FSOps normally connects while MSFS is still in
            // the menu. That left TitleFlown empty and TypeMismatch null, which does not mean "the
            // types matched", it means the comparison never ran. Backfill from what the samples
            // actually reported. Informational only, exactly as at flight start: a mismatch is
            // flagged for the player's information and is never penalised financially.
            if (flight.TitleFlown.Length == 0 && tracker.LastAircraftTitle is { Length: > 0 } observedTitle && aircraftType is not null)
            {
                flight.TitleFlown = observedTitle;
                flight.TypeMismatch = AircraftTypeMatcher.HasAircraftData(observedTitle, tracker.LastAtcModel)
                    ? !AircraftTypeMatcher.IsMatch(aircraftType.MatchPatterns, observedTitle, tracker.LastAtcModel)
                    : null;
            }

            if (route is not null && airline is not null && arrivalAirport is not null && aircraftType is not null)
            {
                var economyConfig = _economyConfigCatalog.Get(airline.Playstyle);

                // Fuel is charged unconditionally here, ahead of the payable check
                // PostCompletionAsync applies below - fuel actually burned is a real cost whether
                // or not the sector turns out to be payable (a slew/position-jump sector still
                // keeps its fuel cost, same as it always has). Priced against the ORIGINALLY
                // PLANNED arrival (tracker.ArrivalIcao) rather than landing.Icao where they differ
                // (a diversion) - a diverted sector still burned roughly what the planned sector
                // was expected to, so that stays the honest planning basis for the fallback figure
                // even though every ground fee above is correctly charged at wherever the aircraft
                // actually ended up.
                var departureAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao);
                if (departureAirport is not null)
                {
                    var plannedArrivalAirport = string.Equals(landing.Icao, tracker.ArrivalIcao, StringComparison.OrdinalIgnoreCase)
                        ? arrivalAirport
                        : await db.Airports.FirstOrDefaultAsync(a => a.Icao == tracker.ArrivalIcao) ?? arrivalAirport;
                    var plan = RoutePreviewCalculator.Calculate(economyConfig, departureAirport, plannedArrivalAirport, aircraftType, airline.StrategyProfile);
                    var plannedChargedFuelKg = plan.FuelBreakdown.ChargedFuelKg;

                    var measuredBurnKg = FuelBurnResolver.Measure(tracker.EngineStartFuelKg, tracker.AccumulatedBurnKg, tracker.FirstSampleFuelKg, tracker.LastFuelKg);
                    var resolution = FuelBurnResolver.Resolve(measuredBurnKg, plannedChargedFuelKg, plannedChargedFuelKg);
                    if (resolution.UsedFallback && measuredBurnKg is not null)
                    {
                        _logger.LogWarning(
                            "Flight {FlightId} had an unusable measured fuel burn ({MeasuredKg:F0} kg) - billed the planned {PlannedKg:F0} kg instead.",
                            flight.Id, measuredBurnKg.Value, plannedChargedFuelKg);
                    }

                    flight.FuelUsedKg = resolution.BilledKg;
                    var fuelWorldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, CancellationToken.None);
                    var fuelCost = FlightEconomicsPoster.PostFuelBurn(db, flight, economyConfig, departureAirport, resolution.BilledKg, completionUtc, fuelWorldSeed);
                    flight.TotalCost += fuelCost;
                }

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

        /// <summary>Settable at construction so <see cref="BeginTracking"/> (and the rehydrate path)
        /// can seed it with where the aircraft is expected to be - see
        /// <see cref="FlightIntegrityMonitor"/>'s constructor. Defaults to a monitor with no expected
        /// start, which is what every hand-built test tracker gets.</summary>
        public FlightIntegrityMonitor IntegrityMonitor { get; init; } = new();

        /// <summary>The very first fuel reading this tracker received, regardless of engine state -
        /// the tier-2 fallback baseline (see <see cref="FSOps.Core.Flights.FuelBurnResolver.Measure"/>)
        /// used only when the engines were never observed running at all (a sim that connected
        /// late, or a telemetry gap that missed the genuine engine-start sample). Null until the
        /// first sample arrives (and permanently null after a rehydrate that never resumes - see
        /// <see cref="RehydrateInProgressFlightAsync"/>).</summary>
        public double? FirstSampleFuelKg { get; set; }

        /// <summary>The fuel reading at the first sample where the engines were observed genuinely
        /// running - the real burn baseline (see <see cref="FSOps.Core.Flights.FuelBurnResolver.Measure"/>'s
        /// tier 1). Locked in exactly once and never moved again, including across a later
        /// shutdown/restart mid-sector: everything from this point on is burn-tracking territory,
        /// continuous regardless of momentary engine state. Null until the engines are first seen
        /// running - deliberately NOT the same as <see cref="FirstSampleFuelKg"/>, since between
        /// "Start flight" and engine start the tank can move for reasons that are not burn (a
        /// spawn load, a menu fuel set, a ground-crew uplift before startup).</summary>
        public double? EngineStartFuelKg { get; set; }

        /// <summary>Running sum of every fuel DECREASE observed WHILE THE ENGINES WERE RUNNING,
        /// since <see cref="EngineStartFuelKg"/> was set - never a rise (immune to a mid-sector
        /// top-up at any point, without needing to detect or classify it specially), and never a
        /// decrease seen with the engines off either (a defuel, a menu change, ground-crew activity
        /// during a turnaround - none of that is burn, and billing it as burn would charge the
        /// player for fuel that was never actually flown off). See
        /// <see cref="FSOps.Core.Flights.FuelBurnResolver.Measure"/>'s own doc. Always &gt;= 0 by
        /// construction.</summary>
        public double AccumulatedBurnKg { get; set; }

        /// <summary>The most recent fuel reading, in any engine state - used both as the previous
        /// term in <see cref="AccumulatedBurnKg"/>'s running sum and as the tier-2 fallback's "end"
        /// reading.</summary>
        public double LastFuelKg { get; set; }

        /// <summary>The most recent non-empty aircraft title reported by the sim, and the ATC model
        /// alongside it. Null until the sim has told us anything - which is normal for a while at the
        /// start of a flight, since FSOps usually connects before MSFS has loaded an aircraft. Used
        /// by <see cref="FinalizeFlightAsync"/> to backfill a flight whose identity was still unknown
        /// when it started.</summary>
        public string? LastAircraftTitle { get; set; }

        public string? LastAtcModel { get; set; }

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

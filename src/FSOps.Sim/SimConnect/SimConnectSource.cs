using System.Threading.Channels;
using CTrue.FsConnect;
using Microsoft.Extensions.Logging;

namespace FSOps.Sim.SimConnect;

/// <summary>
/// Talks to a running copy of MSFS 2024 via CTrue.FsConnect. Retries every
/// <see cref="SimConnectSourceOptions.ReconnectInterval"/> while the sim is not running, connects
/// cleanly once it appears, and survives the sim being closed and reopened without leaking
/// handles - each attempt gets a brand new <see cref="FsConnect"/> instance, and the previous one
/// is always disposed before a retry.
/// </summary>
public sealed class SimConnectSource : ISimSource
{
    private enum Definition
    {
        Telemetry = 0,
        AircraftIdentity = 1,
    }

    private enum Request
    {
        Telemetry = 0,
        AircraftIdentity = 1,
    }

    // SIMCONNECT_OBJECT_ID_USER - always 0, the user's own aircraft.
    private const uint UserObjectId = 0;
    private const string AppName = "FSOps";

    private readonly SimConnectSourceOptions _options;
    private readonly ILogger<SimConnectSource> _logger;
    private readonly Channel<TelemetrySample> _channel;

    private FsConnect? _activeConnection;
    private CancellationTokenSource? _cts;
    private Task? _connectionLoopTask;
    private SimConnectionState _connectionState = SimConnectionState.Disconnected;
    private bool _lowAltitudeMode;
    private string _aircraftTitle = string.Empty;
    private string _aircraftAtcModel = string.Empty;
    private string _aircraftAtcType = string.Empty;
    /// <summary>Last connection-failure type+message, so an unchanging failure is only reported once.</summary>
    private string? _lastFailureSignature;

    /// <summary>
    /// Non-zero once <see cref="DisposeAsync"/> has run. Disposal here MUST be idempotent, and so
    /// must a <see cref="StopAsync"/> that arrives after it, because this instance is captured for
    /// disposal by the DI container more than once: Program.cs registers it as the
    /// <c>ISimSource</c> singleton, and <c>SimTelemetryService</c> - itself registered both as a
    /// singleton and as a hosted service resolving that same singleton - disposes it as well. The
    /// container adds an instance to its disposal list once per service descriptor, so the same
    /// object really is disposed twice on shutdown.
    ///
    /// <para>Before this guard existed, the second pass reached <c>_cts.Cancel()</c> on a
    /// <see cref="CancellationTokenSource"/> the first pass had already disposed.
    /// <see cref="CancellationTokenSource.Cancel()"/> throws <see cref="ObjectDisposedException"/>
    /// in that state, and it threw from inside <c>Host.DisposeAsync()</c> - past the last catch
    /// block in the process - so every clean exit terminated with an unhandled exception. Nine of
    /// those were recorded in the Windows Application event log across three days. Worse, the
    /// throw aborted the container's disposal loop part-way, so the remaining disposables -
    /// Serilog's logger among them - were never flushed, and the death left nothing in the app's
    /// own log.</para>
    ///
    /// <para>This is a flag over the whole method rather than a null-out of <c>_cts</c> on
    /// purpose: double disposal is a property of how the class is registered, not of this one
    /// field, so the class has to tolerate it outright. Guarding only the field that happened to
    /// throw would leave the next field added here to reintroduce exactly the same crash.</para>
    /// </summary>
    private int _disposed;

    public SimConnectSource(SimConnectSourceOptions options, ILogger<SimConnectSource> logger)
    {
        _options = options;
        _logger = logger;
        _channel = SimTelemetryChannel.Create();
    }

    public string Kind => "SimConnect";

    public SimConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (_connectionState == value)
            {
                return;
            }

            _connectionState = value;
            ConnectionStateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<SimConnectionState>? ConnectionStateChanged;

    public AircraftIdentity? CurrentAircraft =>
        _aircraftTitle.Length == 0 ? null : new AircraftIdentity(_aircraftTitle, _aircraftAtcModel, _aircraftAtcType);

    public ChannelReader<TelemetrySample> Telemetry => _channel.Reader;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _connectionLoopTask = Task.Run(() => ConnectionLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Already disposed means already stopped - there is no loop left to cancel and the token
        // source is gone. See _disposed for why this is reachable at all.
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _cts?.Cancel();

        if (_connectionLoopTask is not null)
        {
            try
            {
                await _connectionLoopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Claim disposal exactly once. Everything after this point runs on the first call only,
        // so a second (or third) DisposeAsync is a no-op rather than a process-killing throw.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // StopAsync would now short-circuit on the flag, so do its work here instead.
        _cts?.Cancel();

        if (_connectionLoopTask is not null)
        {
            try
            {
                await _connectionLoopTask.WaitAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts?.Dispose();
    }

    /// <summary>
    /// Owns the whole connection lifecycle for the process's lifetime: connect, stay connected
    /// until the sim quits or the link errors out, clean up, wait, retry. Nothing in here may
    /// throw out to the caller - a failed attempt just means another retry.
    /// </summary>
    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var disconnectedSignal = new SemaphoreSlim(0, 1);
            FsConnect? fsConnect = null;

            try
            {
                ConnectionState = SimConnectionState.Connecting;

                fsConnect = new FsConnect();
                _activeConnection = fsConnect;

                fsConnect.ConnectionChanged += (_, connected) =>
                {
                    if (connected)
                    {
                        ConnectionState = SimConnectionState.Connected;
                        // Reset so that if the link later breaks for a new reason, that reason is
                        // reported rather than suppressed as a repeat of something long resolved.
                        _lastFailureSignature = null;
                        _logger.LogInformation("SimConnect connected to the simulator.");
                    }
                    else
                    {
                        TryRelease(disconnectedSignal);
                    }
                };
                fsConnect.FsDataReceived += OnFsDataReceived;
                fsConnect.FsError += (_, e) =>
                    _logger.LogWarning("SimConnect reported {ExceptionCode} (send #{SendId}).", e.ExceptionCode, e.SendID);
                fsConnect.AircraftLoaded += (_, _) => RequestAircraftIdentity(fsConnect);

                // Config index 0 - the local, same-machine connection. Throws if MSFS is not
                // running or hasn't finished starting up yet.
                //
                // This MUST come before RegisterDataDefinition. FsConnect forwards a definition
                // straight to the underlying SimConnect handle, which does not exist until the
                // connection is open - registering first throws NullReferenceException on every
                // attempt, whether or not the sim is running. That is exactly what happened here:
                // the calls were the other way round, so the link never once established and the
                // failure was invisible because it was only logged at Debug.
                fsConnect.Connect(AppName, 0);

                fsConnect.RegisterDataDefinition<TelemetryData>(Definition.Telemetry);
                fsConnect.RegisterDataDefinition<AircraftIdentityData>(Definition.AircraftIdentity);

                _lowAltitudeMode = false;
                fsConnect.RequestDataOnSimObject(
                    Request.Telemetry, Definition.Telemetry, UserObjectId,
                    FsConnectPeriod.SimFrame, FsConnectDRequestFlag.Default,
                    0, _options.NormalSimFrameInterval, 0);
                RequestAircraftIdentity(fsConnect);

                // Blocks here for as long as the connection is up - FsConnect delivers everything
                // through the events above on its own background thread, so there is nothing else
                // to pump. Released the moment ConnectionChanged reports the link is gone.
                await disconnectedSignal.WaitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The overwhelmingly common case here is simply "MSFS is not running yet", which
                // is not worth a log entry every five seconds. But staying silent forever hid a
                // real defect for an entire chunk - the connection failed identically on every
                // attempt and nothing ever said so. So: report the first failure, and any change
                // of failure type, at Warning; only the repeats drop to Debug.
                var signature = $"{ex.GetType().FullName}:{ex.Message}";
                if (signature != _lastFailureSignature)
                {
                    _lastFailureSignature = signature;
                    _logger.LogWarning(ex, "SimConnect connection attempt failed; retrying every {Seconds}s.",
                        _options.ReconnectInterval.TotalSeconds);
                }
                else
                {
                    _logger.LogDebug(ex, "SimConnect connection attempt failed (repeat).");
                }
            }
            finally
            {
                _activeConnection = null;
                _aircraftTitle = string.Empty;
                _aircraftAtcModel = string.Empty;
                _aircraftAtcType = string.Empty;

                if (fsConnect is not null)
                {
                    try
                    {
                        fsConnect.Dispose();
                    }
                    catch
                    {
                        // Best-effort - the sim closing out from under us can leave this unhappy.
                    }
                }

                ConnectionState = SimConnectionState.Disconnected;
            }

            try
            {
                await Task.Delay(_options.ReconnectInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void TryRelease(SemaphoreSlim signal)
    {
        try
        {
            if (signal.CurrentCount == 0)
            {
                signal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // The connection attempt already finished cleaning up; nothing left to signal.
        }
    }

    private static void RequestAircraftIdentity(FsConnect fsConnect) =>
        fsConnect.RequestDataOnSimObject(
            Request.AircraftIdentity, Definition.AircraftIdentity, UserObjectId,
            FsConnectPeriod.Once, FsConnectDRequestFlag.Default, 0, 0, 0);

    private void OnFsDataReceived(object? sender, FsDataReceivedEventArgs e)
    {
        if (e.Data.Count == 0)
        {
            return;
        }

        if (e.RequestId == (uint)Request.Telemetry && e.Data[0] is TelemetryData telemetry)
        {
            HandleTelemetry(telemetry);
        }
        else if (e.RequestId == (uint)Request.AircraftIdentity && e.Data[0] is AircraftIdentityData identity)
        {
            _aircraftTitle = identity.Title.Trim();
            _aircraftAtcModel = identity.AtcModel.Trim();
            _aircraftAtcType = identity.AtcType.Trim();
        }
    }

    private void HandleTelemetry(TelemetryData data)
    {
        var sample = new TelemetrySample(
            DateTimeOffset.UtcNow,
            data.Latitude,
            data.Longitude,
            data.AltitudeMsl,
            data.AltitudeAgl,
            data.IndicatedAirspeed,
            data.GroundSpeed,
            data.VerticalSpeed,
            data.HeadingTrue,
            data.HeadingMagnetic,
            data.OnGround != 0,
            data.EngineCombustion != 0,
            data.ParkingBrake != 0,
            data.GForce,
            data.TouchdownNormalVelocity,
            data.FuelTotalWeightKg,
            _aircraftTitle,
            _aircraftAtcModel,
            _aircraftAtcType,
            data.SimulationRate,
            data.IsSlewActive != 0);

        _channel.Writer.TryWrite(sample);

        AdjustSamplingRate(data.AltitudeAgl);
    }

    /// <summary>
    /// Adaptive sampling: re-issuing RequestDataOnSimObject changes the interval of an existing
    /// subscription in place. Normally it fires every <see cref="SimConnectSourceOptions.NormalSimFrameInterval"/>th
    /// sim frame (roughly 5 Hz); below the low-altitude threshold it drops to every frame for
    /// landing fidelity, with hysteresis so hovering near the threshold does not thrash it.
    /// </summary>
    private void AdjustSamplingRate(double altitudeAglFt)
    {
        var fsConnect = _activeConnection;
        if (fsConnect is null)
        {
            return;
        }

        if (!_lowAltitudeMode && altitudeAglFt < _options.LowAltitudeThresholdAglFt)
        {
            _lowAltitudeMode = true;
            fsConnect.RequestDataOnSimObject(
                Request.Telemetry, Definition.Telemetry, UserObjectId,
                FsConnectPeriod.SimFrame, FsConnectDRequestFlag.Default, 0, 1, 0);
        }
        else if (_lowAltitudeMode && altitudeAglFt > _options.LowAltitudeThresholdAglFt + _options.LowAltitudeHysteresisFt)
        {
            _lowAltitudeMode = false;
            fsConnect.RequestDataOnSimObject(
                Request.Telemetry, Definition.Telemetry, UserObjectId,
                FsConnectPeriod.SimFrame, FsConnectDRequestFlag.Default, 0, _options.NormalSimFrameInterval, 0);
        }
    }
}

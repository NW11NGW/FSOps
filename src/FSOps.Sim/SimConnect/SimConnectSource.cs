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
        await StopAsync(CancellationToken.None);
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

                fsConnect.RegisterDataDefinition<TelemetryData>(Definition.Telemetry);
                fsConnect.RegisterDataDefinition<AircraftIdentityData>(Definition.AircraftIdentity);

                // Config index 0 - the local, same-machine connection. Throws if MSFS is not
                // running or hasn't finished starting up yet.
                fsConnect.Connect(AppName, 0);

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
                // is not an error worth a full log entry every five seconds.
                _logger.LogDebug(ex, "SimConnect connection attempt failed.");
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
            _aircraftAtcType);

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

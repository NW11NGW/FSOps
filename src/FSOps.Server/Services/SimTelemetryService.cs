using FSOps.Server.Hubs;
using FSOps.Sim;
using Microsoft.AspNetCore.SignalR;

namespace FSOps.Server.Services;

/// <summary>
/// Owns whichever <see cref="ISimSource"/> was selected at startup, pumps its telemetry onto the
/// live hub, and exposes the current connection state for the heartbeat and the sim status
/// endpoint to read. Registered as both a singleton (so those can inject it directly) and a
/// hosted service (so the host starts/stops it with everything else).
/// </summary>
public sealed class SimTelemetryService : IHostedService, IAsyncDisposable
{
    // The sim source can produce telemetry far faster than any browser needs to see it - full
    // rate stays server-side, and only one sample per this interval is broadcast over SignalR.
    private static readonly TimeSpan BroadcastInterval = TimeSpan.FromMilliseconds(500);

    private readonly ISimSource _source;
    private readonly IHubContext<LiveHub> _hub;
    private readonly ILogger<SimTelemetryService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private DateTimeOffset _lastBroadcastUtc = DateTimeOffset.MinValue;

    public SimTelemetryService(ISimSource source, IHubContext<LiveHub> hub, ILogger<SimTelemetryService> logger)
    {
        _source = source;
        _hub = hub;
        _logger = logger;
    }

    public string SourceKind => _source.Kind;

    public SimConnectionState ConnectionState => _source.ConnectionState;

    public string? AircraftTitle => _source.CurrentAircraft?.Title;

    public AircraftIdentity? CurrentAircraft => _source.CurrentAircraft;

    public DateTimeOffset? LastSampleUtc { get; private set; }

    /// <summary>
    /// The most recent sample the pump has seen, full stop - not throttled like the SignalR
    /// broadcast. Lets a request-scoped caller (see <c>FlightEndpoints.StartAsync</c>'s fuel
    /// reconciliation) read "what does the sim currently report" without subscribing to
    /// <see cref="SampleReceived"/> itself. Null until the first sample of this run arrives.
    /// </summary>
    public TelemetrySample? LastSample { get; private set; }

    /// <summary>
    /// Test-only hook that sets <see cref="LastSample"/>/<see cref="LastSampleUtc"/> directly,
    /// without running the real telemetry pump - internal (rather than private) purely so
    /// fuel-reconciliation tests can drive <c>FlightEndpoints.StartAsync</c> as if a sim had just
    /// reported a reading, deterministically and without a real-time replay. See
    /// FuelReconciliationTests.
    /// </summary>
    internal void SetLastSampleForTests(TelemetrySample sample)
    {
        LastSampleUtc = sample.TimestampUtc;
        LastSample = sample;
    }

    /// <summary>
    /// Fires for every sample, at full rate, unlike the throttled SignalR broadcast below - this
    /// is what <see cref="FlightLifecycleService"/> drives the flight phase state machine from.
    /// Handlers run synchronously on the telemetry pump's own loop, so they must be fast and must
    /// never do I/O directly - queue work instead of awaiting it here, or the whole sim stream
    /// stalls behind a slow handler.
    /// </summary>
    public event EventHandler<TelemetrySample>? SampleReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        await _source.StartAsync(_cts.Token);
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        await _source.StopAsync(cancellationToken);

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        await _source.DisposeAsync();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sample in _source.Telemetry.ReadAllAsync(ct))
            {
                LastSampleUtc = sample.TimestampUtc;
                LastSample = sample;
                SampleReceived?.Invoke(this, sample);

                var now = DateTimeOffset.UtcNow;
                if (now - _lastBroadcastUtc < BroadcastInterval)
                {
                    continue;
                }

                _lastBroadcastUtc = now;
                await BroadcastAsync(sample, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry pump stopped unexpectedly.");
        }
    }

    private Task BroadcastAsync(TelemetrySample sample, CancellationToken ct)
    {
        var payload = new
        {
            timestampUtc = sample.TimestampUtc.ToString("o"),
            latitude = sample.LatitudeDeg,
            longitude = sample.LongitudeDeg,
            altitudeMslFt = sample.AltitudeMslFt,
            altitudeAglFt = sample.AltitudeAglFt,
            indicatedAirspeedKt = sample.IndicatedAirspeedKt,
            groundSpeedKt = sample.GroundSpeedKt,
            verticalSpeedFpm = sample.VerticalSpeedFpm,
            headingTrue = sample.TrueHeadingDeg,
            headingMagnetic = sample.MagneticHeadingDeg,
            onGround = sample.OnGround,
            connectionState = ConnectionState.ToString(),
        };

        return _hub.Clients.All.SendAsync("telemetry", payload, ct);
    }
}

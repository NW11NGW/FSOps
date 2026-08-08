using System.Text.Json;
using System.Threading.Channels;

namespace FSOps.Sim.Fake;

/// <summary>
/// Replays a scripted flight from a JSON file instead of talking to a simulator. This is the
/// project's core development strategy: every feature above the sim layer can be built and
/// tested against a deterministic, always-available flight with no copy of MSFS required.
/// </summary>
public sealed class FakeSimSource : ISimSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FakeSimSourceOptions _options;
    private readonly Channel<TelemetrySample> _channel;

    private FakeFlightScript? _script;
    private double _durationSeconds;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private SimConnectionState _connectionState = SimConnectionState.Disconnected;

    public FakeSimSource(FakeSimSourceOptions options)
    {
        _options = options;
        _channel = SimTelemetryChannel.Create();
    }

    public string Kind => "Fake";

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

    public AircraftIdentity? CurrentAircraft { get; private set; }

    public ChannelReader<TelemetrySample> Telemetry => _channel.Reader;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ConnectionState = SimConnectionState.Connecting;

        var json = await File.ReadAllTextAsync(_options.ReplayFilePath, cancellationToken);
        var script = JsonSerializer.Deserialize<FakeFlightScript>(json, JsonOptions);

        if (script is null || script.Keyframes.Count == 0)
        {
            ConnectionState = SimConnectionState.Disconnected;
            throw new InvalidOperationException($"Replay file '{_options.ReplayFilePath}' is missing or has no keyframes.");
        }

        _script = script;
        _durationSeconds = script.Keyframes[^1].TSeconds;
        CurrentAircraft = new AircraftIdentity(script.Aircraft.Title, script.Aircraft.AtcModel, script.Aircraft.AtcType);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RunAsync(script, _durationSeconds, _cts.Token), CancellationToken.None);

        ConnectionState = SimConnectionState.Connected;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        ConnectionState = SimConnectionState.Disconnected;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _cts?.Dispose();
    }

    private async Task RunAsync(FakeFlightScript script, double durationSeconds, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var startOffsetSeconds = Math.Clamp(_options.StartAtSeconds, 0, durationSeconds);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var simSeconds = startOffsetSeconds + (clock.Elapsed.TotalSeconds * _options.TimeCompressionFactor);

                if (simSeconds >= durationSeconds)
                {
                    if (_options.Loop)
                    {
                        clock.Restart();
                        startOffsetSeconds = 0;
                        simSeconds = 0;
                    }
                    else
                    {
                        _channel.Writer.TryWrite(FakeFlightInterpolator.Sample(script, durationSeconds));
                        break;
                    }
                }

                _channel.Writer.TryWrite(FakeFlightInterpolator.Sample(script, simSeconds));

                await Task.Delay(_options.SampleInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path - StopAsync cancelled the token.
        }
    }
}

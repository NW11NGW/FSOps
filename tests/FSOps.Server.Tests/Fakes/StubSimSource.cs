using System.Threading.Channels;
using FSOps.Sim;

namespace FSOps.Server.Tests.Fakes;

/// <summary>
/// An <see cref="ISimSource"/> whose <see cref="CurrentAircraft"/> is fixed at construction, so
/// tests can drive <c>FlightEndpoints.StartAsync</c> with a specific TITLE/ATC MODEL already
/// reported by the sim - the "genuine family mismatch" case, as opposed to <see cref="NoOpSimSource"/>'s
/// "sim never connected" case (CurrentAircraft always null). Everything else about the connection
/// is irrelevant to these tests, so it's otherwise identical to NoOpSimSource.
/// </summary>
internal sealed class StubSimSource : ISimSource
{
    private readonly Channel<TelemetrySample> _channel = Channel.CreateUnbounded<TelemetrySample>();

    public StubSimSource(AircraftIdentity currentAircraft)
    {
        CurrentAircraft = currentAircraft;
    }

    public string Kind => "Stub";

    public SimConnectionState ConnectionState => SimConnectionState.Connected;

    public event EventHandler<SimConnectionState>? ConnectionStateChanged { add { } remove { } }

    public AircraftIdentity? CurrentAircraft { get; }

    public ChannelReader<TelemetrySample> Telemetry => _channel.Reader;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

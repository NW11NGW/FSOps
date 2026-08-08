using System.Threading.Channels;
using FSOps.Sim;

namespace FSOps.Server.Tests.Fakes;

/// <summary>
/// A no-op <see cref="ISimSource"/> so tests can construct a real
/// <see cref="FSOps.Server.Services.SimTelemetryService"/> (needed to call
/// <c>FlightEndpoints.StartAsync</c> directly) without a simulator or a scripted replay file -
/// there's simply never an aircraft loaded and never a telemetry sample, which is fine since
/// nothing under test here depends on either.
/// </summary>
internal sealed class NoOpSimSource : ISimSource
{
    private readonly Channel<TelemetrySample> _channel = Channel.CreateUnbounded<TelemetrySample>();

    public string Kind => "NoOp";

    public SimConnectionState ConnectionState => SimConnectionState.Disconnected;

    public event EventHandler<SimConnectionState>? ConnectionStateChanged { add { } remove { } }

    public AircraftIdentity? CurrentAircraft => null;

    public ChannelReader<TelemetrySample> Telemetry => _channel.Reader;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

using System.Threading.Channels;

namespace FSOps.Sim;

/// <summary>
/// A source of flight telemetry. There are two implementations: <see cref="Fake.FakeSimSource"/>
/// replays a scripted flight so the whole app works with no simulator installed, and
/// <c>SimConnectSource</c> talks to a running copy of MSFS. Nothing outside FSOps.Sim - not even
/// the hosted service that owns this - may depend on which one is in use, so no SimConnect (or
/// any other sim-specific) type appears anywhere in this interface.
/// </summary>
public interface ISimSource : IAsyncDisposable
{
    /// <summary>A short label for diagnostics and the sim status endpoint - "Fake" or "SimConnect".</summary>
    string Kind { get; }

    /// <summary>Current connection lifecycle state.</summary>
    SimConnectionState ConnectionState { get; }

    /// <summary>Raised whenever <see cref="ConnectionState"/> changes.</summary>
    event EventHandler<SimConnectionState>? ConnectionStateChanged;

    /// <summary>The aircraft currently loaded in the sim, or null if not yet known.</summary>
    AircraftIdentity? CurrentAircraft { get; }

    /// <summary>Telemetry samples as they become available. Never completes on its own while running.</summary>
    ChannelReader<TelemetrySample> Telemetry { get; }

    /// <summary>Begins connecting/replaying. Returns once the background work has been started, not once connected.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops cleanly, releasing any sim handles or background loops.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

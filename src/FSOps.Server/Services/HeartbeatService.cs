using FSOps.Server.Hubs;
using FSOps.Sim;
using Microsoft.AspNetCore.SignalR;

namespace FSOps.Server.Services;

/// <summary>
/// Broadcasts a heartbeat once a second so the UI can show a live server clock and
/// connection-status pill even before any sim data exists.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    // Resolved from the assembly rather than retyped, so the heartbeat, /api/v1/health and the
    // update checker can never disagree about which build is running. See AppVersion.
    private static readonly string ServerVersion = AppVersion.Current;

    private readonly IHubContext<LiveHub> _hub;
    private readonly SimTelemetryService _simTelemetry;

    public HeartbeatService(IHubContext<LiveHub> hub, SimTelemetryService simTelemetry)
    {
        _hub = hub;
        _simTelemetry = simTelemetry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var payload = new
            {
                ServerTimeUtc = DateTime.UtcNow.ToString("o"),
                SimConnected = _simTelemetry.ConnectionState == SimConnectionState.Connected,
                Version = ServerVersion
            };

            await _hub.Clients.All.SendAsync("heartbeat", payload, stoppingToken);
        }
    }
}

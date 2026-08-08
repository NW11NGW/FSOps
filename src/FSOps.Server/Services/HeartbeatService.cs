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
    private const string ServerVersion = "0.1.0";

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

using Microsoft.AspNetCore.SignalR;

namespace FSOps.Server.Hubs;

/// <summary>
/// Push channel for live data: heartbeat now, sim telemetry and flight events later.
/// Mapped at /hubs/live.
/// </summary>
public sealed class LiveHub : Hub
{
}

using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>
/// Un-grounds any fleet aircraft whose maintenance downtime has elapsed - the wall-clock
/// counterpart to <see cref="MaintenancePoster"/> grounding one. Resolved lazily on read (called at
/// the top of every endpoint that reports or depends on fleet availability - GET /fleet,
/// GET /flights/options, POST /flights/start) rather than by a dedicated background timer: a check
/// due days from now doesn't need active polling, and this way the very next request after the
/// grounding period ends always sees the aircraft correctly released, without adding another
/// BackgroundService tick to the process.
/// </summary>
public static class MaintenanceReleaser
{
    /// <summary>
    /// Releases every InMaintenance aircraft for one airline whose <see cref="FleetAircraft.GroundedUntilUtc"/>
    /// has passed. SQLite's EF provider can't translate a DateTimeOffset comparison inside a Where
    /// clause (the project's own documented EF/SQLite trap), so the InMaintenance filter runs in
    /// SQL and the GroundedUntilUtc cutoff is applied in memory after materialising - same pattern
    /// as EconomyClockService.PostMonthlyChargesAsync's period-end filter.
    /// </summary>
    public static async Task<int> ReleaseDueAsync(FsOpsDbContext db, Guid airlineId, DateTimeOffset now, CancellationToken ct)
    {
        var grounded = (await db.FleetAircraft
                .Where(f => f.AirlineId == airlineId && f.Status == FleetAircraftStatus.InMaintenance)
                .ToListAsync(ct))
            .Where(f => f.GroundedUntilUtc is not null && f.GroundedUntilUtc <= now)
            .ToList();

        if (grounded.Count == 0)
        {
            return 0;
        }

        foreach (var aircraft in grounded)
        {
            aircraft.Status = FleetAircraftStatus.Active;
            aircraft.GroundedUntilUtc = null;
        }

        await db.SaveChangesAsync(ct);
        return grounded.Count;
    }
}

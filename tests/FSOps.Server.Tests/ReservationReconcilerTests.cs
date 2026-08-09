using FSOps.Core.Entities;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves <see cref="ReservationReconciler.ReconcileAsync"/> fixes the contradiction the OLD
/// soft-preference reservation model could leave behind: a <see cref="FleetAircraft"/> flagged
/// <c>ReservedForPlayer = true</c> that also has active <see cref="PilotScheduleEntry"/> rows -
/// confirmed reachable via ordinary pre-3a play (lease an aircraft unreserved, build a schedule on
/// it, reserve it afterward through the old endpoint, which never checked). Every scenario here
/// constructs that shape DIRECTLY via EF, bypassing <c>PilotEndpoints.SaveScheduleAsync</c> entirely -
/// the redesigned endpoint would correctly refuse to create it today, which is exactly why a
/// legacy-data reconciler is needed at all.
/// </summary>
public class ReservationReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_NoContradiction_FindsNothing_WritesNothing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        // Founding aircraft is reserved by fixture default, with no legs - nothing to reconcile.

        var result = await ReservationReconciler.ReconcileAsync(ctx.Db);

        Assert.False(result.HasFindings);
        Assert.Empty(result.Released);
        Assert.Empty(result.FallbackReserved);
        Assert.Empty(result.AirlinesLeftWithNoReservedAircraft);
    }

    [Fact]
    public async Task ReconcileAsync_ReservedAircraftWithLegs_IsReleased_ScheduleIsKept()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(); // reserved = true by fixture default
        var (pilot, route) = await SeedPilotAndRouteAsync(ctx);
        await AddScheduleEntryAsync(ctx, pilot.Id, aircraft.Id, route.Id, DayOfWeek.Monday, "06:00:00");

        var result = await ReservationReconciler.ReconcileAsync(ctx.Db);

        Assert.True(result.HasFindings);
        var released = Assert.Single(result.Released);
        Assert.Equal(aircraft.Id, released.FleetAircraftId);
        Assert.Equal(1, released.ScheduledLegCount);

        var reloaded = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == aircraft.Id);
        Assert.False(reloaded.ReservedForPlayer);

        // The schedule itself is untouched - this is a flag fix, never a schedule fix.
        Assert.Equal(1, await ctx.Db.PilotScheduleEntries.CountAsync());
    }

    [Fact]
    public async Task ReconcileAsync_ReleasingLeavesZeroReserved_FallsBackToAnUnscheduledAircraft_PreferringTheHub()
    {
        using var ctx = await RouteTestContext.CreateAsync(); // founding aircraft at EGGD (the hub), reserved
        var founding = await ctx.Db.FleetAircraft.SingleAsync();
        var (pilot, route) = await SeedPilotAndRouteAsync(ctx);
        await AddScheduleEntryAsync(ctx, pilot.Id, founding.Id, route.Id, DayOfWeek.Monday, "06:00:00");

        // Unreserved, no legs, NOT at the hub.
        var awayAircraft = await AddAircraftAsync(ctx, "G-AWAY", "EGPH", reserved: false);
        // Unreserved, no legs, AT the hub - must be preferred over the one above.
        var hubAircraft = await AddAircraftAsync(ctx, "G-HUBB", "EGGD", reserved: false);

        var result = await ReservationReconciler.ReconcileAsync(ctx.Db);

        Assert.Single(result.Released);
        var fallback = Assert.Single(result.FallbackReserved);
        Assert.Equal(hubAircraft.Id, fallback.FleetAircraftId);
        Assert.Empty(result.AirlinesLeftWithNoReservedAircraft);

        var reloadedHub = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == hubAircraft.Id);
        Assert.True(reloadedHub.ReservedForPlayer);
        var reloadedAway = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == awayAircraft.Id);
        Assert.False(reloadedAway.ReservedForPlayer);
    }

    /// <summary>
    /// The case called out explicitly as the one that will be got wrong later: every aircraft in
    /// the fleet has scheduled legs, so none can be reserved without recreating the exact
    /// contradiction just fixed. The airline is left at zero, loudly recorded in the result - never
    /// silently handed a schedule-bearing aircraft, and never had a schedule cleared to make room.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_EveryAircraftHasLegs_LeavesTheAirlineAtZero_ClearsNoSchedule()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var founding = await ctx.Db.FleetAircraft.SingleAsync();
        var (pilotA, route) = await SeedPilotAndRouteAsync(ctx);
        await AddScheduleEntryAsync(ctx, pilotA.Id, founding.Id, route.Id, DayOfWeek.Monday, "06:00:00");

        var second = await AddAircraftAsync(ctx, "G-SECN", "EGGD", reserved: false);
        var pilotB = await HirePilotDirectlyAsync(ctx, "Second FO");
        await AddScheduleEntryAsync(ctx, pilotB.Id, second.Id, route.Id, DayOfWeek.Tuesday, "06:00:00");

        var result = await ReservationReconciler.ReconcileAsync(ctx.Db);

        Assert.Single(result.Released);
        Assert.Empty(result.FallbackReserved);
        var zeroAirline = Assert.Single(result.AirlinesLeftWithNoReservedAircraft);
        Assert.Equal(ctx.Airline.Id, zeroAirline);

        var fleet = await ctx.Db.FleetAircraft.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(fleet, f => f.ReservedForPlayer);

        // Both schedules survive untouched - 2 entries total, nothing cleared.
        Assert.Equal(2, await ctx.Db.PilotScheduleEntries.CountAsync());
    }

    [Fact]
    public async Task ReconcileAsync_AnotherAircraftAlreadyReserved_NoFallbackNeeded()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var founding = await ctx.Db.FleetAircraft.SingleAsync(); // reserved, about to get legs -> released
        var (pilot, route) = await SeedPilotAndRouteAsync(ctx);
        await AddScheduleEntryAsync(ctx, pilot.Id, founding.Id, route.Id, DayOfWeek.Monday, "06:00:00");

        // ALSO reserved, no legs - the airline still has this one reserved after founding is
        // released, so no fallback should ever fire.
        var alsoReserved = await AddAircraftAsync(ctx, "G-ALSO", "EGGD", reserved: true);

        var result = await ReservationReconciler.ReconcileAsync(ctx.Db);

        Assert.Single(result.Released);
        Assert.Empty(result.FallbackReserved);
        Assert.Empty(result.AirlinesLeftWithNoReservedAircraft);

        var reloadedAlso = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == alsoReserved.Id);
        Assert.True(reloadedAlso.ReservedForPlayer);
    }

    [Fact]
    public async Task ReconcileAsync_RunTwice_SecondRunFindsNothing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var founding = await ctx.Db.FleetAircraft.SingleAsync();
        var (pilot, route) = await SeedPilotAndRouteAsync(ctx);
        await AddScheduleEntryAsync(ctx, pilot.Id, founding.Id, route.Id, DayOfWeek.Monday, "06:00:00");
        await AddAircraftAsync(ctx, "G-SECN", "EGGD", reserved: false);

        var first = await ReservationReconciler.ReconcileAsync(ctx.Db);
        Assert.True(first.HasFindings);

        var secondRun = await ReservationReconciler.ReconcileAsync(ctx.Db);
        Assert.False(secondRun.HasFindings);
        Assert.Empty(secondRun.Released);
        Assert.Empty(secondRun.FallbackReserved);
        Assert.Empty(secondRun.AirlinesLeftWithNoReservedAircraft);
    }

    private static async Task<(Pilot Pilot, Route Route)> SeedPilotAndRouteAsync(RouteTestContext ctx)
    {
        var pilot = await HirePilotDirectlyAsync(ctx, "Legacy FO");
        var route = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "EGPH",
            DistanceNm = 275.2, BaseFare = 89.00m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);
        await ctx.Db.SaveChangesAsync();
        return (pilot, route);
    }

    private static async Task<Pilot> HirePilotDirectlyAsync(RouteTestContext ctx, string name)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = name, IsPlayer = false,
            MonthlySalary = 4_500m, SkillRating = 50, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Pilots.Add(pilot);
        await ctx.Db.SaveChangesAsync();
        return pilot;
    }

    /// <summary>Inserts a schedule + entry DIRECTLY via EF, bypassing PilotEndpoints.SaveScheduleAsync
    /// entirely - see this class's own doc for why that's the point, not an oversight.</summary>
    private static async Task AddScheduleEntryAsync(
        RouteTestContext ctx, Guid pilotId, Guid fleetAircraftId, Guid routeId, DayOfWeek day, string time)
    {
        var schedule = await ctx.Db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilotId);
        if (schedule is null)
        {
            schedule = new PilotSchedule { Id = Guid.NewGuid(), PilotId = pilotId, AirlineId = ctx.Airline.Id, CreatedUtc = DateTimeOffset.UtcNow };
            ctx.Db.PilotSchedules.Add(schedule);
        }

        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(),
            PilotScheduleId = schedule.Id,
            DayOfWeek = day,
            DepartureTimeUtc = TimeSpan.Parse(time),
            RouteId = routeId,
            FleetAircraftId = fleetAircraftId,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<FleetAircraft> AddAircraftAsync(RouteTestContext ctx, string registration, string locationIcao, bool reserved)
    {
        var aircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            AircraftTypeId = ctx.AircraftType.Id,
            Registration = registration,
            Ownership = AircraftOwnership.Owned,
            LocationIcao = locationIcao,
            Status = FleetAircraftStatus.Active,
            ReservedForPlayer = reserved,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.FleetAircraft.Add(aircraft);
        await ctx.Db.SaveChangesAsync();
        return aircraft;
    }
}

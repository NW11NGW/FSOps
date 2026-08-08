using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Chunk E2's own stated verification for the pilot/schedule endpoints: hiring, the weekly
/// schedule builder's whole-airline conflict validation, and releasing a pilot cascading to their
/// schedule. Drives PilotEndpoints' handlers directly against an isolated in-memory
/// RouteTestContext - same convention as FleetEndpointsTests/MaintenanceTriggerTests.
/// </summary>
public class PilotEndpointsTests
{
    [Fact]
    public async Task HireAsync_CreatesAVirtualPilot_AtThePlaystylesStandardSalary()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();

        var result = await PilotEndpoints.HireAsync(new HirePilotRequest("First Officer Ada"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));

        var pilots = await ctx.Db.Pilots.Where(p => p.AirlineId == ctx.Airline.Id && !p.IsPlayer).ToListAsync();
        var hired = Assert.Single(pilots);
        Assert.Equal("First Officer Ada", hired.Name);
        Assert.Equal(catalog.Get(ctx.Airline.Playstyle).AirlineStartup.StartingPilotMonthlySalary, hired.MonthlySalary);
        Assert.Equal(50.0, hired.SkillRating);
    }

    [Fact]
    public async Task HireAsync_WithNoName_AutoGeneratesOne()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();

        var result = await PilotEndpoints.HireAsync(new HirePilotRequest(null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        var pilot = await ctx.Db.Pilots.SingleAsync(p => p.AirlineId == ctx.Airline.Id && !p.IsPlayer);
        Assert.False(string.IsNullOrWhiteSpace(pilot.Name));
    }

    [Fact]
    public async Task SaveSchedule_ValidRoundTrip_SavesAndIsReturnedByGet()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var request = new SaveScheduleRequest(new[]
        {
            new ScheduleEntryRequest(0, "06:00:00", outbound.Id, aircraftId), // Sunday
            new ScheduleEntryRequest(0, "08:00:00", inbound.Id, aircraftId),
        });

        var saveResult = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(saveResult));

        var getResult = await PilotEndpoints.GetScheduleAsync(pilot.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var dto = OkValueOf<ScheduleDto>(getResult);
        Assert.Equal(2, dto.Entries.Count);
        Assert.Contains(dto.Entries, e => e.DepartureIcao == "EGGD" && e.ArrivalIcao == "EGPH");
        Assert.Contains(dto.Entries, e => e.DepartureIcao == "EGPH" && e.ArrivalIcao == "EGGD");
    }

    [Fact]
    public async Task SaveSchedule_GeographicallyImpossibleChain_ReturnsConflictsInPlainWords()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var elsewhere = await SeedRouteAsync(ctx, "EGSS", "EGPF");
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        // Lands at EGPH (outbound) but the second leg departs EGSS - no route connects them.
        var request = new SaveScheduleRequest(new[]
        {
            new ScheduleEntryRequest(1, "06:00:00", outbound.Id, aircraftId), // Monday
            new ScheduleEntryRequest(1, "08:00:00", elsewhere.Id, aircraftId),
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var value = OkValueOf<ConflictDto>(result);
        Assert.NotEmpty(value.Conflicts);
        Assert.Contains(value.Conflicts, c => c.Contains("EGPH") && c.Contains("EGSS"));
        // No EGPH -> EGSS route was seeded, so the fix offered must be to CREATE one - not to
        // schedule a leg on a route that doesn't exist.
        Assert.Contains(value.Conflicts, c => c.Contains("create") && c.Contains("EGPH") && c.Contains("EGSS"));

        // Nothing was persisted - a rejected save must not leave a half-written schedule behind.
        var schedule = await ctx.Db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id);
        Assert.Null(schedule);
    }

    [Fact]
    public async Task SaveSchedule_ChainBreak_ButTheConnectingRouteAlreadyExists_SaysScheduleALeg_NotCreateARoute()
    {
        // The real bug from user feedback 2026-08-08: the validator used to say "you'd need a
        // EGPH -> EGSS route" even when the airline already had one - it just wasn't scheduled
        // anywhere in this chain. Seeding that route here reproduces exactly that: the wording must
        // now say "schedule a leg", not "create a route", because there's nothing to create.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var elsewhere = await SeedRouteAsync(ctx, "EGSS", "EGPF");
        await SeedRouteAsync(ctx, "EGPH", "EGSS"); // the connecting route exists, just isn't scheduled
        // With only two entries and PUT's requireWeekClosure: true, the SAME pair is checked twice -
        // once as the interior gap, once as the cyclic wrap back to the first entry. Seed the wrap's
        // own connecting route too (EGPF -> EGGD) so this test isolates the interior wording it's
        // actually about, rather than also tripping the wrap's unrelated "create a route" conflict.
        await SeedRouteAsync(ctx, "EGPF", "EGGD");
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var request = new SaveScheduleRequest(new[]
        {
            new ScheduleEntryRequest(1, "06:00:00", outbound.Id, aircraftId), // Monday, lands EGPH
            new ScheduleEntryRequest(1, "08:00:00", elsewhere.Id, aircraftId), // departs EGSS
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var value = OkValueOf<ConflictDto>(result);
        Assert.Contains(value.Conflicts, c => c.Contains("schedule a EGPH -> EGSS leg"));
        Assert.DoesNotContain(value.Conflicts, c => c.Contains("you'd need to create"));

        var schedule = await ctx.Db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id);
        Assert.Null(schedule);
    }

    [Fact]
    public async Task SaveSchedule_ReservedAircraft_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        // Deliberately left reserved (RouteTestContext's founding aircraft defaults to false in the
        // fixture, but the real founding aircraft is reserved-for-player by default - simulate that
        // here explicitly rather than relying on the fixture's own default).
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.ReservedForPlayer = true;
        await ctx.Db.SaveChangesAsync();

        var request = new SaveScheduleRequest(new[]
        {
            new ScheduleEntryRequest(0, "06:00:00", outbound.Id, aircraftId),
            new ScheduleEntryRequest(0, "08:00:00", inbound.Id, aircraftId),
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var value = OkValueOf<ConflictDto>(result);
        Assert.Contains(value.Conflicts, c => c.Contains("reserved for the player"));
    }

    [Fact]
    public async Task SaveSchedule_ForThePlayerPilot_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();

        var playerPilot = new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "You", IsPlayer = true,
            MonthlySalary = 9_000m, SkillRating = 50, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Pilots.Add(playerPilot);
        await ctx.Db.SaveChangesAsync();

        var result = await PilotEndpoints.SaveScheduleAsync(
            playerPilot.Id, new SaveScheduleRequest(Array.Empty<ScheduleEntryRequest>()), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
    }

    [Fact]
    public async Task ReleaseAsync_SoftDeletesThePilot_AndCascadesToTheirSchedule()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var saveResult = await PilotEndpoints.SaveScheduleAsync(
            pilot.Id,
            new SaveScheduleRequest(new[]
            {
                new ScheduleEntryRequest(0, "06:00:00", outbound.Id, aircraftId),
                new ScheduleEntryRequest(0, "08:00:00", inbound.Id, aircraftId),
            }),
            ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(saveResult));

        var releaseResult = await PilotEndpoints.ReleaseAsync(pilot.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status204NoContent, StatusCodeOf(releaseResult));

        var pilots = await ctx.Db.Pilots.Where(p => p.AirlineId == ctx.Airline.Id && !p.IsPlayer).ToListAsync();
        Assert.Empty(pilots); // soft-deleted, excluded by the query filter

        var schedules = await ctx.Db.PilotSchedules.Where(s => s.PilotId == pilot.Id).ToListAsync();
        Assert.Empty(schedules); // cascaded

        var entries = await ctx.Db.PilotScheduleEntries.ToListAsync();
        Assert.Empty(entries); // no entries survive the cascade
    }

    [Fact]
    public async Task ScheduleOptions_ReservedAircraftIsIllegal_UnreservedIsLegal()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.ReservedForPlayer = true;
        await ctx.Db.SaveChangesAsync();

        var result = await PilotEndpoints.GetScheduleOptionsAsync(
            pilot.Id, new ScheduleOptionsRequest(1, "06:00", null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<OptionsDto>(result);
        Assert.Contains(value.Illegal, i => i.FleetAircraftId == aircraftId && i.Reason.Contains("reserved for the player"));
        Assert.DoesNotContain(value.Legal, l => l.FleetAircraftId == aircraftId);
    }

    [Fact]
    public async Task ScheduleOptions_EmptySchedule_OffersLegalOptions()
    {
        // Regression for the bug the schedule-builder agent found: options used to validate
        // full-week closure (including the wraparound from the week's last leg back to its first)
        // against a lone candidate, so a brand-new pilot's empty schedule returned legal: [] for
        // every single candidate - a week could never be built up one leg at a time.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var result = await PilotEndpoints.GetScheduleOptionsAsync(
            pilot.Id, new ScheduleOptionsRequest(1, "06:00", null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<OptionsDto>(result);
        Assert.Contains(value.Legal, l => l.RouteId == outbound.Id && l.FleetAircraftId == aircraftId);
    }

    [Fact]
    public async Task ScheduleOptions_PartiallyBuiltWeek_OffersASensibleNextLeg()
    {
        // A Monday out-and-back (06:00 out, 08:00 back) has already been drafted client-side but
        // NOT saved. Querying a third leg at 12:00 the same day, departing from where the aircraft
        // now sits (EGGD, after the 08:00 return), must be legal - the second half of the original
        // bug report: even after the empty-schedule case is fixed, the SECOND leg of a day was
        // still judged against a stale aircraft position unless the draft is passed in.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var draft = new[]
        {
            new ScheduleEntryRequest(1, "06:00:00", outbound.Id, aircraftId),
            new ScheduleEntryRequest(1, "08:00:00", inbound.Id, aircraftId),
        };

        var result = await PilotEndpoints.GetScheduleOptionsAsync(
            pilot.Id, new ScheduleOptionsRequest(1, "12:00", draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<OptionsDto>(result);
        Assert.Contains(value.Legal, l => l.RouteId == outbound.Id && l.FleetAircraftId == aircraftId);

        // The SAME query without the draft (server falls back to nothing known about this pilot's
        // day) would have no way to know the aircraft is at EGGD rather than wherever it started -
        // demonstrating the draft is actually load-bearing, not just accepted and ignored.
        var withoutDraft = await PilotEndpoints.GetScheduleOptionsAsync(
            pilot.Id, new ScheduleOptionsRequest(1, "12:00", null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var withoutDraftValue = OkValueOf<OptionsDto>(withoutDraft);
        Assert.Contains(withoutDraftValue.Legal, l => l.RouteId == outbound.Id && l.FleetAircraftId == aircraftId);
    }

    [Fact]
    public async Task ScheduleOptions_WrongAirportIsStillIllegal_WithItsReason()
    {
        // Geographic continuity within the drafted day must still be enforced even with closure
        // relaxed - only the WRAP is exempted, not the interior legs the player has actually built.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var elsewhere = await SeedRouteAsync(ctx, "EGSS", "EGPF");
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var draft = new[] { new ScheduleEntryRequest(1, "06:00:00", outbound.Id, aircraftId) }; // lands EGPH

        var result = await PilotEndpoints.GetScheduleOptionsAsync(
            pilot.Id, new ScheduleOptionsRequest(1, "08:00", draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<OptionsDto>(result);
        // The EGSS->EGPF candidate departs EGSS, but the aircraft (after the drafted 06:00 leg) is
        // at EGPH - illegal, with a reason naming both airports. No EGPH -> EGSS route was seeded,
        // so the reason must offer to CREATE one.
        Assert.Contains(value.Illegal, i => i.RouteId == elsewhere.Id && i.FleetAircraftId == aircraftId && i.Reason.Contains("EGPH") && i.Reason.Contains("EGSS"));
        Assert.Contains(value.Illegal, i => i.RouteId == elsewhere.Id && i.FleetAircraftId == aircraftId && i.Reason.Contains("create"));
        Assert.DoesNotContain(value.Legal, l => l.RouteId == elsewhere.Id && l.FleetAircraftId == aircraftId);
    }

    [Fact]
    public async Task ScheduleOptions_WrongAirportButTheConnectingRouteAlreadyExists_SaysScheduleALeg()
    {
        // Same setup as ScheduleOptions_WrongAirportIsStillIllegal_WithItsReason, but this time the
        // EGPH -> EGSS route already exists - just unscheduled. The options endpoint must offer the
        // corrected "schedule a leg" wording exactly as a save-time rejection does (docs/PLAN.md
        // "2b", user feedback 2026-08-08).
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var elsewhere = await SeedRouteAsync(ctx, "EGSS", "EGPF");
        await SeedRouteAsync(ctx, "EGPH", "EGSS");
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var draft = new[] { new ScheduleEntryRequest(1, "06:00:00", outbound.Id, aircraftId) }; // lands EGPH

        var result = await PilotEndpoints.GetScheduleOptionsAsync(
            pilot.Id, new ScheduleOptionsRequest(1, "08:00", draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var value = OkValueOf<OptionsDto>(result);
        Assert.Contains(value.Illegal, i =>
            i.RouteId == elsewhere.Id && i.FleetAircraftId == aircraftId && i.Reason.Contains("schedule a EGPH -> EGSS leg"));
        Assert.DoesNotContain(value.Illegal, i => i.RouteId == elsewhere.Id && i.Reason.Contains("you'd need to create"));
    }

    [Fact]
    public async Task ScheduleOptions_GroundedAircraftIsStillIllegal_WithItsReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.Status = FleetAircraftStatus.InMaintenance;
        aircraft.GroundedUntilUtc = DateTimeOffset.UtcNow.AddDays(2);
        await ctx.Db.SaveChangesAsync();

        var result = await PilotEndpoints.GetScheduleOptionsAsync(
            pilot.Id, new ScheduleOptionsRequest(1, "06:00", null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var value = OkValueOf<OptionsDto>(result);
        Assert.Contains(value.Illegal, i => i.FleetAircraftId == aircraftId && i.Reason.Contains("maintenance"));
        Assert.DoesNotContain(value.Legal, l => l.FleetAircraftId == aircraftId);
    }

    [Fact]
    public async Task ScheduleOptions_NoRestRoomLeft_IsStillIllegal_WithItsReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        // Sunday duty already runs 20:00 -> ~21:05 in the draft. A Monday 03:00 departure leaves
        // well under the 10-hour minimum rest - illegal even though closure itself is relaxed,
        // because rest between two ALREADY-drafted duty days is an interior (non-wrap) check.
        var draft = new[] { new ScheduleEntryRequest(0, "20:00:00", outbound.Id, aircraftId) };

        var result = await PilotEndpoints.GetScheduleOptionsAsync(
            pilot.Id, new ScheduleOptionsRequest(1, "03:00", draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var value = OkValueOf<OptionsDto>(result);
        Assert.Contains(value.Illegal, i => i.RouteId == inbound.Id && i.FleetAircraftId == aircraftId && (i.Reason.Contains("rest") || i.Reason.Contains("hours")));
    }

    [Fact]
    public async Task SaveSchedule_WeekThatDoesNotClose_IsStillRejected()
    {
        // The other half of the split: options relaxes closure, but PUT /schedule must not. A
        // single Monday leg with no return leaves the aircraft unable to start its own next
        // week's Monday departure from the right airport.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var request = new SaveScheduleRequest(new[] { new ScheduleEntryRequest(1, "06:00:00", outbound.Id, aircraftId) });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var schedule = await ctx.Db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id);
        Assert.Null(schedule); // nothing persisted
    }

    private static async Task<Pilot> HirePilotAsync(RouteTestContext ctx, EconomyConfigCatalog catalog)
    {
        var result = await PilotEndpoints.HireAsync(new HirePilotRequest("Test FO"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        return await ctx.Db.Pilots.SingleAsync(p => p.AirlineId == ctx.Airline.Id && !p.IsPlayer);
    }

    private static async Task<(Route Outbound, Route Inbound)> SeedRoundTripRoutesAsync(RouteTestContext ctx)
    {
        var outbound = await SeedRouteAsync(ctx, "EGGD", "EGPH");
        var inbound = await SeedRouteAsync(ctx, "EGPH", "EGGD");
        return (outbound, inbound);
    }

    private static async Task<Route> SeedRouteAsync(RouteTestContext ctx, string departure, string arrival)
    {
        var route = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = departure, ArrivalIcao = arrival,
            DistanceNm = 275.2, BaseFare = 89.00m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);
        await ctx.Db.SaveChangesAsync();
        return route;
    }

    private static async Task<Guid> FleetAircraftIdAsync(RouteTestContext ctx) =>
        await ctx.Db.FleetAircraft.Where(f => f.AirlineId == ctx.Airline.Id).Select(f => f.Id).SingleAsync();

    private static async Task ReleaseReservationAsync(RouteTestContext ctx, Guid aircraftId)
    {
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.ReservedForPlayer = false;
        await ctx.Db.SaveChangesAsync();
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static T OkValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record ScheduleDto(Guid PilotId, List<ScheduleEntryDto> Entries);

    private sealed record ScheduleEntryDto(Guid Id, int DayOfWeek, string DepartureTimeUtc, Guid RouteId, string? DepartureIcao, string? ArrivalIcao, string? FlightNumber, Guid FleetAircraftId, string? Registration, int? BlockMinutes);

    private sealed record ConflictDto(string Error, List<string> Conflicts);

    private sealed record OptionsDto(List<OptionDto> Legal, List<IllegalOptionDto> Illegal);

    private sealed record OptionDto(Guid RouteId, string DepartureIcao, string ArrivalIcao, Guid FleetAircraftId, string Registration);

    private sealed record IllegalOptionDto(Guid RouteId, Guid FleetAircraftId, string Reason);
}

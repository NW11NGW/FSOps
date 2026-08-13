using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Chunk E3's own stated verification for the redesigned pilot/schedule endpoints - aircraft
/// assigned per pilot per DUTY DAY, not per leg, plus the hard
/// reservation invariant that a reserved aircraft can never be scheduled to a virtual pilot. Drives PilotEndpoints' handlers directly against an
/// isolated in-memory RouteTestContext - same convention as FleetEndpointsTests/MaintenanceTriggerTests.
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
            new DutyDayRequest(0, aircraftId, new[] // Sunday
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("08:00:00", inbound.Id),
            }),
        });

        var saveResult = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(saveResult));

        var getResult = await PilotEndpoints.GetScheduleAsync(pilot.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var dto = OkValueOf<ScheduleDto>(getResult);
        var day = Assert.Single(dto.DutyDays);
        Assert.Equal(aircraftId, day.FleetAircraftId);
        Assert.Equal(2, day.Legs.Count);
        Assert.Contains(day.Legs, l => l.DepartureIcao == "EGGD" && l.ArrivalIcao == "EGPH");
        Assert.Contains(day.Legs, l => l.DepartureIcao == "EGPH" && l.ArrivalIcao == "EGGD");
    }

    [Fact]
    public async Task GetSchedule_BeforeAnySave_ReportsAutoSuspendOnMaintenanceDefaultOfTrue()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);

        var getResult = await PilotEndpoints.GetScheduleAsync(pilot.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var dto = OkValueOf<ScheduleDto>(getResult);

        Assert.True(dto.AutoSuspendOnMaintenance);
    }

    [Fact]
    public async Task SaveSchedule_OmittingAutoSuspendOnMaintenance_DefaultsToTrue()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        // No AutoSuspendOnMaintenance argument at all - the exact shape an older client (one that
        // predates this field) would send, since the parameter defaults to null on the wire.
        var request = new SaveScheduleRequest(new[]
        {
            new DutyDayRequest(0, aircraftId, new[]
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("08:00:00", inbound.Id),
            }),
        });

        var saveResult = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var savedDto = OkValueOf<ScheduleDto>(saveResult);
        Assert.True(savedDto.AutoSuspendOnMaintenance);

        var schedule = await ctx.Db.PilotSchedules.SingleAsync(s => s.PilotId == pilot.Id);
        Assert.True(schedule.AutoSuspendOnMaintenance);
    }

    [Fact]
    public async Task SaveSchedule_ExplicitlySettingAutoSuspendOnMaintenanceFalse_PersistsAndIsReturnedByGet()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var request = new SaveScheduleRequest(
            new[]
            {
                new DutyDayRequest(0, aircraftId, new[]
                {
                    new DutyLegRequest("06:00:00", outbound.Id),
                    new DutyLegRequest("08:00:00", inbound.Id),
                }),
            },
            AutoSuspendOnMaintenance: false);

        var saveResult = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var savedDto = OkValueOf<ScheduleDto>(saveResult);
        Assert.False(savedDto.AutoSuspendOnMaintenance);

        var getResult = await PilotEndpoints.GetScheduleAsync(pilot.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var getDto = OkValueOf<ScheduleDto>(getResult);
        Assert.False(getDto.AutoSuspendOnMaintenance);
    }

    [Fact]
    public async Task SaveSchedule_DayWithLegsButNoAircraft_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);

        var request = new SaveScheduleRequest(new[]
        {
            new DutyDayRequest(0, null, new[] { new DutyLegRequest("06:00:00", outbound.Id) }),
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var schedule = await ctx.Db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id);
        Assert.Null(schedule);
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
            new DutyDayRequest(1, aircraftId, new[] // Monday
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("08:00:00", elsewhere.Id),
            }),
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
            new DutyDayRequest(1, aircraftId, new[] // Monday, lands EGPH then departs EGSS
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("08:00:00", elsewhere.Id),
            }),
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
            new DutyDayRequest(0, aircraftId, new[]
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("08:00:00", inbound.Id),
            }),
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var value = OkValueOf<ConflictDto>(result);
        Assert.Contains(value.Conflicts, c => c.Contains("reserved for the player"));
    }

    /// <summary>
    /// Regression for the 2026-08-09 real-use defect via the actual save endpoint (not just the
    /// pure validator): a duty day with two same-origin legs on two different airframes must be
    /// rejected, not silently accepted with a meaningless rendered turnaround between them.
    /// </summary>
    [Fact]
    public async Task SaveSchedule_TwoLegsSameDutyDay_DifferentAircraft_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (_, inbound) = await SeedRoundTripRoutesAsync(ctx); // EGPH -> EGGD
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var secondAircraft = await LeaseSecondAircraftAsync(ctx, catalog);

        // Both legs depart EGPH, one on each aircraft - the API cannot even express "one aircraft
        // per day" being violated (DutyDayRequest.FleetAircraftId is a single field), so this can
        // only be reproduced with two SEPARATE duty-day entries for the same day, which
        // SaveScheduleAsync happily accepts as input but the validator must still reject once
        // merged, because dayOfWeek collides.
        var request = new SaveScheduleRequest(new[]
        {
            new DutyDayRequest(1, aircraftId, new[] { new DutyLegRequest("13:05:00", inbound.Id) }),
            new DutyDayRequest(1, secondAircraft, new[] { new DutyLegRequest("14:50:00", inbound.Id) }),
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var value = OkValueOf<ConflictDto>(result);
        Assert.Contains(value.Conflicts, c => c.Contains("single") && c.Contains("aircraft"));

        var schedule = await ctx.Db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id);
        Assert.Null(schedule);
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
            playerPilot.Id, new SaveScheduleRequest(Array.Empty<DutyDayRequest>()), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

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
                new DutyDayRequest(0, aircraftId, new[]
                {
                    new DutyLegRequest("06:00:00", outbound.Id),
                    new DutyLegRequest("08:00:00", inbound.Id),
                }),
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
    public async Task AircraftOptions_ReservedAircraftIsIneligible_WithAQuietReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.ReservedForPlayer = true;
        await ctx.Db.SaveChangesAsync();

        var result = await PilotEndpoints.GetAircraftOptionsAsync(pilot.Id, new AircraftOptionsRequest(1), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<AircraftOptionsDto>(result);
        var option = Assert.Single(value.Options);
        Assert.False(option.Eligible);
        Assert.Contains("reserved for the player", option.Reason);
    }

    [Fact]
    public async Task AircraftOptions_UnreservedGroundedAircraft_IsIneligible_WithMaintenanceReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.Status = FleetAircraftStatus.InMaintenance;
        aircraft.GroundedUntilUtc = DateTimeOffset.UtcNow.AddDays(2);
        await ctx.Db.SaveChangesAsync();

        var result = await PilotEndpoints.GetAircraftOptionsAsync(pilot.Id, new AircraftOptionsRequest(1), ctx.Db, ctx.CurrentUser, CancellationToken.None);

        var value = OkValueOf<AircraftOptionsDto>(result);
        var option = Assert.Single(value.Options);
        Assert.False(option.Eligible);
        Assert.Contains("maintenance", option.Reason);
    }

    [Fact]
    public async Task AircraftOptions_UnreservedIdleAircraft_IsEligible()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var result = await PilotEndpoints.GetAircraftOptionsAsync(pilot.Id, new AircraftOptionsRequest(1), ctx.Db, ctx.CurrentUser, CancellationToken.None);

        var value = OkValueOf<AircraftOptionsDto>(result);
        var option = Assert.Single(value.Options);
        Assert.True(option.Eligible);
        Assert.Null(option.Reason);
    }

    [Fact]
    public async Task LegOptions_ReservedAircraft_EveryRouteIsIllegal_WithOneQuietReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.ReservedForPlayer = true;
        await ctx.Db.SaveChangesAsync();

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "06:00", aircraftId, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<LegOptionsDto>(result);
        Assert.Empty(value.Legal);
        Assert.Contains(value.Illegal, i => i.RouteId == outbound.Id && i.Reason.Contains("reserved for the player"));
    }

    [Fact]
    public async Task LegOptions_EmptySchedule_OffersLegalOptions()
    {
        // Regression for the bug the schedule-builder agent found: options used to validate
        // full-week closure (including the wraparound from the week's last leg back to its first)
        // against a lone candidate, so a brand-new pilot's empty schedule returned legal: [] for
        // every single candidate - a week could never be built up one leg at a time.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "06:00", aircraftId, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<LegOptionsDto>(result);
        Assert.Contains(value.Legal, l => l.RouteId == outbound.Id);
    }

    [Fact]
    public async Task LegOptions_PartiallyBuiltWeek_OffersASensibleNextLeg()
    {
        // A Monday out-and-back (06:00 out, 08:00 back) has already been drafted client-side but
        // NOT saved. Querying a third leg at 12:00 the same day, departing from where the aircraft
        // now sits (EGGD, after the 08:00 return), must be legal - the draft must actually be
        // load-bearing, not just accepted and ignored.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var draft = new[]
        {
            new DutyDayRequest(1, aircraftId, new[]
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("08:00:00", inbound.Id),
            }),
        };

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "12:00", aircraftId, draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<LegOptionsDto>(result);
        Assert.Contains(value.Legal, l => l.RouteId == outbound.Id);

        // The SAME query without the draft (server falls back to nothing known about this pilot's
        // day) still offers the outbound leg legally - it just has no basis to know the aircraft
        // isn't at EGGD, demonstrating the draft only NARROWS options, it doesn't invent conflicts.
        var withoutDraft = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "12:00", aircraftId, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var withoutDraftValue = OkValueOf<LegOptionsDto>(withoutDraft);
        Assert.Contains(withoutDraftValue.Legal, l => l.RouteId == outbound.Id);
    }

    [Fact]
    public async Task LegOptions_FirstLegOfTheWeek_MustDepartFromWhereTheAircraftActuallyIs()
    {
        // Real-use defect (K36): LEBL -> EGKK was offered as a duty day's first leg while the
        // aircraft actually sat at EGKK. Here the aircraft sits at EGPH (not the home airport,
        // EGGD) with nothing drafted yet - the EGGD -> EGPH outbound must now be illegal (it
        // departs from where the aircraft ISN'T), while the EGPH -> EGGD inbound - which departs
        // from where it actually is - stays legal.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.LocationIcao = "EGPH";
        await ctx.Db.SaveChangesAsync();

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "06:00", aircraftId, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<LegOptionsDto>(result);
        Assert.Contains(value.Illegal, i => i.RouteId == outbound.Id && i.Reason.Contains("EGPH") && i.Reason.Contains("EGGD") && i.Reason.Contains("first leg"));
        Assert.DoesNotContain(value.Legal, l => l.RouteId == outbound.Id);
        Assert.Contains(value.Legal, l => l.RouteId == inbound.Id);
    }

    [Fact]
    public async Task LegOptions_LegalOption_CarriesBlockMinutesForTheChosenAircraft_NotASharedDefault()
    {
        // K34: the draft grid used to show a block time computed for SOME default aircraft (the
        // route preview endpoint, called with no aircraft in mind), then jump to a different figure
        // once save recomputed it against the REAL aircraft for the duty day - a slow ATR duty day
        // briefly showing an A320's faster estimate. Proven here by asking leg-options for the SAME
        // route on two aircraft of very different cruise speeds and confirming each gets its own,
        // materially different block time - never one figure shared between them.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var fastAircraftId = await FleetAircraftIdAsync(ctx); // RouteTestContext's A320neo, 450 kts cruise
        await ReleaseReservationAsync(ctx, fastAircraftId);

        var slowType = new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = "ATR72", Family = "ATR", Manufacturer = "ATR", Name = "ATR 72-600",
            PaxCapacity = 70, RangeNm = 900, CruiseTasKts = 275, FuelBurnKgPerHour = 700, MtowTonnes = 23.0,
            MinRunwayFt = 3500, ServiceCeilingFt = 25000, PurchasePrice = 20_000_000m, MonthlyLeaseRate = 100_000m, MatchPatterns = "[]",
        };
        ctx.Db.AircraftTypes.Add(slowType);
        var slowAircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, AircraftTypeId = slowType.Id, Registration = "G-SLOW",
            Ownership = AircraftOwnership.Owned, LocationIcao = "EGGD", Status = FleetAircraftStatus.Active,
            ReservedForPlayer = false, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.FleetAircraft.Add(slowAircraft);
        await ctx.Db.SaveChangesAsync();

        var fastResult = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "06:00", fastAircraftId, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var slowResult = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "06:00", slowAircraft.Id, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var fastOption = Assert.Single(OkValueOf<LegOptionsDto>(fastResult).Legal, l => l.RouteId == outbound.Id);
        var slowOption = Assert.Single(OkValueOf<LegOptionsDto>(slowResult).Legal, l => l.RouteId == outbound.Id);

        Assert.NotNull(fastOption.BlockMinutes);
        Assert.NotNull(slowOption.BlockMinutes);
        Assert.True(slowOption.BlockMinutes > fastOption.BlockMinutes, $"Expected the slower ATR ({slowOption.BlockMinutes}min) to take longer than the A320 ({fastOption.BlockMinutes}min) on the identical route.");
    }

    [Fact]
    public async Task LegOptions_WrongAirportIsStillIllegal_WithItsReason()
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

        var draft = new[] { new DutyDayRequest(1, aircraftId, new[] { new DutyLegRequest("06:00:00", outbound.Id) }) }; // lands EGPH

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "08:00", aircraftId, draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<LegOptionsDto>(result);
        // The EGSS->EGPF candidate departs EGSS, but the aircraft (after the drafted 06:00 leg) is
        // at EGPH - illegal, with a reason naming both airports. No EGPH -> EGSS route was seeded,
        // so the reason must offer to CREATE one.
        Assert.Contains(value.Illegal, i => i.RouteId == elsewhere.Id && i.Reason.Contains("EGPH") && i.Reason.Contains("EGSS"));
        Assert.Contains(value.Illegal, i => i.RouteId == elsewhere.Id && i.Reason.Contains("create"));
        Assert.DoesNotContain(value.Legal, l => l.RouteId == elsewhere.Id);
    }

    [Fact]
    public async Task LegOptions_WrongAirportButTheConnectingRouteAlreadyExists_SaysScheduleALeg()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var elsewhere = await SeedRouteAsync(ctx, "EGSS", "EGPF");
        await SeedRouteAsync(ctx, "EGPH", "EGSS");
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var draft = new[] { new DutyDayRequest(1, aircraftId, new[] { new DutyLegRequest("06:00:00", outbound.Id) }) }; // lands EGPH

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "08:00", aircraftId, draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var value = OkValueOf<LegOptionsDto>(result);
        Assert.Contains(value.Illegal, i => i.RouteId == elsewhere.Id && i.Reason.Contains("schedule a EGPH -> EGSS leg"));
        Assert.DoesNotContain(value.Illegal, i => i.Reason.Contains("you'd need to create"));
    }

    [Fact]
    public async Task LegOptions_GroundedAircraft_EveryRouteIsIllegal_WithItsReason()
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

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "06:00", aircraftId, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var value = OkValueOf<LegOptionsDto>(result);
        Assert.Contains(value.Illegal, i => i.RouteId == outbound.Id && i.Reason.Contains("maintenance"));
        Assert.Empty(value.Legal);
    }

    [Fact]
    public async Task LegOptions_NoRestRoomLeft_IsStillIllegal_WithItsReason()
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
        var draft = new[] { new DutyDayRequest(0, aircraftId, new[] { new DutyLegRequest("20:00:00", outbound.Id) }) };

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "03:00", aircraftId, draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var value = OkValueOf<LegOptionsDto>(result);
        Assert.Contains(value.Illegal, i => i.RouteId == inbound.Id && (i.Reason.Contains("rest") || i.Reason.Contains("hours")));
        // Both routes fail a check against what's already committed BEFORE this slot (inbound on
        // rest, outbound on not departing from where Sunday's leg left the aircraft) - a genuine
        // dead end, not just "inbound happens to be illegal". Nothing should show up as legal here.
        Assert.Empty(value.Legal);
    }

    [Fact]
    public async Task LegOptions_AircraftMidRotation_RepositioningLegsAreLegalWithAWarning_NotHiddenAsIllegal()
    {
        // Real-use defect (2026-08-12): a pilot flying EGGD<->EGPH once daily, five identical
        // weekdays, could not add ANY further leg. Every candidate at 13:00 Monday came back
        // illegal - two because they departed from the wrong end of their route entirely (the
        // aircraft's actual predecessor leg lands it at EGGD, not EGPH/EGSS), and two - EGGD->EGPH
        // and EGGD->EGSS, which genuinely DO depart from where the aircraft is - because inserting
        // either one alone left the aircraft unable to reach Tuesday's already-drafted 06:00 EGGD
        // departure. That second pair is not a reason to refuse the leg - it is a consequence the
        // player is about to take on and can resolve with their very next leg (a return leg later
        // the same day). It must be offered as LEGAL, with a warning attached, never hidden behind
        // "not available".
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx); // EGGD->EGPH, EGPH->EGGD
        var thirdOut = await SeedRouteAsync(ctx, "EGGD", "EGSS");
        var thirdIn = await SeedRouteAsync(ctx, "EGSS", "EGGD");
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        DutyDayRequest MakeDay(int day) => new DutyDayRequest(day, aircraftId, new[]
        {
            new DutyLegRequest("06:00:00", outbound.Id),
            new DutyLegRequest("10:00:00", inbound.Id),
        });
        var draft = new[] { MakeDay(1), MakeDay(2), MakeDay(3), MakeDay(4), MakeDay(5) };

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "13:00", aircraftId, draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<LegOptionsDto>(result);

        // The two routes that genuinely depart from where the aircraft is (EGGD, after the 10:00
        // return) are LEGAL, each carrying a warning about the Tuesday conflict it creates.
        // The continuity-gap warning is "info" severity, not "alert" (2026-08-13 fix) - it is
        // resolvable purely by continuing to build the week (adding a leg after this one), the
        // ordinary halfway point of an ordinary round trip, never an alarm.
        var outboundOption = Assert.Single(value.Legal, l => l.RouteId == outbound.Id);
        Assert.NotEmpty(outboundOption.Warnings);
        Assert.Contains(outboundOption.Warnings, w => w.Message.Contains("Tuesday") && w.Severity == "info");

        var thirdOutOption = Assert.Single(value.Legal, l => l.RouteId == thirdOut.Id);
        Assert.NotEmpty(thirdOutOption.Warnings);
        Assert.Contains(thirdOutOption.Warnings, w => w.Message.Contains("Tuesday") && w.Severity == "info");

        // The two routes that depart from the wrong end entirely (the aircraft is at EGGD, not
        // EGPH/EGSS) are still genuinely illegal - a "before" conflict, not a warning.
        Assert.Contains(value.Illegal, i => i.RouteId == inbound.Id);
        Assert.Contains(value.Illegal, i => i.RouteId == thirdIn.Id);
    }

    /// <summary>
    /// EGGD-EGPH (275.2 nm) block time on <see cref="RouteTestContext"/>'s founding A320neo
    /// (450 kt cruise - see RouteTestContext.cs). Pinned and asserted explicitly in
    /// <see cref="BuildRoundTripsOneLegAtATimeAsync"/> rather than trusting whatever the fixture
    /// happens to produce: the round-trip-count acceptance tests below only mean what they claim
    /// ("four round trips fit in a 13-hour day at a 30-minute turnaround") for THIS specific block
    /// time. If RouteTestContext's aircraft or the seeded distance ever changes, silently
    /// recomputing from whatever comes back would let these tests keep passing (or start failing)
    /// for a completely different, undocumented reason - asserting the pinned value here means that
    /// shows up as a loud, specific failure ("expected block 65, got N") pointing straight at the
    /// fixture change, not as a confusing pass/fail flip in the round-trip count further down. 65
    /// minutes is also close to the user's own description of the flight ("around 1 hour"), which is
    /// why this route/aircraft pair was chosen for the acceptance test in the first place.
    /// </summary>
    private const int EggdEgphBlockMinutes = 65;

    /// <summary>
    /// Acceptance test, direct from the user: "the EGGD-EGPH flight is around 1 hour... for a
    /// virtual pilot they should be able to do a return flight over 4 times a day... it was not
    /// letting me". Builds up Monday's round trips ONE LEG AT A TIME through
    /// <see cref="PilotEndpoints.GetLegOptionsAsync"/> exactly as the picker does - querying the
    /// slot, requiring the route to actually be offered (legal, warning or not), THEN adding it -
    /// never constructing the finished day in one shot. Baseline (Tuesday-Friday) keeps the user's
    /// original single daily round trip, so the next-day pressure from the real report is still
    /// present. Returns the built Monday legs and a human-readable build report for the caller to
    /// assert on or print into a failure message.
    /// </summary>
    private static async Task<(List<DutyLegRequest> MondayLegs, List<string> Report)> BuildRoundTripsOneLegAtATimeAsync(
        RouteTestContext ctx, EconomyConfigCatalog catalog, Guid pilotId, Guid aircraftId,
        Route outbound, Route inbound, List<DutyDayRequest> otherDays, int roundTrips)
    {
        var mondayLegs = new List<DutyLegRequest>();
        var time = TimeSpan.FromHours(6);
        var report = new List<string>();

        for (var trip = 1; trip <= roundTrips; trip++)
        {
            foreach (var routeId in new[] { outbound.Id, inbound.Id })
            {
                var fullDraft = otherDays.ToList();
                if (mondayLegs.Count > 0)
                {
                    fullDraft.Add(new DutyDayRequest(1, aircraftId, mondayLegs.ToArray()));
                }

                var timeText = $"{(int)time.TotalHours:00}:{time.Minutes:00}";
                var result = await PilotEndpoints.GetLegOptionsAsync(
                    pilotId, new LegOptionsRequest(1, timeText, aircraftId, fullDraft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
                var value = OkValueOf<LegOptionsDto>(result);

                var option = value.Legal.FirstOrDefault(l => l.RouteId == routeId);
                if (option is null)
                {
                    var illegalReason = value.Illegal.FirstOrDefault(i => i.RouteId == routeId)?.Reason ?? "(not offered at all)";
                    report.Add($"trip {trip} @ {timeText}: NOT LEGAL - {illegalReason}");
                    return (mondayLegs, report);
                }

                // See EggdEgphBlockMinutes's own doc for why this is asserted rather than just used -
                // a mismatch here means the WHOLE test's premise (how many legs fit in a duty day) no
                // longer holds, and that has to fail loudly here, not as a confusing round-trip-count
                // mismatch several legs later.
                Assert.True(option.BlockMinutes == EggdEgphBlockMinutes,
                    $"Expected EGGD->EGPH block time to still be {EggdEgphBlockMinutes} min (RouteTestContext's founding A320neo) - " +
                    $"got {option.BlockMinutes}. The round-trip counts this test asserts are only valid for the pinned figure.");

                report.Add($"trip {trip} @ {timeText}: block={option.BlockMinutes}, warnings=[{string.Join("; ", option.Warnings.Select(w => $"{w.Severity}:{w.Message}"))}]");
                mondayLegs.Add(new DutyLegRequest($"{timeText}:00", routeId));
                // 30 minutes - the FLOOR PilotScheduleValidator enforces (SchedulingConfig.MinTurnaroundMinutes,
                // corrected 2026-08-12 from 45: see economy-config.json's "scheduling" comment) - the
                // best case for fitting the most legs into a day, never a realistic buffer choice.
                time = time.Add(TimeSpan.FromMinutes(EggdEgphBlockMinutes)).Add(TimeSpan.FromMinutes(30));
            }
        }

        return (mondayLegs, report);
    }

    [Fact]
    public async Task LegOptions_FourRoundTripsInADay_CanBeBuiltOneLegAtATimeThroughThePickerAndSaved()
    {
        // The user's own acceptance case: EGGD<->EGPH is "around 1 hour" each way, and a virtual
        // pilot should be able to fly it "over 4 times a day". At the pinned 65-minute block time and
        // the corrected 30-minute minimum turnaround, 4 round trips (8 legs) is
        // 8*65 + 7*30 = 730 minutes = 12h10m - comfortably inside the untouched 13-hour max duty day
        // (see LegOptions_AFifthRoundTripInADay_HitsTheDutyHourCap_WithAnExplainedReason for
        // confirmation the cap still genuinely bites one round trip further on).
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx); // EGGD->EGPH, EGPH->EGGD
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var otherDays = new List<DutyDayRequest>();
        for (var day = 2; day <= 5; day++)
        {
            otherDays.Add(new DutyDayRequest(day, aircraftId, new[]
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("10:00:00", inbound.Id),
            }));
        }

        var (mondayLegs, report) = await BuildRoundTripsOneLegAtATimeAsync(ctx, catalog, pilot.Id, aircraftId, outbound, inbound, otherDays, roundTrips: 4);
        Assert.True(mondayLegs.Count == 8, $"Expected all 4 round trips (8 legs) to be buildable one at a time. Report: {string.Join(" | ", report)}");

        var fullWeek = otherDays.ToList();
        fullWeek.Add(new DutyDayRequest(1, aircraftId, mondayLegs.ToArray()));
        var saveResult = await PilotEndpoints.SaveScheduleAsync(
            pilot.Id, new SaveScheduleRequest(fullWeek), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.True(StatusCodeOf(saveResult) == StatusCodes.Status200OK, $"Expected the completed week to save. Report: {string.Join(" | ", report)}");
    }

    [Fact]
    public async Task LegOptions_AFifthRoundTripInADay_HitsTheDutyHourCap_WithAnExplainedReason()
    {
        // A 5th EGGD<->EGPH round trip (10 sectors of the pinned 65 min each) cannot fit within the
        // untouched 13-hour max duty day even at the corrected, tighter 30-minute minimum turnaround:
        // by the 9th leg (the 5th round trip's OUTBOUND) cumulative duty is
        // 9*65 + 8*30 = 825 minutes = 13h45m, already past the cap - so this is refused on its own,
        // structural grounds (duty length depends only on entries up to and including it, never on
        // what comes after - see PilotEndpoints.GetLegOptionsAsync's "before slice" remarks) rather
        // than deferred as a warning. This is a genuine, pre-existing duty-hour limit doing its job,
        // not the continuity defect - the point of this test is only to confirm the reason shown when
        // it fires actually names the real cause (a duty-hour limit, with real numbers), rather than
        // reading like an unexplained refusal the way the continuity conflicts used to.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var otherDays = new List<DutyDayRequest>();
        for (var day = 2; day <= 5; day++)
        {
            otherDays.Add(new DutyDayRequest(day, aircraftId, new[]
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("10:00:00", inbound.Id),
            }));
        }

        var (mondayLegs, report) = await BuildRoundTripsOneLegAtATimeAsync(ctx, catalog, pilot.Id, aircraftId, outbound, inbound, otherDays, roundTrips: 5);

        // Exactly 8 legs got through (4 full round trips) before the 5th round trip's OUTBOUND leg
        // was refused - confirming the cap fires exactly once, at the leg that actually breaches it,
        // not earlier and not silently.
        Assert.Equal(8, mondayLegs.Count);
        var lastLine = report[^1];
        Assert.Contains("NOT LEGAL", lastLine);
        Assert.Contains("duty", lastLine, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("13", lastLine); // names both the actual hours run and the configured limit
    }

    /// <summary>
    /// The obvious other half of "how many legs can a duty day fit": a transatlantic aircraft that
    /// genuinely HAS the range still cannot fly a same-day out-and-back, because the hours simply are
    /// not there - one leg alone is most of a duty day. Reuses RouteRangeValidationTests' own
    /// EGGD&lt;-&gt;KJFK figures (KJFK's real coordinates, a nominal 2,900 nm route distance) and a
    /// wide-body type built the same way that file's "Long-range type" is (6,000 nm range,
    /// comfortably covers the sector; 470 kt cruise) rather than inventing new coordinates or a new
    /// airframe. Block time per leg is pinned at 401 minutes (6h41m) - NOT
    /// BlockTimeEstimator.Estimate(2900, 470)'s 400: PilotEndpoints.BuildValidationDataAsync resolves
    /// block time through RoutePreviewCalculator from the seeded airports' real GreatCircle distance
    /// (EGGD to KJFK's actual lat/long), never from a route's stored DistanceNm - the same "always
    /// recompute from real coordinates" rule the app applies everywhere else, so the nominal 2,900 nm
    /// this route is created with and the ~2,902 nm actually flown differ by enough to shift the
    /// rounded cruise minutes by one. Asserted explicitly below so a future change to either figure
    /// fails loudly and specifically, not as a confusing pass/fail flip in the duty-hour assertions
    /// that follow. Either way, a same-day round trip is >= 802 minutes (13h22m) of block time ALONE,
    /// already past the untouched 13-hour max duty day before any turnaround is even added - so this
    /// must be refused on duty-hour grounds specifically, never on range (the aircraft can reach KJFK
    /// many times over), continuity (both legs depart from exactly where the aircraft actually is) or
    /// reservation.
    /// </summary>
    [Fact]
    public async Task LegOptions_TransatlanticSameDayRoundTrip_IsRefused_BecauseTheHoursAreNotThere()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);

        // KJFK's real coordinates - same figures RouteRangeValidationTests.SeedTransatlanticAirportAsync uses.
        ctx.Db.Airports.Add(new Airport
        {
            Icao = "KJFK", Iata = "JFK", Name = "John F Kennedy International", Municipality = "New York",
            Country = "US", Latitude = 40.6413, Longitude = -73.7781, ElevationFt = 13,
            SizeCategory = AirportSizeCategory.Large, HasScheduledService = true, LongestRunwayFt = 14511,
        });

        // Same shape as RouteRangeValidationTests.AddTypeAsync's "Long-range type" - 6,000 nm range
        // (comfortably covers the ~2,900 nm sector many times over) and 470 kt cruise.
        var wideBody = new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = "LONG", Family = "LONG", Manufacturer = "Test", Name = "Long-range type",
            PaxCapacity = 200, RangeNm = 6000, CruiseTasKts = 470, FuelBurnKgPerHour = 5000, MtowTonnes = 200,
            MinRunwayFt = 5500, ServiceCeilingFt = 41000, PurchasePrice = 100_000_000m, MonthlyLeaseRate = 500_000m,
            MatchPatterns = "[]",
        };
        ctx.Db.AircraftTypes.Add(wideBody);
        var aircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, AircraftTypeId = wideBody.Id, Registration = "G-LONG",
            Ownership = AircraftOwnership.Owned, LocationIcao = "EGGD", Status = FleetAircraftStatus.Active,
            ReservedForPlayer = false, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.FleetAircraft.Add(aircraft);

        // Same distance RouteRangeValidationTests.SeedTransatlanticRoundTripAsync uses.
        var outbound = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "KJFK",
            DistanceNm = 2900, BaseFare = 400m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        var inbound = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "KJFK", ArrivalIcao = "EGGD",
            DistanceNm = 2900, BaseFare = 400m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.AddRange(outbound, inbound);
        await ctx.Db.SaveChangesAsync();

        // Step 1: the OUTBOUND leg, first thing in the week - nothing should block this on its own.
        // Range is fine (6,000 nm type on a 2,900 nm sector), the aircraft is already at EGGD (its
        // recorded LocationIcao, and this is the week's first entry for it), and nothing else is
        // scheduled yet.
        var outboundResult = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, "06:00", aircraft.Id, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var outboundValue = OkValueOf<LegOptionsDto>(outboundResult);
        var outboundOption = outboundValue.Legal.SingleOrDefault(l => l.RouteId == outbound.Id);
        Assert.True(outboundOption is not null,
            $"Expected the outbound EGGD->KJFK leg to be legal on its own. Illegal reasons: {string.Join(" | ", outboundValue.Illegal.Select(i => i.Reason))}");
        Assert.Equal(401, outboundOption!.BlockMinutes); // see this test's own doc for why 401, not 400

        // Step 2: the RETURN leg, same day, at the earliest legal turnaround (30 min) after the
        // outbound lands. Genuinely departs from where the aircraft now is (KJFK) - this must be
        // refused ONLY because the day's total hours don't fit, never for direction/continuity/range.
        var draft = new[]
        {
            new DutyDayRequest(1, aircraft.Id, new[] { new DutyLegRequest("06:00:00", outbound.Id) }),
        };
        var returnTime = TimeSpan.FromHours(6).Add(TimeSpan.FromMinutes(401)).Add(TimeSpan.FromMinutes(30)); // 06:00 + 6h41m block + 30 min turnaround = 13:11
        var returnTimeText = $"{(int)returnTime.TotalHours:00}:{returnTime.Minutes:00}";

        var returnResult = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(1, returnTimeText, aircraft.Id, draft), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var returnValue = OkValueOf<LegOptionsDto>(returnResult);

        Assert.DoesNotContain(returnValue.Legal, l => l.RouteId == inbound.Id);
        var reason = returnValue.Illegal.Single(i => i.RouteId == inbound.Id).Reason;

        // The load-bearing assertion: refused because the HOURS are not there, never because of
        // range, positioning/continuity, or reservation. If this fires for any other reason, that is
        // a finding to report, not something to make pass by reshaping the test.
        Assert.True(reason.Contains("duty", StringComparison.OrdinalIgnoreCase) || reason.Contains("hour", StringComparison.OrdinalIgnoreCase),
            $"Expected the return leg to be refused for duty-hour reasons, but got: \"{reason}\"");
        Assert.DoesNotContain("range", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reserved", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("runway", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveSchedule_WeekThatDoesNotClose_IsStillRejected()
    {
        // The other half of the split: leg-options relaxes closure, but PUT /schedule must not. A
        // single Monday leg with no return leaves the aircraft unable to start its own next week's
        // Monday departure from the right airport.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        var request = new SaveScheduleRequest(new[]
        {
            new DutyDayRequest(1, aircraftId, new[] { new DutyLegRequest("06:00:00", outbound.Id) }),
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var schedule = await ctx.Db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id);
        Assert.Null(schedule); // nothing persisted
    }

    [Fact]
    public async Task ScheduleOverview_GroupsByAircraftAndByPilot()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await HirePilotAsync(ctx, catalog);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        var aircraftId = await FleetAircraftIdAsync(ctx);
        await ReleaseReservationAsync(ctx, aircraftId);

        await PilotEndpoints.SaveScheduleAsync(
            pilot.Id,
            new SaveScheduleRequest(new[]
            {
                new DutyDayRequest(0, aircraftId, new[]
                {
                    new DutyLegRequest("06:00:00", outbound.Id),
                    new DutyLegRequest("08:00:00", inbound.Id),
                }),
            }),
            ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var result = await PilotEndpoints.GetScheduleOverviewAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var value = OkValueOf<OverviewDto>(result);
        var aircraftRow = Assert.Single(value.ByAircraft, a => a.FleetAircraftId == aircraftId);
        Assert.Equal(2, aircraftRow.Legs.Count);
        Assert.All(aircraftRow.Legs, l => Assert.Equal(pilot.Id, l.PilotId));

        var pilotRow = Assert.Single(value.ByPilot, p => p.PilotId == pilot.Id);
        var day = Assert.Single(pilotRow.DutyDays);
        Assert.Equal(aircraftId, day.FleetAircraftId);
        Assert.Equal(2, day.Legs.Count);
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

    /// <summary>Adds a second, unreserved, already-serviceable aircraft of the same type/location
    /// as the founding one - used by tests that need two distinct schedulable airframes.</summary>
    private static async Task<Guid> LeaseSecondAircraftAsync(RouteTestContext ctx, EconomyConfigCatalog catalog)
    {
        var second = new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            AircraftTypeId = ctx.AircraftType.Id,
            Registration = "G-TEST2",
            Ownership = AircraftOwnership.Owned,
            LocationIcao = ctx.Airline.HomeAirportIcao,
            Status = FleetAircraftStatus.Active,
            ReservedForPlayer = false,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.FleetAircraft.Add(second);
        await ctx.Db.SaveChangesAsync();
        _ = catalog;
        return second.Id;
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static T OkValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record ScheduleDto(Guid PilotId, List<DutyDayDto> DutyDays, bool AutoSuspendOnMaintenance);

    private sealed record DutyDayDto(int DayOfWeek, Guid FleetAircraftId, string? Registration, List<DutyLegDto> Legs);

    private sealed record DutyLegDto(Guid Id, string DepartureTimeUtc, Guid RouteId, string? DepartureIcao, string? ArrivalIcao, string? FlightNumber, int? BlockMinutes);

    private sealed record ConflictDto(string Error, List<string> Conflicts);

    private sealed record AircraftOptionsDto(List<AircraftOptionDto> Options);

    private sealed record AircraftOptionDto(Guid FleetAircraftId, string Registration, string? AircraftTypeName, string? LocationIcao, bool Eligible, string? Reason, int ScheduledLegsThisWeek);

    private sealed record LegOptionsDto(List<LegOptionDto> Legal, List<IllegalLegOptionDto> Illegal);

    private sealed record LegOptionDto(Guid RouteId, string DepartureIcao, string ArrivalIcao, string? FlightNumber, int? BlockMinutes, List<LegWarningDto> Warnings);

    private sealed record LegWarningDto(string Message, string Severity);

    private sealed record IllegalLegOptionDto(Guid RouteId, string Reason);

    private sealed record OverviewDto(List<AircraftRowDto> ByAircraft, List<PilotRowDto> ByPilot);

    private sealed record AircraftRowDto(Guid FleetAircraftId, string Registration, string LocationIcao, List<OverviewLegDto> Legs);

    private sealed record PilotRowDto(Guid PilotId, string Name, List<OverviewDutyDayDto> DutyDays);

    private sealed record OverviewDutyDayDto(int DayOfWeek, Guid FleetAircraftId, string? Registration, List<OverviewLegDto> Legs);

    private sealed record OverviewLegDto(Guid FleetAircraftId, Guid PilotId, string? PilotName, int DayOfWeek, string DepartureTimeUtc, Guid RouteId, string? DepartureIcao, string? ArrivalIcao, string? FlightNumber);
}

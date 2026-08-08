using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Scheduling;

namespace FSOps.Core.Tests.Scheduling;

public class PilotScheduleValidatorTests
{
    private static readonly Guid PilotA = Guid.NewGuid();
    private static readonly Guid PilotB = Guid.NewGuid();
    private static readonly Guid AircraftX = Guid.NewGuid();
    private static readonly Guid AircraftY = Guid.NewGuid();

    private static readonly Guid RouteOut = Guid.NewGuid(); // EGGD -> EGPH
    private static readonly Guid RouteBack = Guid.NewGuid(); // EGPH -> EGGD
    private static readonly Guid RouteElsewhere = Guid.NewGuid(); // EGSS -> EGPF

    private static readonly SchedulingConfig Config = new()
    {
        MinRestHoursBetweenDutyDays = 10,
        MaxDutyHoursPerDay = 13,
        MinTurnaroundMinutes = 45,
    };

    private static Dictionary<Guid, Route> Routes() => new()
    {
        [RouteOut] = new Route { Id = RouteOut, DepartureIcao = "EGGD", ArrivalIcao = "EGPH", DistanceNm = 275.2 },
        [RouteBack] = new Route { Id = RouteBack, DepartureIcao = "EGPH", ArrivalIcao = "EGGD", DistanceNm = 275.2 },
        [RouteElsewhere] = new Route { Id = RouteElsewhere, DepartureIcao = "EGSS", ArrivalIcao = "EGPF", DistanceNm = 300 },
    };

    private static Dictionary<Guid, FleetAircraft> Fleet(bool reserved = false) => new()
    {
        [AircraftX] = new FleetAircraft { Id = AircraftX, Registration = "G-ONEX", ReservedForPlayer = reserved },
        [AircraftY] = new FleetAircraft { Id = AircraftY, Registration = "G-TWOY", ReservedForPlayer = false },
    };

    private static Dictionary<(Guid, Guid), int> BlockMinutes() => new()
    {
        [(RouteOut, AircraftX)] = 65,
        [(RouteBack, AircraftX)] = 65,
        [(RouteOut, AircraftY)] = 65,
        [(RouteBack, AircraftY)] = 65,
        [(RouteElsewhere, AircraftX)] = 70,
        [(RouteElsewhere, AircraftY)] = 70,
    };

    /// <summary>The airline's whole active-route set as departure/arrival ICAO pairs - what the
    /// "create a route" vs "schedule a leg" wording turns on. EGGD/EGPH/EGSS/EGPF match Routes()
    /// above; pass <paramref name="includeHubToElsewhere"/> to additionally simulate the player
    /// already having an EGPH -> EGSS route (that just isn't scheduled anywhere) without needing a
    /// Route object that any entry actually references. Also adds EGPF -> EGGD in that case: with
    /// requireWeekClosure: true and only two entries, the SAME pair gets checked twice (as the
    /// interior gap AND, cyclically, as the wrap back to the first entry) - closing the wrap with a
    /// route too keeps a "route exists" test isolated to the one interior gap it's actually about,
    /// rather than also tripping the wrap's own, unrelated "create a route" conflict.</summary>
    private static HashSet<(string, string)> ExistingRoutePairs(bool includeHubToElsewhere = false)
    {
        var pairs = new HashSet<(string, string)>
        {
            ("EGGD", "EGPH"),
            ("EGPH", "EGGD"),
            ("EGSS", "EGPF"),
        };

        if (includeHubToElsewhere)
        {
            pairs.Add(("EGPH", "EGSS"));
            pairs.Add(("EGPF", "EGGD"));
        }

        return pairs;
    }

    [Fact]
    public void Validate_ThereAndBackChainWithAmpleRest_IsValid()
    {
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.True(result.IsValid);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Validate_ChainThatDoesNotConnect_ReportsGeographicContinuityConflict_AndOffersToCreateTheRoute()
    {
        // Lands at EGPH but the next leg on the same aircraft departs EGSS - no route connects them,
        // and the airline doesn't have one (ExistingRoutePairs() with no EGPH->EGSS entry) - so the
        // fix offered must be "create a route", not "schedule a leg" (there's nothing to schedule).
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteElsewhere, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("EGPH") && c.Contains("EGSS"));
        Assert.Contains(result.Conflicts, c => c.Contains("create") && c.Contains("EGPH") && c.Contains("EGSS"));
        Assert.DoesNotContain(result.Conflicts, c => c.Contains("schedule a EGPH -> EGSS leg"));
    }

    [Fact]
    public void Validate_ChainThatDoesNotConnect_ButTheRouteAlreadyExists_OffersToScheduleALeg_NotCreateARoute()
    {
        // Same broken chain as above, but this time the airline ALREADY has an EGPH -> EGSS route -
        // it's just not scheduled anywhere. This is the real bug from user feedback 2026-08-08: the
        // old wording said "you'd need a EGPH -> EGSS route" even when the player already had one.
        // What's actually missing is a scheduled repositioning leg, and the message must say so.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteElsewhere, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs(includeHubToElsewhere: true));

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("EGPH") && c.Contains("EGSS"));
        Assert.Contains(result.Conflicts, c => c.Contains("schedule a EGPH -> EGSS leg"));
        Assert.DoesNotContain(result.Conflicts, c => c.Contains("you'd need to create"));
    }

    [Fact]
    public void Validate_SingleWeeklyLegWithNoReturn_ReportsConflict_BecauseTheWeekMustCloseTheLoop()
    {
        // Only one leg all week on this aircraft - it can never get back to EGGD for its own next
        // week's departure, so the repeating-week invariant is violated.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_OverlappingLegsOnSameAircraft_AcrossDifferentPilots_ReportsDoubleBooking()
    {
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotB, DayOfWeek.Monday, new TimeSpan(8, 30, 0), RouteOut, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("double-booked"));
    }

    [Fact]
    public void Validate_TooLittleTurnaround_ReportsConflict()
    {
        // 65-minute block, next departure only 20 minutes after landing - below the 45-minute
        // minimum turnaround.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(9, 45, 0), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("turnaround") || c.Contains("minutes on the ground"));
    }

    [Fact]
    public void Validate_InsufficientRestBetweenDutyDays_ReportsConflict()
    {
        // Monday's only leg departs 20:00 (EGGD->EGPH, 65 min, lands 21:05); Tuesday's only leg
        // departs 07:00 from EGPH (continuity holds - the aircraft is where it needs to be) back to
        // EGGD. Rest between them is under 10 hours (21:05 -> 07:00 next day = 9h55), which is what
        // this test is isolating - turnaround and continuity both stay legal throughout.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(20, 0, 0), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Tuesday, new TimeSpan(7, 0, 0), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("rest") || c.Contains("hours"));
    }

    [Fact]
    public void Validate_DutyDayLongerThanMaximum_ReportsConflict()
    {
        // First departure 06:00, last arrival past 20:00 - well above the 13h maximum duty day.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(6, 0, 0), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(8, 0, 0), RouteBack, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(10, 0, 0), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(12, 0, 0), RouteBack, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(14, 0, 0), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(19, 0, 0), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("maximum duty day"));
    }

    [Fact]
    public void Validate_ReservedAircraft_CanNeverBeAssigned()
    {
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(reserved: true), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("reserved for the player"));
    }

    [Fact]
    public void Validate_WithClosureNotRequired_SingleWeeklyLegWithNoReturn_IsValid()
    {
        // The options-endpoint semantics: a week under construction is legitimately open. The same
        // single, unreturned leg that Validate_SingleWeeklyLegWithNoReturn_ReportsConflict correctly
        // rejects by DEFAULT (requireWeekClosure: true, PUT /schedule's behaviour) must be accepted
        // when the caller is only asking "does this leg fit so far".
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs(), requireWeekClosure: false);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithClosureNotRequired_InteriorContinuityStillEnforced_AndOffersToCreateTheRoute()
    {
        // Only the WRAP (last entry back to the first) is exempted - a genuinely broken interior
        // link (lands EGPH, next drafted leg departs EGSS) must still be caught even with closure
        // relaxed, since the player has actually built this part of the chain. This is the
        // options-endpoint's own semantics (requireWeekClosure: false), so it exercises the same
        // wording fix "via options" as PilotScheduleValidatorTests' save-time cases do.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX), // lands EGPH
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteElsewhere, AircraftX), // departs EGSS
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs(), requireWeekClosure: false);

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("EGPH") && c.Contains("EGSS"));
        Assert.Contains(result.Conflicts, c => c.Contains("create") && c.Contains("EGPH") && c.Contains("EGSS"));
    }

    [Fact]
    public void Validate_WithClosureNotRequired_InteriorContinuityBroken_ButRouteExists_OffersToScheduleALeg()
    {
        // Same broken interior link as above, but this time the connecting route already exists -
        // the options endpoint must offer the same corrected wording as a save-time rejection does.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX), // lands EGPH
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteElsewhere, AircraftX), // departs EGSS
        };

        var result = PilotScheduleValidator.Validate(
            entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs(includeHubToElsewhere: true), requireWeekClosure: false);

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("schedule a EGPH -> EGSS leg"));
        Assert.DoesNotContain(result.Conflicts, c => c.Contains("you'd need to create"));
    }

    [Fact]
    public void Validate_WithClosureNotRequired_InteriorRestStillEnforced()
    {
        // Same exemption boundary applied to the rest/duty check: the WRAP rest gap (last flown day
        // back to the first) is skipped, but rest between two already-drafted duty days is interior
        // and must still be enforced.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(20, 0, 0), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Tuesday, new TimeSpan(7, 0, 0), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs(), requireWeekClosure: false);

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("rest") || c.Contains("hours"));
    }

    [Fact]
    public void Validate_TwoPilotsSharingOneAircraftOnDifferentDays_WithoutOverlap_IsValid()
    {
        // Pilot A flies the aircraft Monday, Pilot B flies it Tuesday - both round trips, no
        // overlap and the aircraft is back at EGGD before each pilot's leg needs it there.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
            new PilotScheduleEntryInput(PilotB, DayOfWeek.Tuesday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotB, DayOfWeek.Tuesday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), BlockMinutes(), Config, ExistingRoutePairs());

        Assert.True(result.IsValid);
    }
}

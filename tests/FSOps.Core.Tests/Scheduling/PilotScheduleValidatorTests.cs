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

    private static readonly Guid NarrowbodyTypeId = Guid.NewGuid();

    private static Dictionary<Guid, FleetAircraft> Fleet(bool reserved = false) => new()
    {
        [AircraftX] = new FleetAircraft { Id = AircraftX, Registration = "G-ONEX", ReservedForPlayer = reserved, AircraftTypeId = NarrowbodyTypeId },
        [AircraftY] = new FleetAircraft { Id = AircraftY, Registration = "G-TWOY", ReservedForPlayer = false, AircraftTypeId = NarrowbodyTypeId },
    };

    /// <summary>Keyed by AircraftTypeId, as the validator's range and runway checks expect. 3,300 nm
    /// published (2,805 nm operational) comfortably covers every route in <see cref="Routes"/>, a
    /// 4,000 ft minimum runway is comfortably short of every airport <see cref="AirportsByIcao"/>
    /// fixtures use, and 78 tonnes MTOW is comfortably under the ICAO "Heavy" threshold - so none of
    /// the three is a non-event for a test that isn't specifically about it. Pass
    /// <paramref name="rangeNm"/>, <paramref name="minRunwayFt"/> or <paramref name="mtowTonnes"/> to
    /// make one relevant.</summary>
    private static Dictionary<Guid, AircraftType> AircraftTypes(int rangeNm = 3300, int minRunwayFt = 4000, double mtowTonnes = 78.0) => new()
    {
        [NarrowbodyTypeId] = new AircraftType
        {
            Id = NarrowbodyTypeId, Name = "Airbus A320", RangeNm = rangeNm, MinRunwayFt = minRunwayFt, MtowTonnes = mtowTonnes,
        },
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

    /// <summary>Keyed by ICAO, as the validator's runway-suitability check expects. Empty by default -
    /// the check gracefully says nothing whenever an airport is missing from this dictionary (see
    /// PilotScheduleValidator's own doc), so every existing test that isn't specifically about runway
    /// suitability sees exactly the same behaviour as before that check existed. Pass explicit
    /// airports to make it relevant.</summary>
    private static Dictionary<string, Airport> AirportsByIcao(params Airport[] airports) =>
        airports.ToDictionary(a => a.Icao);

    private static Airport MakeAirport(string icao, int longestRunwayFt, params Runway[] runways) => new()
    {
        Icao = icao,
        Name = icao,
        LongestRunwayFt = longestRunwayFt,
        Runways = runways.ToList(),
    };

    private static Runway MakeRunway(int lengthFt, string surface = "ASP", bool closed = false) => new()
    {
        Id = Guid.NewGuid(),
        Designator = "09/27",
        LengthFt = lengthFt,
        Surface = surface,
        IsClosed = closed,
    };

    [Fact]
    public void Validate_ThereAndBackChainWithAmpleRest_IsValid()
    {
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs(includeHubToElsewhere: true));

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(reserved: true), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("reserved for the player"));
    }

    [Fact]
    public void Validate_SameReservedAircraftFlownEveryWeekday_ReportsItOnce()
    {
        // Same one-plain-reason rule the over-range check already keeps: five weekdays on the one
        // reserved airframe is ONE problem, and repeating the identical sentence five times is the
        // wall of text that rule exists to prevent.
        var entries = Enumerable.Range(1, 5)
            .Select(day => new PilotScheduleEntryInput(PilotA, (DayOfWeek)day, TimeSpan.FromHours(8), RouteOut, AircraftX))
            .ToArray();

        var result = PilotScheduleValidator.Validate(
            entries, Routes(), Fleet(reserved: true), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Single(result.Conflicts);
        Assert.Contains("G-ONEX", result.Conflicts[0]);
    }

    [Fact]
    public void Validate_BrokenChain_QuotesTheArrivalTimeOfTheLegThatLanded_NotItsDeparture()
    {
        // "G-ONEX lands at EGPH (Monday 08:00)" for a leg that DEPARTED at 08:00 reads as a broken
        // clock - the sentence must quote when it actually lands (08:00 + 65 min block = 09:05).
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(12), RouteElsewhere, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        var chainConflict = Assert.Single(result.Conflicts, c => c.Contains("lands at EGPH"));
        Assert.Contains("lands at EGPH (Monday 09:05)", chainConflict);
        Assert.Contains("departs EGSS (Monday 12:00)", chainConflict);
    }

    [Fact]
    public void Validate_LegBeyondTheAircraftsRange_IsRefusedWithOnePlainReason()
    {
        // J24: an A320 must never be scheduled beyond its range. 275.2 nm against a 300 nm published
        // range (255 nm operational) is over by a whisker, which is the interesting boundary - the
        // refusal comes from the derated figure, not the catalogue one.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(
            entries, Routes(), Fleet(), AircraftTypes(rangeNm: 300), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.All(result.Conflicts, c => Assert.Contains("beyond G-ONEX's", c));
        Assert.Contains(result.Conflicts, c => c.Contains("EGGD -> EGPH") && c.Contains("275 nm") && c.Contains("255 nm"));
        // One reason per (route, aircraft) pair, not one per leg - the two entries above are two
        // different routes, so exactly two conflicts, not four and not one.
        Assert.Equal(2, result.Conflicts.Count);
    }

    [Fact]
    public void Validate_SameOverRangeLegFlownEveryWeekday_ReportsItOnce()
    {
        var entries = Enumerable.Range(1, 5)
            .Select(day => new PilotScheduleEntryInput(PilotA, (DayOfWeek)day, TimeSpan.FromHours(8), RouteOut, AircraftX))
            .ToArray();

        var result = PilotScheduleValidator.Validate(
            entries, Routes(), Fleet(), AircraftTypes(rangeNm: 300), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.Single(result.Conflicts);
    }

    // ---- J28: runway suitability (length AND surface) -----------------------------------------

    [Fact]
    public void Validate_LegToAnAirportTooShortForTheAircraft_IsRefusedWithOnePlainReason()
    {
        // EGGD is comfortably long enough (9,500 ft); EGPH's longest runway (8,500 ft) is
        // comfortably short of the 9,000 ft requirement - a wide margin either way so this is never
        // a rounding-boundary case, and EGPH is consistently the one named regardless of direction.
        var airports = AirportsByIcao(
            MakeAirport("EGGD", 9500, MakeRunway(9500)),
            MakeAirport("EGPH", 8500, MakeRunway(8500)));
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(
            entries, Routes(), Fleet(), AircraftTypes(minRunwayFt: 9000), BlockMinutes(), airports, Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.All(result.Conflicts, c => Assert.Contains("too short for G-ONEX", c));
        Assert.Contains(result.Conflicts, c => c.Contains("EGPH") && c.Contains("8,500 ft") && c.Contains("9,000 ft"));
        // One reason per (route, aircraft) pair, same as the range check.
        Assert.Equal(2, result.Conflicts.Count);
    }

    [Fact]
    public void Validate_HeavyAircraftOnAGrassRunway_IsRefused_RegardlessOfLength()
    {
        // EGPH's only runway is a very long 10,000 ft grass strip - plenty of length, but a heavy
        // aircraft still cannot use it. Proves the length check alone would wrongly pass this.
        var airports = AirportsByIcao(
            MakeAirport("EGGD", 8000, MakeRunway(8000)),
            MakeAirport("EGPH", 10000, MakeRunway(10000, surface: "GRASS")));
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(
            entries, Routes(), Fleet(), AircraftTypes(minRunwayFt: 5000, mtowTonnes: 250), BlockMinutes(), airports, Config, ExistingRoutePairs());

        Assert.False(result.IsValid);
        Assert.All(result.Conflicts, c => Assert.Contains("too soft for G-ONEX", c));
        Assert.Contains(result.Conflicts, c => c.Contains("EGPH") && c.Contains("heavy aircraft need a paved runway"));
    }

    [Fact]
    public void Validate_LightAircraftOnAGrassRunway_IsAllowed()
    {
        // Same grass runway as above, but this aircraft is under the ICAO "Heavy" threshold - grass
        // is never a problem for it, no matter how it's phrased.
        var airports = AirportsByIcao(
            MakeAirport("EGGD", 8000, MakeRunway(8000)),
            MakeAirport("EGPH", 10000, MakeRunway(10000, surface: "GRASS")));
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(
            entries, Routes(), Fleet(), AircraftTypes(minRunwayFt: 5000, mtowTonnes: 20), BlockMinutes(), airports, Config, ExistingRoutePairs());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_LegExactlyAtTheOperationalRangeLimit_IsAllowed()
    {
        // 275.2 nm against 324 nm published = 275.4 nm operational. The boundary must be inclusive:
        // "can just about make it" is a legal schedule, not a refusal.
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(8), RouteOut, AircraftX),
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, TimeSpan.FromHours(10), RouteBack, AircraftX),
        };

        var result = PilotScheduleValidator.Validate(
            entries, Routes(), Fleet(), AircraftTypes(rangeNm: 324), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

        Assert.True(result.IsValid);
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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs(), requireWeekClosure: false);

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs(), requireWeekClosure: false);

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
            entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs(includeHubToElsewhere: true), requireWeekClosure: false);

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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs(), requireWeekClosure: false);

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("rest") || c.Contains("hours"));
    }

    /// <summary>
    /// Regression for the 2026-08-09 real-use defect: a duty day was accepted
    /// with two legs, both EGPH -&gt; EGLL, on two DIFFERENT airframes (G-PKS0, then a rendered "38m
    /// turnaround", then G-LHRE) - impossible, because after the first leg the first aircraft is at
    /// EGLL, and the second aircraft was never checked to be at EGPH at all. Root cause:
    /// <c>ValidateAircraftChains</c> groups strictly by <c>FleetAircraftId</c>, so two legs on
    /// different aircraft in the same pilot-day were never compared to each other - each aircraft's
    /// own (single-leg) chain looked completely fine in isolation. This exact shape - two
    /// same-origin legs, one duty day, two different airframes - must now be rejected by the
    /// aircraft-per-duty-day invariant before continuity is even checked.
    /// </summary>
    [Fact]
    public void Validate_TwoSameOriginLegsOneDutyDay_DifferentAirframes_IsRejected()
    {
        var entries = new[]
        {
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(13, 5, 0), RouteBack, AircraftX), // EGPH -> EGGD on G-ONEX
            new PilotScheduleEntryInput(PilotA, DayOfWeek.Monday, new TimeSpan(14, 50, 0), RouteBack, AircraftY), // EGPH -> EGGD on G-TWOY
        };

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs(), requireWeekClosure: false);

        Assert.False(result.IsValid);
        Assert.Contains(result.Conflicts, c => c.Contains("G-ONEX") && c.Contains("G-TWOY"));
        Assert.Contains(result.Conflicts, c => c.Contains("single") && c.Contains("aircraft"));
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

        var result = PilotScheduleValidator.Validate(entries, Routes(), Fleet(), AircraftTypes(), BlockMinutes(), AirportsByIcao(), Config, ExistingRoutePairs());

        Assert.True(result.IsValid);
    }
}

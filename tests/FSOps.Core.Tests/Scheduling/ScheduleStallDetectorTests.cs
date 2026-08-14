using FSOps.Core.Entities;
using FSOps.Core.Scheduling;

namespace FSOps.Core.Tests.Scheduling;

/// <summary>
/// The staleness rule. Most of these are about NOT crying wolf: the detector has to stay silent for
/// every out-of-position case a rolling weekly pattern repairs on its own, or the indicator becomes
/// noise the player learns to ignore - at which point it fails on the one occasion it matters.
/// </summary>
public class ScheduleStallDetectorTests
{
    private static FleetAircraft Aircraft(Guid id, string registration, string locationIcao, FleetAircraftStatus status = FleetAircraftStatus.Active) =>
        new()
        {
            Id = id,
            AirlineId = Guid.NewGuid(),
            AircraftTypeId = Guid.NewGuid(),
            Registration = registration,
            LocationIcao = locationIcao,
            Status = status,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

    private static ScheduledLegPosition Leg(Guid aircraftId, DayOfWeek day, int hour, string departureIcao) =>
        new(aircraftId, day, TimeSpan.FromHours(hour), departureIcao);

    [Fact]
    public void AnAircraftParkedWhereNoLegDepartsFrom_IsReportedAsStalled()
    {
        // EGGD -> EGPH -> EGGD every Monday, but the airframe is sitting at EGPF. Nothing in the
        // pattern departs EGPF, so nothing will ever move it and every occurrence from now on is
        // recorded as missed. This is the case worth telling the player about.
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-STUK", "EGPF") };
        var legs = new[] { Leg(id, DayOfWeek.Monday, 8, "EGGD"), Leg(id, DayOfWeek.Monday, 12, "EGPH") };

        var stalled = Assert.Single(ScheduleStallDetector.Detect(fleet, legs));

        Assert.Equal("G-STUK", stalled.Registration);
        Assert.Equal("EGPF", stalled.LocationIcao);
        Assert.Equal("EGGD", stalled.PatternStartIcao);
    }

    [Fact]
    public void AnAircraftParkedAtAnAirportThePatternDepartsFromLater_IsNotStalled_BecauseItRepairsItself()
    {
        // This is the whole reason the rule is not "is it where the week starts". The airframe is at
        // EGPH; the week's FIRST leg departs EGGD, so a first-leg check would fire. But the Monday
        // 12:00 EGPH -> EGGD leg can fly, which puts it back at EGGD, and the closed weekly loop
        // then lines up on its own. Warning here would be crying wolf about a pattern that is
        // already fixing itself.
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-FINE", "EGPH") };
        var legs = new[] { Leg(id, DayOfWeek.Monday, 8, "EGGD"), Leg(id, DayOfWeek.Monday, 12, "EGPH") };

        Assert.Empty(ScheduleStallDetector.Detect(fleet, legs));
    }

    [Fact]
    public void AnAircraftAtThePatternsStartingPoint_IsNotStalled()
    {
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-HOME", "EGGD") };
        var legs = new[] { Leg(id, DayOfWeek.Monday, 8, "EGGD"), Leg(id, DayOfWeek.Monday, 12, "EGPH") };

        Assert.Empty(ScheduleStallDetector.Detect(fleet, legs));
    }

    [Fact]
    public void AnAircraftInFlightIsNeverReported_BecauseItsRecordedLocationIsKnowablyStale()
    {
        // Mid-sector, LocationIcao is the airport it left, not where it is. Comparing against it
        // would produce a stall warning for an aircraft that is at that moment doing exactly what
        // the schedule asked.
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-AIR", "EGPF", FleetAircraftStatus.InFlight) };
        var legs = new[] { Leg(id, DayOfWeek.Monday, 8, "EGGD") };

        Assert.Empty(ScheduleStallDetector.Detect(fleet, legs));
    }

    [Fact]
    public void AnAircraftInMaintenanceIsNeverReported_BecauseThatIsAPauseRatherThanAStall()
    {
        // A check ends, and the schedule resumes with no intervention. Calling that "stopped" would
        // tell the player to go and fix something the app is already handling.
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-FIXD", "EGPF", FleetAircraftStatus.InMaintenance) };
        var legs = new[] { Leg(id, DayOfWeek.Monday, 8, "EGGD") };

        Assert.Empty(ScheduleStallDetector.Detect(fleet, legs));
    }

    [Fact]
    public void AnIdleAircraftWithNoLegsAtAll_IsNotAStalledSchedule()
    {
        var scheduled = Guid.NewGuid();
        var idle = Guid.NewGuid();
        var fleet = new[] { Aircraft(scheduled, "G-BUSY", "EGGD"), Aircraft(idle, "G-IDLE", "EGPF") };
        var legs = new[] { Leg(scheduled, DayOfWeek.Monday, 8, "EGGD") };

        Assert.Empty(ScheduleStallDetector.Detect(fleet, legs));
    }

    [Fact]
    public void LocationMatchingIsCaseInsensitive()
    {
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-CASE", "eggd") };
        var legs = new[] { Leg(id, DayOfWeek.Monday, 8, "EGGD") };

        Assert.Empty(ScheduleStallDetector.Detect(fleet, legs));
    }

    [Fact]
    public void PatternStartIsTheChronologicallyEarliestLegOfTheWeek_NotWhicheverRowCameBackFirst()
    {
        // Sunday is day 0, so a Saturday leg listed first must not be mistaken for the start.
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-ORDR", "LFPG") };
        var legs = new[]
        {
            Leg(id, DayOfWeek.Saturday, 18, "EGSS"),
            Leg(id, DayOfWeek.Sunday, 6, "EGGD"),
            Leg(id, DayOfWeek.Sunday, 5, "EGPH"),
        };

        var stalled = Assert.Single(ScheduleStallDetector.Detect(fleet, legs));

        Assert.Equal("EGPH", stalled.PatternStartIcao);
    }

    [Fact]
    public void TheMessageNamesTheAircraft_WhereItIs_WhereItIsNeeded_AndBothWaysOut()
    {
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-STUK", "EGPF") };
        var legs = new[] { Leg(id, DayOfWeek.Monday, 8, "EGGD") };

        var message = Assert.Single(ScheduleStallDetector.Detect(fleet, legs)).Message;

        Assert.Contains("G-STUK", message);
        Assert.Contains("EGPF", message);
        Assert.Contains("EGGD", message);
        Assert.Contains("Fleet page", message);
        // It has to read as information, not as a failure: the schedule is valid and still rolling.
        Assert.Contains("still repeating", message);
    }

    [Fact]
    public void EachStalledAircraftIsReportedOnce_HoweverManyLegsItHas()
    {
        var id = Guid.NewGuid();
        var fleet = new[] { Aircraft(id, "G-MANY", "LFPG") };
        var legs = Enumerable.Range(0, 7)
            .Select(d => Leg(id, (DayOfWeek)d, 8, "EGGD"))
            .ToList();

        Assert.Single(ScheduleStallDetector.Detect(fleet, legs));
    }
}

using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// FlightStatus is persisted as text (see FlightConfiguration), which is the mapping this project
/// has twice nearly shipped a bug through - a value written into permanent history that nothing can
/// read back. These tests hold the two things that keep it safe: every declared member survives a
/// round trip through the database as its own name, and an unset Flight is born as a status a real
/// creation path would have given it rather than as whatever member happens to be declared first.
/// </summary>
public class FlightStatusPersistenceTests
{
    [Fact]
    public void ANewFlight_IsBornInProgress_NotWhicheverEnumMemberIsDeclaredFirst()
    {
        // These two coincide today. The test is about the ones being equal by INTENT rather than by
        // accident: reordering FlightStatus must never silently change what an unset flight is.
        Assert.Equal(FlightStatus.InProgress, new Flight().Status);
    }

    [Fact]
    public void ThereIsNoPlannedStatus_BecauseNoCodePathHasEverWrittenOne()
    {
        // A flight that exists but has not begun is not a state this app has - the plan lives in
        // PilotScheduleEntry, and a Flight row is only ever created at the moment one starts (or,
        // for a virtual occurrence, at the moment it is resolved). Re-adding Planned would be a
        // flight lifecycle change, not an enum edit, so this guards it deliberately.
        Assert.DoesNotContain("Planned", Enum.GetNames<FlightStatus>());
    }

    [Fact]
    public async Task EveryFlightStatus_RoundTripsThroughTheDatabaseAsItsOwnName()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var statuses = Enum.GetValues<FlightStatus>();
        var idsByStatus = new Dictionary<FlightStatus, Guid>();

        foreach (var status in statuses)
        {
            var id = Guid.NewGuid();
            idsByStatus[status] = id;
            ctx.Db.Flights.Add(new Flight
            {
                Id = id,
                AirlineId = ctx.Airline.Id,
                RouteId = Guid.NewGuid(),
                FleetAircraftId = Guid.NewGuid(),
                PilotId = Guid.NewGuid(),
                Status = status,
                PlannedDepartureUtc = DateTimeOffset.UtcNow,
                PlannedBlockMinutes = 75,
                TitleFlown = "Airbus A320neo",
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }

        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        foreach (var (status, id) in idsByStatus)
        {
            var reloaded = await ctx.Db.Flights.SingleAsync(f => f.Id == id);
            Assert.Equal(status, reloaded.Status);
        }

        // And on the wire it is genuinely the member's own name - not an ordinal, and not the empty
        // string an EF-scaffolded default would have written.
        await using var command = ctx.Connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT Status FROM Flights ORDER BY Status;";
        var stored = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                stored.Add(reader.GetString(0));
            }
        }

        Assert.Equal(Enum.GetNames<FlightStatus>().OrderBy(n => n, StringComparer.Ordinal), stored);
        Assert.DoesNotContain(string.Empty, stored);
    }

    [Fact]
    public async Task NoStatusEverStoredByThisAppIsUnreadable()
    {
        // The failure mode this guards is specific: a status text in the database that no member of
        // the enum matches makes EVERY read of that row throw, not just the status field, so one
        // bad write takes a flight out of history entirely. Reading them all back through EF is the
        // only way to be sure the text and the enum still agree.
        using var ctx = await RouteTestContext.CreateAsync();

        foreach (var status in Enum.GetValues<FlightStatus>())
        {
            ctx.Db.Flights.Add(new Flight
            {
                Id = Guid.NewGuid(),
                AirlineId = ctx.Airline.Id,
                RouteId = Guid.NewGuid(),
                FleetAircraftId = Guid.NewGuid(),
                PilotId = Guid.NewGuid(),
                Status = status,
                PlannedDepartureUtc = DateTimeOffset.UtcNow,
                PlannedBlockMinutes = 60,
                TitleFlown = "Airbus A320neo",
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }

        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var all = await ctx.Db.Flights.ToListAsync();
        Assert.Equal(Enum.GetValues<FlightStatus>().Length, all.Count);
    }
}

using FSOps.Core.Contracts;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
using FSOps.Data;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using FSOps.Sim;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The contract API end to end: browsing the board, taking a job, flying its legs in order, and
/// handing it back.
///
/// <para>Driven through the endpoints themselves rather than the services beneath them, because the
/// refusals are half the point. A screen has to be able to explain why a leg cannot be started, and a
/// refusal that comes back as a bare status code is one the UI cannot render into anything a player
/// can act on.</para>
/// </summary>
public class ContractEndpointsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    // ---------- The board ----------

    /// <summary>
    /// <b>The board is a board, not a lever.</b> Two reads in the same period return the same jobs -
    /// the same identities, not merely the same shapes - so reloading cannot reroll it. If it could,
    /// the rational play would be to keep reloading until something good appeared.
    /// </summary>
    [Fact]
    public async Task ReadingTheBoardTwiceInTheSamePeriod_ReturnsTheSameJobs()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);
        var service = CreateBoardService(ctx, Now);

        var first = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var second = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.NotEmpty(first.Offered);
        Assert.Equal(
            first.Offered.Select(c => c.Id).OrderBy(id => id),
            second.Offered.Select(c => c.Id).OrderBy(id => id));

        // And nothing was written twice - the unique index on (airline, bucket, slot) would have
        // rejected it, but a second board quietly appearing under a new bucket would not.
        Assert.Equal(first.Offered.Count, await ctx.Db.Contracts.CountAsync());
    }

    /// <summary>
    /// Offers that were never taken expire when the board moves on - they do not accumulate, and they
    /// are Expired rather than Abandoned. An offer nobody took is not a job somebody walked out of,
    /// and it must never carry a charge.
    /// </summary>
    [Fact]
    public async Task WhenTheBoardRefreshes_UntakenOffersExpireWithoutCharge()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        var clock = new FakeClock(Now);
        var service = CreateBoardService(ctx, clock);

        var first = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        var firstIds = first.Offered.Select(c => c.Id).ToHashSet();
        Assert.NotEmpty(firstIds);

        clock.UtcNow = Now.AddDays(1);
        ctx.Db.ChangeTracker.Clear();
        var second = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.NotEqual(first.Bucket, second.Bucket);
        Assert.DoesNotContain(second.Offered, c => firstIds.Contains(c.Id));

        var expired = await ctx.Db.Contracts.Where(c => firstIds.Contains(c.Id)).ToListAsync();
        Assert.All(expired, c => Assert.Equal(ContractStatus.Expired, c.Status));

        // Expiring an untaken offer costs nothing at all.
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.Category == LedgerCategory.ContractFee).ToListAsync());
    }

    /// <summary>An accepted job survives the refresh: it belongs to the player now, not to the board.</summary>
    [Fact]
    public async Task AnAcceptedJob_SurvivesTheBoardRefreshing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        var clock = new FakeClock(Now);
        var service = CreateBoardService(ctx, clock);
        var board = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        var taken = board.Offered.First();

        await ContractEndpoints.AcceptAsync(taken.Id, ctx.Db, ctx.CurrentUser, clock, CancellationToken.None);

        clock.UtcNow = Now.AddDays(3);
        ctx.Db.ChangeTracker.Clear();
        var later = await service.GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        Assert.Contains(later.Accepted, c => c.Id == taken.Id);
        Assert.DoesNotContain(later.Offered, c => c.Id == taken.Id);
    }

    /// <summary>
    /// With almost nothing available the board is thin, and the API says so in words a player can act
    /// on. This is the honest-degradation requirement checked at the boundary the UI actually reads.
    /// </summary>
    [Fact]
    public async Task WithNoAircraftAvailable_TheBoardEndpointExplainsItselfRatherThanReturningNothing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);

        // Tick every catalogue aircraft OFF, which is the strongest form of "nothing available".
        var settings = new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ctx.CurrentUser.UserId,
            SimEdition = SimEdition.Standard,
            SimAircraftOverridesJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                on = Array.Empty<string>(),
                off = ContractAircraftCatalogue.All.Select(a => a.TypeDesignator).ToArray(),
            }),
        };
        ctx.Db.UserSettings.Add(settings);
        await ctx.Db.SaveChangesAsync();

        var result = await ContractEndpoints.BoardAsync(
            ctx.Db, ctx.CurrentUser, CreateBoardService(ctx, Now), CancellationToken.None);

        var body = OkValueOf<BoardProbe>(result);
        Assert.Empty(body.Offered);
        Assert.NotNull(body.Limitation.Message);
        Assert.Contains("Settings", body.Limitation.Message);
        Assert.Equal(0, body.Limitation.AvailableAircraftCount);
    }

    // ---------- Accepting ----------

    [Fact]
    public async Task AcceptingAJob_MovesItOffTheBoardAndStartsItsDeadline()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);
        var clock = new FakeClock(Now);
        var board = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        var offer = board.Offered.First();
        var advertisedDeadline = offer.DeadlineUtc;

        var result = await ContractEndpoints.AcceptAsync(offer.Id, ctx.Db, ctx.CurrentUser, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var accepted = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == offer.Id);
        Assert.Equal(ContractStatus.Accepted, accepted.Status);
        Assert.Equal(Now, accepted.AcceptedUtc);

        // The deadline the player was shown before accepting is the deadline that applies. It is
        // stamped at generation and never recalculated on acceptance, so it cannot move under them.
        Assert.Equal(advertisedDeadline, accepted.DeadlineUtc);
    }

    /// <summary>Accepting twice is a double click, not an error - and it must not restart the deadline.</summary>
    [Fact]
    public async Task AcceptingTheSameJobTwice_IsHarmless()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);
        var clock = new FakeClock(Now);
        var board = await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);
        var offer = board.Offered.First();

        await ContractEndpoints.AcceptAsync(offer.Id, ctx.Db, ctx.CurrentUser, clock, CancellationToken.None);
        clock.UtcNow = Now.AddHours(2);
        var second = await ContractEndpoints.AcceptAsync(offer.Id, ctx.Db, ctx.CurrentUser, clock, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(second));
        var accepted = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == offer.Id);
        Assert.Equal(Now, accepted.AcceptedUtc);
    }

    // ---------- Flying it ----------

    /// <summary>
    /// <b>Legs are flown in order</b>, and the endpoint always hands back the next outstanding one.
    /// The order is the whole shape of a ferry: the aeroplane is physically where the last leg left
    /// it, so leg four cannot begin until leg three has landed.
    /// </summary>
    [Fact]
    public async Task StartingALeg_AlwaysGivesTheNextOutstandingOne_AndCreatesAContractFlight()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 3);
        var legs = contract.Legs.OrderBy(l => l.Sequence).ToList();

        var result = await StartLegAsync(ctx, contract.Id);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync();
        Assert.Equal(legs[0].Id, flight.ContractLegId);

        // The two nulls that make the whole guarantee structural.
        Assert.Null(flight.RouteId);
        Assert.Null(flight.FleetAircraftId);
        Assert.Equal(FlightStatus.InProgress, flight.Status);
        Assert.Equal(legs[0].PlannedBlockMinutes, flight.PlannedBlockMinutes);
    }

    /// <summary>
    /// One sector at a time, airline-wide - the same gate an ordinary flight has, and the refusal is
    /// a sentence rather than a bare conflict.
    /// </summary>
    [Fact]
    public async Task StartingALegWhileAFlightIsInProgress_IsRefusedWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 2);

        await StartLegAsync(ctx, contract.Id);
        var second = await StartLegAsync(ctx, contract.Id);

        Assert.Equal(StatusCodes.Status409Conflict, StatusCodeOf(second));
        Assert.Contains("already in progress", ErrorOf(second));
    }

    [Fact]
    public async Task StartingALegOnAJobThatWasNeverAccepted_IsRefusedWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 1);
        var row = await ctx.Db.Contracts.SingleAsync(c => c.Id == contract.Id);
        row.Status = ContractStatus.Offered;
        await ctx.Db.SaveChangesAsync();

        var result = await StartLegAsync(ctx, contract.Id);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("accept it before flying it", ErrorOf(result));
    }

    [Fact]
    public async Task StartingALegWhenEveryLegIsFlown_IsRefusedWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 1);

        foreach (var leg in await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).ToListAsync())
        {
            leg.FlightId = Guid.NewGuid();
            leg.FlownUtc = Now;
        }

        await ctx.Db.SaveChangesAsync();

        var result = await StartLegAsync(ctx, contract.Id);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("already been flown", ErrorOf(result));
    }

    // ---------- Handing it back ----------

    /// <summary>
    /// Abandoning returns the charge <b>and the sentence explaining it</b>, so the screen can tell the
    /// player what it cost and why rather than leaving them to discover a number in their ledger.
    /// </summary>
    [Fact]
    public async Task AbandoningAJob_ReturnsTheChargeAndTheReasonForIt()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 3);

        // Mark the first leg flown, so there is genuinely an aircraft stranded somewhere.
        var legs = await ctx.Db.ContractLegs.Where(l => l.ContractId == contract.Id).OrderBy(l => l.Sequence).ToListAsync();
        legs[0].FlightId = Guid.NewGuid();
        legs[0].FlownUtc = Now;
        await ctx.Db.SaveChangesAsync();

        var result = await ContractEndpoints.AbandonAsync(
            contract.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), new FakeClock(Now), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var body = OkValueOf<AbandonProbe>(result);

        Assert.Equal(2, body.UnflownLegCount);
        Assert.True(body.Charge > 0);
        Assert.Contains("recover the aircraft", body.Reason);

        var closed = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Abandoned, closed.Status);
        Assert.Equal("You handed this job back.", closed.ClosedReason);
    }

    /// <summary>
    /// A leg in the air is not something to resolve behind the player's back: abandoning underneath a
    /// tracked flight would leave it pointing at a closed contract.
    /// </summary>
    [Fact]
    public async Task AbandoningWhileALegIsInTheAir_IsRefusedWithAReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var contract = await SeedAcceptedContractAsync(ctx, 2);
        await StartLegAsync(ctx, contract.Id);

        var result = await ContractEndpoints.AbandonAsync(
            contract.Id, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), new FakeClock(Now), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("still in progress", ErrorOf(result));
    }

    /// <summary>
    /// A deadline that has genuinely passed closes the job, and it does so lazily on a board read
    /// rather than from a background pass - so the player only ever meets it at a moment they asked
    /// to look, with a date they were shown before they accepted.
    /// </summary>
    [Fact]
    public async Task AnAcceptedJobPastItsDeadline_ClosesItselfOnTheNextBoardRead()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedWorldAsync(ctx);
        var contract = await SeedAcceptedContractAsync(ctx, 2);

        var clock = new FakeClock(Now.AddDays(60));
        ctx.Db.ChangeTracker.Clear();
        await CreateBoardService(ctx, clock).GetBoardAsync(ctx.Airline, ctx.CurrentUser.UserId, CancellationToken.None);

        var closed = await ctx.Db.Contracts.AsNoTracking().SingleAsync(c => c.Id == contract.Id);
        Assert.Equal(ContractStatus.Abandoned, closed.Status);
        Assert.Contains("deadline", closed.ClosedReason!);
    }

    // ---------- Helpers ----------

    private static int StatusCodeOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static T OkValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return System.Text.Json.JsonSerializer.Deserialize<T>(
            json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    /// <summary>
    /// The <c>error</c> field of a refusal. Every refusal in this API carries one, because a screen
    /// has to be able to tell the player what to do next - a bare status code is not something a UI
    /// can render into a sentence.
    /// </summary>
    private static string ErrorOf(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out var error), $"No 'error' field on the refusal: {json}");
        return error.GetString() ?? string.Empty;
    }

    private sealed record BoardProbe(long Bucket, List<object> Offered, List<object> Accepted, LimitationProbe Limitation);

    private sealed record LimitationProbe(int AvailableAircraftCount, int OriginCount, int Requested, int Generated, string? Message);

    private sealed record AbandonProbe(object Contract, decimal Charge, int UnflownLegCount, int UnflownBlockMinutes, string Reason);

    private static Task<IResult> StartLegAsync(RouteTestContext ctx, Guid contractId)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(), telemetry, new NoOpHubContext(),
            EconomyConfigCatalog.Default(), null, NullLogger<FlightLifecycleService>.Instance);

        return ContractEndpoints.StartLegAsync(
            contractId, ctx.Db, ctx.CurrentUser, lifecycle, telemetry, new FakeClock(Now), CancellationToken.None);
    }

    private static ContractBoardService CreateBoardService(RouteTestContext ctx, DateTimeOffset now) =>
        CreateBoardService(ctx, new FakeClock(now));

    private static ContractBoardService CreateBoardService(RouteTestContext ctx, FakeClock clock) =>
        new(ctx.Db,
            new SimAircraftService(ctx.Db, new InstalledAircraftScanner(), clock, NullLogger<SimAircraftService>.Instance),
            EconomyConfigCatalog.Default(),
            clock,
            NullLogger<ContractBoardService>.Instance);

    /// <summary>
    /// Enough airports for the generator to have somewhere to send a job. RouteTestContext seeds four
    /// British ones; these add the reach a board needs to produce anything but the shortest hop.
    /// </summary>
    private static async Task SeedWorldAsync(RouteTestContext ctx)
    {
        (string Icao, double Lat, double Lon, int Runway, AirportSizeCategory Size)[] extras =
        [
            ("EGLL", 51.470, -0.454, 12_800, AirportSizeCategory.Large),
            ("EGPC", 58.459, -3.093, 5_900, AirportSizeCategory.Small),
            ("EGPO", 58.216, -6.331, 7_200, AirportSizeCategory.Small),
            ("EGJJ", 49.208, -2.195, 5_597, AirportSizeCategory.Small),
            ("EGNS", 54.083, -4.624, 5_754, AirportSizeCategory.Small),
            ("EIDW", 53.421, -6.270, 8_652, AirportSizeCategory.Large),
            ("LFPG", 49.010, 2.548, 13_829, AirportSizeCategory.Large),
            ("EHAM", 52.309, 4.764, 12_467, AirportSizeCategory.Large),
            ("EDDF", 50.033, 8.571, 13_123, AirportSizeCategory.Large),
            ("LEMD", 40.472, -3.561, 14_272, AirportSizeCategory.Large),
            ("LIRF", 41.800, 12.239, 12_795, AirportSizeCategory.Large),
            ("ENGM", 60.194, 11.100, 11_811, AirportSizeCategory.Medium),
            ("EKCH", 55.618, 12.656, 11_811, AirportSizeCategory.Medium),
            ("LPPT", 38.774, -9.134, 12_484, AirportSizeCategory.Medium),
            ("BIRK", 64.130, -21.941, 6_120, AirportSizeCategory.Medium),
            ("EKVG", 62.064, -7.277, 5_910, AirportSizeCategory.Small),
        ];

        foreach (var (icao, lat, lon, runway, size) in extras)
        {
            ctx.Db.Airports.Add(new Airport
            {
                Icao = icao,
                Name = icao,
                Municipality = icao,
                Country = "XX",
                Latitude = lat,
                Longitude = lon,
                SizeCategory = size,
                LongestRunwayFt = runway,
            });
        }

        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<Contract> SeedAcceptedContractAsync(RouteTestContext ctx, int legCount)
    {
        var chain = new[] { "EGGD", "EGPH", "EGPF", "EGSS" };
        var blockMinutes = Enumerable.Range(0, legCount).Select(i => 60 + i * 30).ToList();
        var shares = ContractPayCalculator.AllocateFeeShares(12_000m, blockMinutes);

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Kind = ContractKind.Ferry,
            Status = ContractStatus.Accepted,
            BoardBucket = 1,
            BoardSlot = 0,
            OperatorName = "Kestrel Aircraft Leasing",
            AircraftTypeDesignator = "C208",
            LoadDescription = "Positioning flight",
            Fee = 12_000m,
            TotalDistanceNm = 300 * legCount,
            TotalPlannedBlockMinutes = blockMinutes.Sum(),
            OfferedUtc = Now.AddDays(-1),
            DeadlineUtc = Now.AddDays(28),
            AcceptedUtc = Now,
            CreatedUtc = Now.AddDays(-1),
        };

        for (var i = 0; i < legCount; i++)
        {
            contract.Legs.Add(new ContractLeg
            {
                Id = Guid.NewGuid(),
                ContractId = contract.Id,
                Sequence = i + 1,
                DepartureIcao = chain[i % chain.Length],
                ArrivalIcao = chain[(i + 1) % chain.Length],
                DistanceNm = 300,
                PlannedBlockMinutes = blockMinutes[i],
                FeeShare = shares[i],
            });
        }

        ctx.Db.Contracts.Add(contract);

        if (!await ctx.Db.Pilots.AnyAsync(p => p.AirlineId == ctx.Airline.Id))
        {
            ctx.Db.Pilots.Add(new Pilot
            {
                Id = Guid.NewGuid(),
                AirlineId = ctx.Airline.Id,
                Name = "You",
                IsPlayer = true,
                CreatedUtc = Now,
            });
        }

        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return contract;
    }
}

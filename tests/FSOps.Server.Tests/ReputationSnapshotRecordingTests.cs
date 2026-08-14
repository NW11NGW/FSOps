using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Covers the daily <see cref="ReputationSnapshot"/> the economy clock writes.
///
/// Reputation cannot be honestly reconstructed after the fact (see that entity's own doc), so this
/// table is the only source of real reputation history the app will ever have - which makes two
/// properties non-negotiable, and both are pinned here: a day is written <b>exactly once</b>, and a
/// day already written is <b>never rewritten</b>, however many times the clock ticks. It follows
/// that the score kept for a day is the first one observed on it, not the last.
/// </summary>
public class ReputationSnapshotRecordingTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);

    private static EconomyClockService CreateService(RouteTestContext ctx, FakeClock clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        return new EconomyClockService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            EconomyConfigCatalog.Default(),
            clock,
            NullLogger<EconomyClockService>.Instance);
    }

    private static async Task SeedEconomyStateAsync(RouteTestContext ctx, DateTimeOffset lastProcessedUtc)
    {
        ctx.Db.EconomyStates.Add(new EconomyState
        {
            Id = Guid.NewGuid(),
            LastProcessedUtc = lastProcessedUtc,
            WorldSeed = 1,
            FuelPricePerKg = 0m,
        });
        await ctx.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task APassRecordsTodaysScore()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedEconomyStateAsync(ctx, Base);
        ctx.Airline.ReputationScore = 62.5;
        await ctx.Db.SaveChangesAsync();

        await CreateService(ctx, new FakeClock(Base.AddHours(2))).RunOnceAsync(CancellationToken.None);

        var snapshot = Assert.Single(await ctx.Db.ReputationSnapshots.AsNoTracking().ToListAsync());
        Assert.Equal(ctx.Airline.Id, snapshot.AirlineId);
        Assert.Equal("2026-01-15", snapshot.DateUtc);
        Assert.Equal(62.5, snapshot.Score);
    }

    [Fact]
    public async Task RepeatedPassesOnTheSameDay_WriteExactlyOneRow_AndNeverRewriteIt()
    {
        // The clock ticks every 60 seconds for the whole life of the process, so "the same day"
        // happens hundreds of times. The first observation of a day is as legitimate as any other,
        // and an insert-only series must not be rewritten - so a later tick must leave the recorded
        // score exactly as it was, not overwrite it with a fresher reading.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedEconomyStateAsync(ctx, Base);
        ctx.Airline.ReputationScore = 50;
        await ctx.Db.SaveChangesAsync();

        var clock = new FakeClock(Base);
        var service = CreateService(ctx, clock);
        await service.RunOnceAsync(CancellationToken.None);

        // Reputation moves during the day, and the clock ticks again.
        var airline = await ctx.Db.Airlines.SingleAsync();
        airline.ReputationScore = 71;
        await ctx.Db.SaveChangesAsync();

        clock.UtcNow += TimeSpan.FromHours(6);
        await service.RunOnceAsync(CancellationToken.None);
        clock.UtcNow += TimeSpan.FromHours(6);
        await service.RunOnceAsync(CancellationToken.None);

        var snapshots = await ctx.Db.ReputationSnapshots.AsNoTracking().ToListAsync();
        var snapshot = Assert.Single(snapshots);
        Assert.Equal(50, snapshot.Score);
    }

    [Fact]
    public async Task PassesOnDifferentDays_EachGetTheirOwnRow()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedEconomyStateAsync(ctx, Base);
        ctx.Airline.ReputationScore = 50;
        await ctx.Db.SaveChangesAsync();

        var clock = new FakeClock(Base);
        var service = CreateService(ctx, clock);
        await service.RunOnceAsync(CancellationToken.None);

        var airline = await ctx.Db.Airlines.SingleAsync();
        airline.ReputationScore = 58.5;
        await ctx.Db.SaveChangesAsync();

        clock.UtcNow += TimeSpan.FromDays(1);
        await service.RunOnceAsync(CancellationToken.None);

        var snapshots = (await ctx.Db.ReputationSnapshots.AsNoTracking().ToListAsync()).OrderBy(s => s.DateUtc).ToList();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(["2026-01-15", "2026-01-16"], snapshots.Select(s => s.DateUtc));
        Assert.Equal([50, 58.5], snapshots.Select(s => s.Score));
    }

    [Fact]
    public async Task DaysTheAppWasNeverOpen_GetNoRow_RatherThanACarriedForwardOne()
    {
        // The app being closed for a week is not evidence about reputation during that week, so no
        // row may be written for those days. A gap in the chart is the honest rendering; a flat line
        // would be a claim FSOps has no observation to support.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedEconomyStateAsync(ctx, Base);
        await ctx.Db.SaveChangesAsync();

        var clock = new FakeClock(Base);
        var service = CreateService(ctx, clock);
        await service.RunOnceAsync(CancellationToken.None);

        // The app is closed for a week, then reopened.
        clock.UtcNow += TimeSpan.FromDays(7);
        await service.RunOnceAsync(CancellationToken.None);

        var snapshots = (await ctx.Db.ReputationSnapshots.AsNoTracking().ToListAsync()).OrderBy(s => s.DateUtc).ToList();
        Assert.Equal(["2026-01-15", "2026-01-22"], snapshots.Select(s => s.DateUtc));
    }

    [Fact]
    public async Task ASoftDeletedAirlineIsNotSnapshotted()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedEconomyStateAsync(ctx, Base);
        ctx.Airline.DeletedUtc = Base.AddDays(-1);
        await ctx.Db.SaveChangesAsync();

        await CreateService(ctx, new FakeClock(Base)).RunOnceAsync(CancellationToken.None);

        Assert.Empty(await ctx.Db.ReputationSnapshots.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TheVeryFirstPassEverAlsoRecordsAScore()
    {
        // The first pass in an app's life creates the EconomyState row and returns early. Reputation
        // still has to be recorded from day one - otherwise a brand-new airline's very first day is
        // silently missing from its own history.
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Airline.ReputationScore = 50;
        await ctx.Db.SaveChangesAsync();

        await CreateService(ctx, new FakeClock(Base)).RunOnceAsync(CancellationToken.None);

        var snapshot = Assert.Single(await ctx.Db.ReputationSnapshots.AsNoTracking().ToListAsync());
        Assert.Equal("2026-01-15", snapshot.DateUtc);
    }
}

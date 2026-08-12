using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Data.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves AircraftTypeSeeder.ReconcileAsync fixes the bug reported against the user's own
/// instance: an old database created before 21 new types and the A320 price correction shipped
/// showed only 6 types and priced the A320 at the stale £101,000,000 figure. Reconciliation must
/// bring an existing database into line with the current catalogue on every startup, not just
/// seed once into an empty table - and it must do so without disturbing anything a player already
/// owns (see the class's own doc for why FleetAircraft.AircraftTypeId makes
/// matching-by-Id instead of by-IcaoType unsafe).
/// </summary>
public class AircraftTypeSeederReconciliationTests
{
    /// <summary>
    /// The exact "old database" shape reported live: only the original 6 narrowbody types, and
    /// the A320 still at the pre-rebalance £101,000,000 purchase price.
    /// </summary>
    private static readonly (Guid Id, string IcaoType, string Family, decimal PurchasePrice)[] OldCatalogue =
    {
        (Guid.NewGuid(), "A319", "A320", 90_000_000m),
        (Guid.NewGuid(), "A320", "A320", 101_000_000m),
        (Guid.NewGuid(), "A321", "A320", 110_000_000m),
        (Guid.NewGuid(), "B737", "B737", 88_000_000m),
        (Guid.NewGuid(), "B738", "B737", 99_000_000m),
        (Guid.NewGuid(), "B752", "B757", 120_000_000m),
    };

    private static async Task<FsOpsDbContext> CreateOldDatabaseAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connection).Options;
        var db = new FsOpsDbContext(options);
        await db.Database.EnsureCreatedAsync();

        foreach (var old in OldCatalogue)
        {
            db.AircraftTypes.Add(new AircraftType
            {
                Id = old.Id,
                IcaoType = old.IcaoType,
                Family = old.Family,
                Manufacturer = "Stale Manufacturer",
                Name = $"Stale {old.IcaoType}",
                PaxCapacity = 1,
                RangeNm = 1,
                CruiseTasKts = 1,
                FuelBurnKgPerHour = 1,
                MtowTonnes = 1,
                MinRunwayFt = 1,
                ServiceCeilingFt = 1,
                PurchasePrice = old.PurchasePrice,
                MonthlyLeaseRate = 1,
                MatchPatterns = "[]",
            });
        }

        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task ReconcileAsync_OnAnOldSixTypeDatabase_GrowsToTheFullCatalogueAndCorrectsTheA320Price()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var seedDb = await CreateOldDatabaseAsync(connection);
        var oldA320Id = OldCatalogue.Single(t => t.IcaoType == "A320").Id;

        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connection).Options;
        await using var reconcileDb = new FsOpsDbContext(options);

        var written = await AircraftTypeSeeder.ReconcileAsync(reconcileDb);

        Assert.True(written > 0, "Reconciling a stale database must actually write changes.");

        await using var verifyDb = new FsOpsDbContext(options);
        var allTypes = await verifyDb.AircraftTypes.ToListAsync();

        // The current catalogue - 27 types (6 original + 21 added since).
        Assert.Equal(27, allTypes.Count);

        var a320 = await verifyDb.AircraftTypes.SingleAsync(t => t.IcaoType == "A320");
        Assert.Equal(47_500_000m, a320.PurchasePrice);
        // Same primary key as before - never re-keyed.
        Assert.Equal(oldA320Id, a320.Id);
        Assert.Equal("Airbus A320", a320.Name);
        Assert.Equal("Airbus", a320.Manufacturer);

        // A newly-added type that did not exist in the old 6-type database.
        Assert.Contains(allTypes, t => t.IcaoType == "A388"); // A380-800
        Assert.Contains(allTypes, t => t.IcaoType == "B77W"); // 777-300ER
    }

    [Fact]
    public async Task ReconcileAsync_RunTwice_SecondRunWritesNothing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var seedDb = await CreateOldDatabaseAsync(connection);

        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connection).Options;

        await using (var firstRunDb = new FsOpsDbContext(options))
        {
            var firstWritten = await AircraftTypeSeeder.ReconcileAsync(firstRunDb);
            Assert.True(firstWritten > 0);
        }

        // Snapshot every field of every row before the second run.
        List<AircraftType> before;
        await using (var snapshotDb = new FsOpsDbContext(options))
        {
            before = await snapshotDb.AircraftTypes.AsNoTracking().OrderBy(t => t.IcaoType).ToListAsync();
        }

        await using (var secondRunDb = new FsOpsDbContext(options))
        {
            var secondWritten = await AircraftTypeSeeder.ReconcileAsync(secondRunDb);
            Assert.Equal(0, secondWritten);
        }

        List<AircraftType> after;
        await using (var verifyDb = new FsOpsDbContext(options))
        {
            after = await verifyDb.AircraftTypes.AsNoTracking().OrderBy(t => t.IcaoType).ToListAsync();
        }

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Id, after[i].Id);
            Assert.Equal(before[i].IcaoType, after[i].IcaoType);
            Assert.Equal(before[i].PurchasePrice, after[i].PurchasePrice);
            Assert.Equal(before[i].Name, after[i].Name);
            Assert.Equal(before[i].MatchPatterns, after[i].MatchPatterns);
        }
    }

    /// <summary>
    /// The test that matters most (per the bug report): an owned FleetAircraft pointing at the
    /// stale A320 row must still resolve to its type after reconciliation, with its own
    /// registration/hours/condition/ownership/location completely untouched - only the shared
    /// AircraftType row it points at gets its fields refreshed.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_AnOwnedFleetAircraftOnTheStaleA320_StillResolvesToItsTypeUntouched()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using var seedDb = await CreateOldDatabaseAsync(connection);
        var oldA320Id = OldCatalogue.Single(t => t.IcaoType == "A320").Id;

        var airlineId = Guid.NewGuid();
        var fleetAircraftId = Guid.NewGuid();
        var createdUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

        seedDb.FleetAircraft.Add(new FleetAircraft
        {
            Id = fleetAircraftId,
            AirlineId = airlineId,
            AircraftTypeId = oldA320Id,
            Registration = "G-USER",
            Ownership = AircraftOwnership.Owned,
            AirframeHours = 4321.5,
            HoursSinceACheck = 120.0,
            HoursSinceCCheck = 4321.5,
            ConditionPercent = 91.25,
            FuelOnBoardKg = 2750.0,
            LocationIcao = "EGGD",
            Status = FleetAircraftStatus.Active,
            ReservedForPlayer = true,
            CreatedUtc = createdUtc,
        });
        await seedDb.SaveChangesAsync();

        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connection).Options;
        await using (var reconcileDb = new FsOpsDbContext(options))
        {
            await AircraftTypeSeeder.ReconcileAsync(reconcileDb);
        }

        await using var verifyDb = new FsOpsDbContext(options);

        var fleetAircraft = await verifyDb.FleetAircraft.SingleAsync(f => f.Id == fleetAircraftId);
        // Every player-owned field is exactly as it was - reconciliation touched only the
        // shared AircraftType catalogue, never FleetAircraft.
        Assert.Equal("G-USER", fleetAircraft.Registration);
        Assert.Equal(AircraftOwnership.Owned, fleetAircraft.Ownership);
        Assert.Equal(4321.5, fleetAircraft.AirframeHours);
        Assert.Equal(120.0, fleetAircraft.HoursSinceACheck);
        Assert.Equal(4321.5, fleetAircraft.HoursSinceCCheck);
        Assert.Equal(91.25, fleetAircraft.ConditionPercent);
        Assert.Equal(2750.0, fleetAircraft.FuelOnBoardKg);
        Assert.Equal("EGGD", fleetAircraft.LocationIcao);
        Assert.Equal(FleetAircraftStatus.Active, fleetAircraft.Status);
        Assert.True(fleetAircraft.ReservedForPlayer);
        Assert.Equal(createdUtc, fleetAircraft.CreatedUtc);
        Assert.Equal(oldA320Id, fleetAircraft.AircraftTypeId);

        // The type it resolves to is now the corrected, current A320 definition - same row,
        // refreshed fields.
        var resolvedType = await verifyDb.AircraftTypes.SingleAsync(t => t.Id == fleetAircraft.AircraftTypeId);
        Assert.Equal("A320", resolvedType.IcaoType);
        Assert.Equal(47_500_000m, resolvedType.PurchasePrice);
        Assert.Equal("Airbus A320", resolvedType.Name);
    }

    /// <summary>
    /// A type present in the database but absent from the code catalogue (e.g. a retired
    /// experimental entry, or - as here - simulating a future removal) must be left alone, never
    /// deleted, since an owned FleetAircraft could still reference it.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_ATypeNotInCode_IsNeverDeleted()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connection).Options;

        var orphanId = Guid.NewGuid();
        await using (var seedDb = new FsOpsDbContext(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.AircraftTypes.Add(new AircraftType
            {
                Id = orphanId,
                IcaoType = "ZZZZ",
                Family = "Retired",
                Manufacturer = "Nobody",
                Name = "A type no longer in code",
                PaxCapacity = 1,
                RangeNm = 1,
                CruiseTasKts = 1,
                FuelBurnKgPerHour = 1,
                MtowTonnes = 1,
                MinRunwayFt = 1,
                ServiceCeilingFt = 1,
                PurchasePrice = 1_000_000m,
                MonthlyLeaseRate = 1,
                MatchPatterns = "[]",
            });
            await seedDb.SaveChangesAsync();
        }

        await using (var reconcileDb = new FsOpsDbContext(options))
        {
            await AircraftTypeSeeder.ReconcileAsync(reconcileDb);
        }

        await using var verifyDb = new FsOpsDbContext(options);
        var orphan = await verifyDb.AircraftTypes.SingleOrDefaultAsync(t => t.Id == orphanId);
        Assert.NotNull(orphan);
        Assert.Equal("ZZZZ", orphan!.IcaoType);
        Assert.Equal(1_000_000m, orphan.PurchasePrice);
    }
}

using FSOps.Core.SimAircraft;
using FSOps.Data;
using FSOps.Data.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// <b>The two aircraft catalogues are separate, and this is the test that keeps them that way.</b>
///
/// <para>The fleet catalogue (<see cref="AircraftTypeSeeder"/>) is what an airline may buy or lease.
/// It is airliners only, on purpose: the demand model, the seat-based fare model and the
/// weight-based airport fees all assume an airliner. The contract catalogue
/// (<see cref="ContractAircraftCatalogue"/>) reaches down to a Cessna 152, because a contract
/// supplies the aircraft and the player just flies it.</para>
///
/// <para>The failure this guards against is not two lists drifting apart - it is somebody
/// "tidying up" by pointing one at the other. Adding a Cessna 172 for contracts must never make a
/// 172 purchasable as an airline aircraft; a fleet full of light singles would quietly corrupt every
/// number the economy is built on, and nothing would look broken while it happened.</para>
///
/// <para>Overlap between the two lists is fine and expected - an ATR ferry is a perfectly
/// reasonable contract. Being the same list is not.</para>
/// </summary>
public class FleetAndContractCatalogueSeparationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FsOpsDbContext _db;

    public FleetAndContractCatalogueSeparationTests()
    {
        _connection = new SqliteConnection($"Data Source=file:{Guid.NewGuid():N}?Mode=Memory;Cache=Shared");
        _connection.Open();
        _db = new FsOpsDbContext(new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Nothing smaller than a regional airliner may be purchasable. The threshold is seats, because
    /// that is what the fare and demand models actually key off.
    /// </summary>
    [Fact]
    public async Task TheFleetCatalogueStaysAirlinersOnly()
    {
        await AircraftTypeSeeder.ReconcileAsync(_db);
        var fleet = await _db.AircraftTypes.ToListAsync();

        Assert.NotEmpty(fleet);
        Assert.All(fleet, t => Assert.True(
            t.PaxCapacity >= 40,
            $"{t.IcaoType} ({t.Name}) is purchasable with {t.PaxCapacity} seats. The fleet catalogue is " +
            "airliners only - the demand, fare and fee models all assume one. Contract-only aircraft " +
            "belong in ContractAircraftCatalogue."));
    }

    /// <summary>
    /// The concrete claim, named: the light aircraft that exist for contract flying are not in the
    /// hangar shop. If this fails, somebody has merged the lists.
    /// </summary>
    [Theory]
    [InlineData("C152")]
    [InlineData("C172")]
    [InlineData("SR22")]
    [InlineData("BE58")]
    [InlineData("TBM9")]
    [InlineData("C25C")]
    public async Task NoLightAircraftFromTheContractCatalogueIsPurchasable(string typeDesignator)
    {
        await AircraftTypeSeeder.ReconcileAsync(_db);

        Assert.NotNull(ContractAircraftCatalogue.Find(typeDesignator));
        Assert.False(await _db.AircraftTypes.AnyAsync(t => t.IcaoType == typeDesignator));
    }

    /// <summary>
    /// The other direction: the contract catalogue is genuinely broader, not a rename of the fleet
    /// one. MSFS 2024 has no general aviation in the fleet catalogue at all, which is why
    /// "transatlantic in a Cessna" could not previously be expressed.
    /// </summary>
    [Fact]
    public async Task TheContractCatalogueReachesWellBelowAnythingPurchasable()
    {
        await AircraftTypeSeeder.ReconcileAsync(_db);
        var smallestPurchasable = await _db.AircraftTypes.MinAsync(t => t.PaxCapacity);

        var smallestContract = ContractAircraftCatalogue.All.Min(a => a.Seats);

        Assert.True(smallestContract < smallestPurchasable);
        Assert.Contains(ContractAircraftCatalogue.All, a => a.Category == ContractAircraftCategory.LightSingle);
    }

    /// <summary>
    /// Overlap is fine - the same airliner can be both leasable and contractable - but the two must
    /// agree on what an ICAO designator means. A "B738" that is a 737 in one list and something else
    /// in the other would produce a contract nobody could satisfy.
    /// </summary>
    [Fact]
    public async Task WhereTheTwoCataloguesOverlapTheyAgreeOnWhatTheDesignatorMeans()
    {
        await AircraftTypeSeeder.ReconcileAsync(_db);
        var fleet = await _db.AircraftTypes.ToListAsync();

        foreach (var type in fleet)
        {
            var contract = ContractAircraftCatalogue.Find(type.IcaoType);
            if (contract is null)
            {
                continue;
            }

            Assert.Equal(type.Manufacturer, contract.Manufacturer);
            Assert.InRange(contract.Seats, (int)(type.PaxCapacity * 0.8), (int)(type.PaxCapacity * 1.25) + 1);
            Assert.InRange(contract.CruiseTasKts, (int)(type.CruiseTasKts * 0.85), (int)(type.CruiseTasKts * 1.15));
        }
    }
}

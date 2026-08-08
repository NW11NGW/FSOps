using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Data.Import;

/// <summary>
/// Seeds the starter aircraft catalogue: A320 family and B737 family. Matching only needs
/// to work at family level (a 737-800 flown on a 737-600 route is fine - see PLAN.md), so
/// every variant in a family shares the same detection patterns rather than trying to tell
/// specific sub-variants apart from a freeform TITLE string.
///
/// <para><b><c>MonthlyLeaseRate</c> on this entity is NOT read for pricing anywhere in the app -
/// it is a legacy/reference column only, kept so an old database's schema stays valid without a
/// migration.</b> Every lease the app actually charges (the founding lease in
/// <c>AirlineEndpoints.CreateAsync</c> and leasing an additional aircraft from
/// <c>FleetEndpoints.LeaseAsync</c>/<c>ListAircraftTypesAsync</c> alike) resolves the rate from
/// <c>economy-config.json</c>'s playstyle-aware <c>EconomyConfig.LeaseRates</c>, keyed by ICAO
/// type, via <c>EconomyConfig.LeaseRateFor</c>. This column cannot be the source of truth: it is
/// one row per aircraft type shared by every airline's database regardless of playstyle, so it
/// can never hold both the Casual and True-life figure for the same type at once, and because the
/// seeder only runs once (against an empty table), a database created before a rebalance would
/// silently keep the old number forever while a fresh one got the new one - exactly the
/// same-app-prices-differently bug that made this column stop being authoritative. The values
/// below are left in place purely as a historical/informational default (roughly what the figure
/// was at seed time); do not read them for anything that affects money.</para>
///
/// <para><b>PurchasePrice is a realistic transaction value, not a manufacturer list price</b>, and
/// - unlike MonthlyLeaseRate above - IS still the value actually charged (see FleetEndpoints.BuyAsync
/// and EconomyConfig.UsedAircraft, which derives the used-aircraft discount from it). Airlines
/// never pay list: real-world launch customers routinely negotiate 45-55% off, so each figure here
/// was originally set from a real-world lease-rate factor of about 0.8% of the aircraft's value per
/// month (a standard operating-lease convention) applied to each type's ORIGINAL realistic lease
/// rate - this kept purchase price and lease rate mutually consistent for the same airframe at
/// seed time, and landed every type at roughly half its old list-price figure. See docs/PLAN.md
/// "Economic balance" for why the old list prices made buying unreachable.</para>
///
/// <para><b>Exception: the A320 and B738 rows' <c>PurchasePrice</c> is deliberately left at its
/// realistic, original-lease-rate-implied value</b> even though those two rows' (inert)
/// <c>MonthlyLeaseRate</c> was cut to a deliberate game-balance figure (Casual: see
/// <c>EconomyConfig.LeaseRates</c>) - buying outright stays a genuine later milestone funded by
/// retained profit or a loan, per docs/PLAN.md "The progression loop", not something the
/// game-balanced lease rate should also cheapen. PurchasePrice is shared across both playstyles
/// (see <c>EconomyConfig.UsedAircraft</c>'s class doc) since real transaction values don't depend
/// on how realistically the buyer chose to be billed elsewhere - there is no per-playstyle
/// divergence risk here the way there was for lease rates.</para>
/// </summary>
public static class AircraftTypeSeeder
{
    private const string A320FamilyPatterns = "[\"A31[89]\",\"A32[01]\",\"A20N\",\"A19N\",\"A21N\"]";
    private const string B737FamilyPatterns = "[\"B73[6-9]\",\"737\",\"B738\"]";

    public static async Task SeedIfNeededAsync(FsOpsDbContext db, CancellationToken ct = default)
    {
        if (await db.AircraftTypes.AnyAsync(ct))
        {
            return;
        }

        var types = new List<AircraftType>
        {
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A319", Family = "A320", Manufacturer = "Airbus",
                Name = "Airbus A319", PaxCapacity = 140, RangeNm = 3700, CruiseTasKts = 447,
                FuelBurnKgPerHour = 2400, MtowTonnes = 75.5, MinRunwayFt = 5000, ServiceCeilingFt = 41000, PurchasePrice = 43_750_000m,
                MonthlyLeaseRate = 350_000m, MatchPatterns = A320FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A320", Family = "A320", Manufacturer = "Airbus",
                Name = "Airbus A320", PaxCapacity = 180, RangeNm = 3300, CruiseTasKts = 447,
                FuelBurnKgPerHour = 2500, MtowTonnes = 78.0, MinRunwayFt = 5500, ServiceCeilingFt = 39000, PurchasePrice = 47_500_000m,
                // Inert - see the class doc. Actual Casual/True-life rates live in
                // economy-config.json's LeaseRates["A320"] (30,000 / 380,000).
                MonthlyLeaseRate = 30_000m, MatchPatterns = A320FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A321", Family = "A320", Manufacturer = "Airbus",
                Name = "Airbus A321", PaxCapacity = 220, RangeNm = 3200, CruiseTasKts = 447,
                FuelBurnKgPerHour = 2700, MtowTonnes = 93.5, MinRunwayFt = 6000, ServiceCeilingFt = 39000, PurchasePrice = 52_500_000m,
                MonthlyLeaseRate = 420_000m, MatchPatterns = A320FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B737", Family = "B737", Manufacturer = "Boeing",
                Name = "Boeing 737-700", PaxCapacity = 140, RangeNm = 3850, CruiseTasKts = 453,
                FuelBurnKgPerHour = 2350, MtowTonnes = 70.1, MinRunwayFt = 5000, ServiceCeilingFt = 41000, PurchasePrice = 42_500_000m,
                MonthlyLeaseRate = 340_000m, MatchPatterns = B737FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B738", Family = "B737", Manufacturer = "Boeing",
                Name = "Boeing 737-800", PaxCapacity = 189, RangeNm = 3115, CruiseTasKts = 453,
                FuelBurnKgPerHour = 2600, MtowTonnes = 79.0, MinRunwayFt = 5500, ServiceCeilingFt = 41000, PurchasePrice = 48_750_000m,
                // Inert - see the class doc. Actual Casual/True-life rates live in
                // economy-config.json's LeaseRates["B738"] (30,000 / 390,000).
                MonthlyLeaseRate = 30_000m, MatchPatterns = B737FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B739", Family = "B737", Manufacturer = "Boeing",
                Name = "Boeing 737-900", PaxCapacity = 220, RangeNm = 3200, CruiseTasKts = 453,
                FuelBurnKgPerHour = 2750, MtowTonnes = 85.1, MinRunwayFt = 6000, ServiceCeilingFt = 41000, PurchasePrice = 51_250_000m,
                MonthlyLeaseRate = 410_000m, MatchPatterns = B737FamilyPatterns,
            },
        };

        db.AircraftTypes.AddRange(types);
        await db.SaveChangesAsync(ct);
    }
}

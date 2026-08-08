using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Data.Import;

/// <summary>
/// Seeds the starter aircraft catalogue: A320 family and B737 family. Matching only needs
/// to work at family level (a 737-800 flown on a 737-600 route is fine - see PLAN.md), so
/// every variant in a family shares the same detection patterns rather than trying to tell
/// specific sub-variants apart from a freeform TITLE string.
///
/// <para><b>PurchasePrice is a realistic transaction value, not a manufacturer list price.</b>
/// Airlines never pay list: real-world launch customers routinely negotiate 45-55% off, so each
/// figure here is set from <c>MonthlyLeaseRate</c> using a real-world lease-rate factor of about
/// 0.8% of the aircraft's value per month (a standard operating-lease convention) rather than
/// picked independently - this keeps purchase price and lease rate mutually consistent for the
/// same airframe, and lands every type at roughly half its old list-price figure. See
/// docs/PLAN.md "Economic balance" for why the old list prices made buying unreachable.</para>
///
/// <para><b>Exception: the A320 and B738 entries' <c>MonthlyLeaseRate</c> is a deliberate
/// game-balance number, not a real one - the 0.8%-of-value convention above no longer applies to
/// those two rows.</b> They are the only two aircraft offered as the starter type at airline
/// creation (see <c>AirlineEndpoints.CreateAsync</c>'s <c>starterIcaoType</c> mapping), and the
/// progression loop the user confirmed 2026-08-08 (docs/PLAN.md "The progression loop") requires
/// one aircraft flown casually (roughly one leg a day) to be genuinely profitable and a second
/// aircraft's lease deposit affordable within about 7-10 flights. That is arithmetically
/// impossible at anything close to a real A320/737-800 lease (their family siblings below are
/// still priced realistically - only the two starter rows changed): 30,000 is roughly 8% of what
/// this airframe actually leases for. <c>PurchasePrice</c> on these two rows is deliberately left
/// untouched at its realistic, lease-rate-implied value (NOT recomputed from the new lease rate) -
/// buying outright stays a genuine later milestone funded by retained profit or a loan, per the
/// user's explicit decision. Do NOT "fix" this rate back toward realism - that reopens exactly the
/// unplayable-grind problem the decision was made to close. See docs/PLAN.md "Status after the
/// progression-loop rebalance" for the full numbers and the accepted trade-off.</para>
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
                // Game-balance lease, not realistic (real rate ~380,000) - see the class doc.
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
                // Game-balance lease, not realistic (real rate ~390,000) - see the class doc.
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

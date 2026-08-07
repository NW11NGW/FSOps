using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Data.Import;

/// <summary>
/// Seeds the starter aircraft catalogue: A320 family and B737 family. Matching only needs
/// to work at family level (a 737-800 flown on a 737-600 route is fine - see PLAN.md), so
/// every variant in a family shares the same detection patterns rather than trying to tell
/// specific sub-variants apart from a freeform TITLE string.
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
                FuelBurnKgPerHour = 2400, MinRunwayFt = 5000, ServiceCeilingFt = 41000, PurchasePrice = 92_000_000m,
                MonthlyLeaseRate = 350_000m, MatchPatterns = A320FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A320", Family = "A320", Manufacturer = "Airbus",
                Name = "Airbus A320", PaxCapacity = 180, RangeNm = 3300, CruiseTasKts = 447,
                FuelBurnKgPerHour = 2500, MinRunwayFt = 5500, ServiceCeilingFt = 39000, PurchasePrice = 101_000_000m,
                MonthlyLeaseRate = 380_000m, MatchPatterns = A320FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A321", Family = "A320", Manufacturer = "Airbus",
                Name = "Airbus A321", PaxCapacity = 220, RangeNm = 3200, CruiseTasKts = 447,
                FuelBurnKgPerHour = 2700, MinRunwayFt = 6000, ServiceCeilingFt = 39000, PurchasePrice = 118_000_000m,
                MonthlyLeaseRate = 420_000m, MatchPatterns = A320FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B737", Family = "B737", Manufacturer = "Boeing",
                Name = "Boeing 737-700", PaxCapacity = 140, RangeNm = 3850, CruiseTasKts = 453,
                FuelBurnKgPerHour = 2350, MinRunwayFt = 5000, ServiceCeilingFt = 41000, PurchasePrice = 89_000_000m,
                MonthlyLeaseRate = 340_000m, MatchPatterns = B737FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B738", Family = "B737", Manufacturer = "Boeing",
                Name = "Boeing 737-800", PaxCapacity = 189, RangeNm = 3115, CruiseTasKts = 453,
                FuelBurnKgPerHour = 2600, MinRunwayFt = 5500, ServiceCeilingFt = 41000, PurchasePrice = 106_000_000m,
                MonthlyLeaseRate = 390_000m, MatchPatterns = B737FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B739", Family = "B737", Manufacturer = "Boeing",
                Name = "Boeing 737-900", PaxCapacity = 220, RangeNm = 3200, CruiseTasKts = 453,
                FuelBurnKgPerHour = 2750, MinRunwayFt = 6000, ServiceCeilingFt = 41000, PurchasePrice = 110_000_000m,
                MonthlyLeaseRate = 410_000m, MatchPatterns = B737FamilyPatterns,
            },
        };

        db.AircraftTypes.AddRange(types);
        await db.SaveChangesAsync(ct);
    }
}

using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Data.Import;

/// <summary>
/// Reconciles the purchasable/leasable aircraft catalogue against the definitions below - see
/// docs/PLAN.md "The aircraft catalogue must cover what people actually fly". Runs on every
/// startup, not just once: an existing database must pick up newly added types and corrected
/// catalogue figures (name, spec, purchase price, match patterns, ...) the same way a fresh
/// database does, rather than being frozen at whatever the catalogue looked like when its
/// AircraftTypes table was first populated.
///
/// <para>Matching is by <see cref="AircraftType.IcaoType"/>, never by <see cref="AircraftType.Id"/>
/// - existing rows keep their primary key, because <c>FleetAircraft.AircraftTypeId</c> points at it.
/// A type is never deleted, even if it disappears from the definitions below: an owned
/// <c>FleetAircraft</c> may still reference it, and orphaning someone's fleet is far worse than a
/// stale catalogue row lingering. Reconciliation only ever inserts a missing type or updates an
/// existing one's reference fields - it never touches anything owned by a player (FleetAircraft,
/// Lease, Loan, LedgerTransaction, ...).</para>
///
/// <para>Matching only needs to work at family level (a 737-800 flown on a 737-600 route is fine -
/// see PLAN.md), so every variant within a family shares the same detection patterns rather than
/// trying to tell specific sub-variants apart from a freeform TITLE string.</para>
///
/// <para><b><c>MonthlyLeaseRate</c> on this entity is NOT read for pricing anywhere in the app -
/// it is a legacy/reference column only, kept so an old database's schema stays valid without a
/// migration.</b> Every lease the app actually charges (the founding lease in
/// <c>AirlineEndpoints.CreateAsync</c> and leasing an additional aircraft from
/// <c>FleetEndpoints.LeaseAsync</c>/<c>ListAircraftTypesAsync</c> alike) resolves the rate from
/// <c>economy-config.json</c>'s playstyle-aware <c>EconomyConfig.LeaseRates</c>, keyed by ICAO
/// type, via <c>EconomyConfig.LeaseRateFor</c> - every type here has an entry in BOTH the
/// <c>casual</c> and <c>trueLife</c> blocks (see docs/PLAN.md "Lease rates are per playstyle and
/// required"), which is what LeaseRateFor's loud throw on a missing type guards. The values below
/// are left in place purely as a historical/informational default (the real-world rate at seed
/// time); do not read them for anything that affects money.</para>
///
/// <para><b>PurchasePrice is a realistic transaction value, shared across both playstyles</b>, and
/// - unlike MonthlyLeaseRate above - IS still the value actually charged, via
/// <c>EconomyConfig.PurchasePriceFor</c> (never read directly - see that method's own doc). True-life
/// charges it unmodified; Casual applies a small, catalogue-wide multiplier
/// (<c>EconomyConfig.PurchasePriceMultiplier</c>) so a used example of even the starter type is
/// affordable after roughly ten sectors' profit - see docs/PLAN.md "Casual pricing must scale the
/// WHOLE catalogue". Airlines never pay list: real-world launch customers routinely negotiate
/// 45-55% off, so each figure here is a realistic negotiated transaction value rather than a
/// manufacturer list price.</para>
/// </summary>
public static class AircraftTypeSeeder
{
    private const string A320FamilyPatterns = """["A31[89]","A32[01]","A20N","A19N","A21N","A32NX"]""";
    private const string B737FamilyPatterns = """["B73[6-9]","737","B738","B37M","B38M","B39M"]""";
    private const string B757Patterns = """["B75[23]","757"]""";
    private const string A330Patterns = """["A33[023]","A338","A339","A330"]""";
    private const string A350Patterns = """["A35[9K]","A350"]""";
    private const string A380Patterns = """["A388","A380"]""";
    private const string B767Patterns = """["B76[234]","767"]""";
    private const string B777Patterns = """["B77[0-9]","B77[LW]","777"]""";
    private const string B787Patterns = """["B78[89X]","787","Dreamliner"]""";
    private const string B747Patterns = """["B74[1-8]","747"]""";
    private const string EJetPatterns = """["E-?170","E-?175","E-?190","E-?195","E75L","E75S","E290","E295"]""";
    private const string CrjPatterns = """["CRJ.{0,3}700","CRJ.{0,3}900","CRJ7","CRJ9"]""";
    private const string AtrPatterns = """["ATR.{0,3}42","ATR.{0,3}72","AT42","AT43","AT45","AT46","AT72","AT73","AT75","AT76"]""";
    private const string Dash8Patterns = """["Q400","DHC-?8","Dash.{0,2}8","DH8D"]""";

    /// <summary>
    /// Brings the AircraftTypes table into line with <see cref="BuildCatalogue"/>: inserts any
    /// missing <see cref="AircraftType.IcaoType"/>, updates the catalogue/spec fields of existing
    /// rows to whatever the code now says, and never deletes or re-keys anything. Idempotent - a
    /// second run against an already-reconciled database changes nothing, since assigning a field
    /// its current value produces no tracked change for EF to write. Returns the number of rows
    /// EF actually inserted or updated, purely so tests can assert idempotency (zero on a
    /// second run) without duplicating this method's field list.
    /// </summary>
    public static async Task<int> ReconcileAsync(FsOpsDbContext db, CancellationToken ct = default)
    {
        var definitions = BuildCatalogue();
        var existingByIcao = await db.AircraftTypes.ToDictionaryAsync(t => t.IcaoType, ct);

        foreach (var definition in definitions)
        {
            if (existingByIcao.TryGetValue(definition.IcaoType, out var existing))
            {
                // Update reference/catalogue fields only. Id and IcaoType are left untouched -
                // Id is the primary key FleetAircraft.AircraftTypeId points at.
                existing.Family = definition.Family;
                existing.Manufacturer = definition.Manufacturer;
                existing.Name = definition.Name;
                existing.PaxCapacity = definition.PaxCapacity;
                existing.RangeNm = definition.RangeNm;
                existing.CruiseTasKts = definition.CruiseTasKts;
                existing.FuelBurnKgPerHour = definition.FuelBurnKgPerHour;
                existing.MtowTonnes = definition.MtowTonnes;
                existing.MinRunwayFt = definition.MinRunwayFt;
                existing.ServiceCeilingFt = definition.ServiceCeilingFt;
                existing.PurchasePrice = definition.PurchasePrice;
                existing.MonthlyLeaseRate = definition.MonthlyLeaseRate;
                existing.MatchPatterns = definition.MatchPatterns;
            }
            else
            {
                db.AircraftTypes.Add(definition);
            }
        }

        // Anything left in existingByIcao whose IcaoType has no matching definition is a type the
        // catalogue no longer defines - left alone deliberately (see class doc): never delete it,
        // an owned FleetAircraft may still reference it.
        return await db.SaveChangesAsync(ct);
    }

    private static List<AircraftType> BuildCatalogue() =>
        new()
        {
            // ---- Narrowbody: A320 family ----
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
                Id = Guid.NewGuid(), IcaoType = "A20N", Family = "A320", Manufacturer = "Airbus",
                Name = "Airbus A320neo", PaxCapacity = 180, RangeNm = 3500, CruiseTasKts = 450,
                FuelBurnKgPerHour = 2150, MtowTonnes = 79.0, MinRunwayFt = 5900, ServiceCeilingFt = 39000, PurchasePrice = 52_000_000m,
                MonthlyLeaseRate = 410_000m, MatchPatterns = A320FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A21N", Family = "A320", Manufacturer = "Airbus",
                Name = "Airbus A321neo", PaxCapacity = 220, RangeNm = 4000, CruiseTasKts = 450,
                FuelBurnKgPerHour = 2350, MtowTonnes = 97.0, MinRunwayFt = 6500, ServiceCeilingFt = 39000, PurchasePrice = 57_000_000m,
                MonthlyLeaseRate = 450_000m, MatchPatterns = A320FamilyPatterns,
            },

            // ---- Narrowbody: B737 family (+ MAX) and B757 ----
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
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B38M", Family = "B737", Manufacturer = "Boeing",
                Name = "Boeing 737 MAX 8", PaxCapacity = 178, RangeNm = 3550, CruiseTasKts = 453,
                FuelBurnKgPerHour = 2200, MtowTonnes = 82.6, MinRunwayFt = 6000, ServiceCeilingFt = 41000, PurchasePrice = 55_000_000m,
                MonthlyLeaseRate = 430_000m, MatchPatterns = B737FamilyPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B752", Family = "B757", Manufacturer = "Boeing",
                Name = "Boeing 757-200", PaxCapacity = 200, RangeNm = 3915, CruiseTasKts = 459,
                FuelBurnKgPerHour = 3100, MtowTonnes = 115.7, MinRunwayFt = 7000, ServiceCeilingFt = 42000, PurchasePrice = 60_000_000m,
                MonthlyLeaseRate = 460_000m, MatchPatterns = B757Patterns,
            },

            // ---- Widebody ----
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A332", Family = "A330", Manufacturer = "Airbus",
                Name = "Airbus A330-200", PaxCapacity = 260, RangeNm = 6100, CruiseTasKts = 470,
                FuelBurnKgPerHour = 5800, MtowTonnes = 230.0, MinRunwayFt = 8000, ServiceCeilingFt = 41000, PurchasePrice = 95_000_000m,
                MonthlyLeaseRate = 750_000m, MatchPatterns = A330Patterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A333", Family = "A330", Manufacturer = "Airbus",
                Name = "Airbus A330-300", PaxCapacity = 300, RangeNm = 6350, CruiseTasKts = 470,
                FuelBurnKgPerHour = 6200, MtowTonnes = 242.0, MinRunwayFt = 8200, ServiceCeilingFt = 41000, PurchasePrice = 105_000_000m,
                MonthlyLeaseRate = 820_000m, MatchPatterns = A330Patterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A359", Family = "A350", Manufacturer = "Airbus",
                Name = "Airbus A350-900", PaxCapacity = 325, RangeNm = 8100, CruiseTasKts = 488,
                FuelBurnKgPerHour = 6500, MtowTonnes = 280.0, MinRunwayFt = 9000, ServiceCeilingFt = 43000, PurchasePrice = 160_000_000m,
                MonthlyLeaseRate = 1_150_000m, MatchPatterns = A350Patterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "A388", Family = "A380", Manufacturer = "Airbus",
                Name = "Airbus A380-800", PaxCapacity = 525, RangeNm = 8000, CruiseTasKts = 488,
                FuelBurnKgPerHour = 11500, MtowTonnes = 575.0, MinRunwayFt = 10000, ServiceCeilingFt = 43000, PurchasePrice = 250_000_000m,
                MonthlyLeaseRate = 1_800_000m, MatchPatterns = A380Patterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B763", Family = "B767", Manufacturer = "Boeing",
                Name = "Boeing 767-300ER", PaxCapacity = 245, RangeNm = 5980, CruiseTasKts = 459,
                FuelBurnKgPerHour = 5600, MtowTonnes = 186.9, MinRunwayFt = 8000, ServiceCeilingFt = 43100, PurchasePrice = 80_000_000m,
                MonthlyLeaseRate = 650_000m, MatchPatterns = B767Patterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B77W", Family = "B777", Manufacturer = "Boeing",
                Name = "Boeing 777-300ER", PaxCapacity = 396, RangeNm = 7370, CruiseTasKts = 490,
                FuelBurnKgPerHour = 8200, MtowTonnes = 351.5, MinRunwayFt = 10000, ServiceCeilingFt = 43100, PurchasePrice = 200_000_000m,
                MonthlyLeaseRate = 1_450_000m, MatchPatterns = B777Patterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B789", Family = "B787", Manufacturer = "Boeing",
                Name = "Boeing 787-9 Dreamliner", PaxCapacity = 296, RangeNm = 7635, CruiseTasKts = 488,
                FuelBurnKgPerHour = 5400, MtowTonnes = 254.0, MinRunwayFt = 9000, ServiceCeilingFt = 43000, PurchasePrice = 145_000_000m,
                MonthlyLeaseRate = 1_050_000m, MatchPatterns = B787Patterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "B748", Family = "B747", Manufacturer = "Boeing",
                Name = "Boeing 747-8", PaxCapacity = 410, RangeNm = 8000, CruiseTasKts = 490,
                FuelBurnKgPerHour = 11000, MtowTonnes = 447.7, MinRunwayFt = 10000, ServiceCeilingFt = 43100, PurchasePrice = 180_000_000m,
                MonthlyLeaseRate = 1_300_000m, MatchPatterns = B747Patterns,
            },

            // ---- Regional ----
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "E170", Family = "E-Jet", Manufacturer = "Embraer",
                Name = "Embraer E170", PaxCapacity = 76, RangeNm = 2150, CruiseTasKts = 430,
                FuelBurnKgPerHour = 1400, MtowTonnes = 37.2, MinRunwayFt = 4700, ServiceCeilingFt = 41000, PurchasePrice = 24_000_000m,
                MonthlyLeaseRate = 185_000m, MatchPatterns = EJetPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "E175", Family = "E-Jet", Manufacturer = "Embraer",
                Name = "Embraer E175", PaxCapacity = 84, RangeNm = 2200, CruiseTasKts = 430,
                FuelBurnKgPerHour = 1450, MtowTonnes = 40.4, MinRunwayFt = 4900, ServiceCeilingFt = 41000, PurchasePrice = 27_000_000m,
                MonthlyLeaseRate = 205_000m, MatchPatterns = EJetPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "E190", Family = "E-Jet", Manufacturer = "Embraer",
                Name = "Embraer E190", PaxCapacity = 100, RangeNm = 2450, CruiseTasKts = 430,
                FuelBurnKgPerHour = 1650, MtowTonnes = 51.8, MinRunwayFt = 5200, ServiceCeilingFt = 41000, PurchasePrice = 33_000_000m,
                MonthlyLeaseRate = 240_000m, MatchPatterns = EJetPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "E195", Family = "E-Jet", Manufacturer = "Embraer",
                Name = "Embraer E195", PaxCapacity = 124, RangeNm = 2450, CruiseTasKts = 430,
                FuelBurnKgPerHour = 1750, MtowTonnes = 52.3, MinRunwayFt = 5580, ServiceCeilingFt = 41000, PurchasePrice = 36_000_000m,
                MonthlyLeaseRate = 260_000m, MatchPatterns = EJetPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "CRJ7", Family = "CRJ", Manufacturer = "Bombardier",
                Name = "Bombardier CRJ-700", PaxCapacity = 70, RangeNm = 1650, CruiseTasKts = 430,
                FuelBurnKgPerHour = 1250, MtowTonnes = 34.0, MinRunwayFt = 4600, ServiceCeilingFt = 41000, PurchasePrice = 20_000_000m,
                MonthlyLeaseRate = 160_000m, MatchPatterns = CrjPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "CRJ9", Family = "CRJ", Manufacturer = "Bombardier",
                Name = "Bombardier CRJ-900", PaxCapacity = 86, RangeNm = 1550, CruiseTasKts = 430,
                FuelBurnKgPerHour = 1400, MtowTonnes = 36.5, MinRunwayFt = 5000, ServiceCeilingFt = 41000, PurchasePrice = 23_000_000m,
                MonthlyLeaseRate = 175_000m, MatchPatterns = CrjPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "AT42", Family = "ATR", Manufacturer = "ATR",
                Name = "ATR 42-600", PaxCapacity = 48, RangeNm = 716, CruiseTasKts = 300,
                FuelBurnKgPerHour = 550, MtowTonnes = 18.6, MinRunwayFt = 3500, ServiceCeilingFt = 25000, PurchasePrice = 13_000_000m,
                MonthlyLeaseRate = 95_000m, MatchPatterns = AtrPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "AT72", Family = "ATR", Manufacturer = "ATR",
                Name = "ATR 72-600", PaxCapacity = 70, RangeNm = 825, CruiseTasKts = 300,
                FuelBurnKgPerHour = 650, MtowTonnes = 23.0, MinRunwayFt = 4300, ServiceCeilingFt = 25000, PurchasePrice = 16_000_000m,
                MonthlyLeaseRate = 115_000m, MatchPatterns = AtrPatterns,
            },
            new()
            {
                Id = Guid.NewGuid(), IcaoType = "DH8D", Family = "Dash8", Manufacturer = "De Havilland Canada",
                Name = "Dash 8 Q400", PaxCapacity = 78, RangeNm = 1100, CruiseTasKts = 360,
                FuelBurnKgPerHour = 900, MtowTonnes = 29.3, MinRunwayFt = 4500, ServiceCeilingFt = 27000, PurchasePrice = 18_000_000m,
                MonthlyLeaseRate = 130_000m, MatchPatterns = Dash8Patterns,
            },
        };
}

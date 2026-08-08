using System.Text.Json;
using System.Text.Json.Serialization;
using FSOps.Core.Entities;

namespace FSOps.Core.Economy;

/// <summary>
/// Every tuning constant the economy engine uses, loaded from economy-config.json. Nothing
/// numeric is hardcoded inside the Economy classes themselves - it all flows through here, so
/// balance can be retuned without a code change. Core stays pure: this type has no idea the
/// file exists on disk. FromJson takes the file's text as a plain string; whoever owns the
/// filesystem (the Server project) reads the bytes and hands them in.
/// </summary>
public sealed class EconomyConfig
{
    /// <summary>Absolute ceiling on load factor - no route can ever sell more than this
    /// fraction of its seats, no matter how cheap the fare. ~92% mirrors a realistic maximum
    /// for a scheduled service (a handful of seats are always empty, late cancellations, etc).</summary>
    public double MaxLoadFactor { get; init; } = 0.92;

    /// <summary>
    /// Integrity guard against the "captive tiny market" exploit: without a bound, a route with
    /// a very small passenger pool relative to seats lets FareDemandModel's flat/price-insensitive
    /// region stretch to an enormous fare multiple (mathematically, the crossover fare grows
    /// without limit as the market shrinks toward zero), so a cheater could charge a handful of
    /// captive passengers an absurd fare and still turn a profit. Beyond
    /// <c>referenceFare x CaptiveFareCeilingMultiple</c>, the market pool itself starts eroding
    /// (see FareDemandModel.EffectiveMarketCap) at <see cref="PostCaptiveElasticity"/>, so revenue
    /// is bounded for every route regardless of how thin its demand is. 1.5 doubles as the upper
    /// end of the "sane multiple of reference" range the exploit tests already require, so this
    /// never touches a normally-sized market - the crossover for every strategy profile already
    /// lands below it.
    /// </summary>
    public double CaptiveFareCeilingMultiple { get; init; } = 1.5;

    /// <summary>How steeply the market pool eaten away beyond the captive ceiling - deliberately
    /// steeper than any strategy's own Elasticity, since a fare this far past the ceiling is no
    /// longer "pricing," it is gouging a captive audience, and even a captive audience has a
    /// breaking point.</summary>
    public double PostCaptiveElasticity { get; init; } = 2.5;

    public ReferenceFareConfig ReferenceFare { get; init; } = new();

    public DemandConfig Demand { get; init; } = new();

    public FuelConfig Fuel { get; init; } = new();

    public CostConfig Costs { get; init; } = new();

    /// <summary>Figures used once, at airline creation - starting capital, the lease deposit and
    /// the founding pilot's salary. Kept here rather than as C# constants (see the old
    /// AirlineCreationDefaults) so balance can be retuned without a code change, same as every
    /// other number in this file.</summary>
    public AirlineStartupConfig AirlineStartup { get; init; } = new();

    /// <summary>Recurring monthly costs that are not tied to any single flight. Currently just
    /// insurance; lease and salary are already recurring via <see cref="Lease.MonthlyRate"/> and
    /// <see cref="Pilot.MonthlySalary"/> and do not need duplicating here.</summary>
    public FleetFinanceConfig FleetFinance { get; init; } = new();

    public IReadOnlyList<StrategyProfileConfig> StrategyProfiles { get; init; } = Array.Empty<StrategyProfileConfig>();

    [JsonIgnore]
    private Dictionary<AirlineStrategyProfile, StrategyProfileConfig>? _strategyLookup;

    /// <summary>Looks up the tuning constants for one strategy profile. Throws if the config is
    /// missing a profile - a missing profile is a config authoring bug, not something to paper
    /// over with a silent default that would make every low-cost route behave like a premium one.</summary>
    public StrategyProfileConfig GetStrategy(AirlineStrategyProfile profile)
    {
        _strategyLookup ??= StrategyProfiles.ToDictionary(s => s.Profile);

        if (!_strategyLookup.TryGetValue(profile, out var config))
        {
            throw new InvalidOperationException($"economy-config.json has no strategy profile entry for '{profile}'.");
        }

        return config;
    }

    /// <summary>Throws if the config is internally inconsistent (e.g. a strategy's baseline
    /// load factor above the hard ceiling). Called after loading from JSON and from Default() so
    /// both paths are validated the same way.</summary>
    public void Validate()
    {
        if (MaxLoadFactor is <= 0 or > 1)
        {
            throw new InvalidOperationException($"MaxLoadFactor must be in (0,1], was {MaxLoadFactor}.");
        }

        if (CaptiveFareCeilingMultiple <= 1.0)
        {
            throw new InvalidOperationException($"CaptiveFareCeilingMultiple must be above 1.0, was {CaptiveFareCeilingMultiple}.");
        }

        if (PostCaptiveElasticity <= 0)
        {
            throw new InvalidOperationException($"PostCaptiveElasticity must be positive, was {PostCaptiveElasticity}.");
        }

        if (StrategyProfiles.Count == 0)
        {
            throw new InvalidOperationException("economy-config.json defines no strategy profiles.");
        }

        foreach (var strategy in StrategyProfiles)
        {
            if (strategy.Elasticity < 1.0)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategy.Profile}' has elasticity {strategy.Elasticity} below 1.0 - " +
                    "an inelastic profile would let revenue climb without limit as fare rises, defeating the anti-exploit rule.");
            }

            if (strategy.BaselineLoadFactor <= 0 || strategy.BaselineLoadFactor > MaxLoadFactor)
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategy.Profile}' has BaselineLoadFactor {strategy.BaselineLoadFactor} " +
                    $"outside (0, MaxLoadFactor={MaxLoadFactor}].");
            }

            if (strategy.ReferenceFareMultiplier <= 0)
            {
                throw new InvalidOperationException($"Strategy '{strategy.Profile}' has a non-positive ReferenceFareMultiplier.");
            }
        }

        if (AirlineStartup.StartingCapital <= 0)
        {
            throw new InvalidOperationException($"AirlineStartup.StartingCapital must be positive, was {AirlineStartup.StartingCapital}.");
        }

        if (AirlineStartup.LeaseDepositMonths <= 0)
        {
            throw new InvalidOperationException($"AirlineStartup.LeaseDepositMonths must be positive, was {AirlineStartup.LeaseDepositMonths}.");
        }

        if (AirlineStartup.StartingPilotMonthlySalary <= 0)
        {
            throw new InvalidOperationException(
                $"AirlineStartup.StartingPilotMonthlySalary must be positive, was {AirlineStartup.StartingPilotMonthlySalary}.");
        }

        if (FleetFinance.MonthlyInsurancePerAircraft < 0)
        {
            throw new InvalidOperationException(
                $"FleetFinance.MonthlyInsurancePerAircraft cannot be negative, was {FleetFinance.MonthlyInsurancePerAircraft}.");
        }

        if (Demand.NoAirMarketBelowNm <= 0 || Demand.NoAirMarketBelowNm >= Demand.SweetSpotMinNm)
        {
            throw new InvalidOperationException(
                $"Demand.NoAirMarketBelowNm must be positive and below Demand.SweetSpotMinNm ({Demand.SweetSpotMinNm}), was {Demand.NoAirMarketBelowNm}.");
        }

        if (Demand.NoAirMarketFloorFactor < 0 || Demand.NoAirMarketFloorFactor > Demand.ShortHopFloorFactor)
        {
            throw new InvalidOperationException(
                $"Demand.NoAirMarketFloorFactor must be in [0, Demand.ShortHopFloorFactor={Demand.ShortHopFloorFactor}], was {Demand.NoAirMarketFloorFactor}.");
        }

        if (Demand.MonthlySeasonality.Count != 12)
        {
            throw new InvalidOperationException("Demand.MonthlySeasonality must have exactly 12 entries (Jan-Dec).");
        }

        if (Demand.DayOfWeekMultiplier.Count != 7)
        {
            throw new InvalidOperationException("Demand.DayOfWeekMultiplier must have exactly 7 entries (Sunday=0..Saturday=6).");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Parses economy-config.json's text. Pure - takes the content as a string, never
    /// touches the filesystem itself. Validates before returning so a bad config fails fast at
    /// startup rather than producing silently wrong numbers mid-flight.</summary>
    public static EconomyConfig FromJson(string json)
    {
        var config = JsonSerializer.Deserialize<EconomyConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("economy-config.json parsed to null.");
        config.Validate();
        return config;
    }

    /// <summary>Hand-rolled defaults, used when no config file is present (e.g. in tests) and as
    /// the canonical values economy-config.json ships with. Numbers are explained where chosen -
    /// see the Economy README-equivalent comments on each config record.</summary>
    public static EconomyConfig Default()
    {
        var config = new EconomyConfig
        {
            MaxLoadFactor = 0.92,
            CaptiveFareCeilingMultiple = 1.5,
            PostCaptiveElasticity = 2.5,
            ReferenceFare = new ReferenceFareConfig
            {
                FarePerNm = 0.12m,
                MinimumFare = 65m,
            },
            Demand = new DemandConfig
            {
                CatchmentLarge = 10.0,
                CatchmentMedium = 3.0,
                CatchmentSmall = 0.6,
                CatchmentOther = 0.2,
                SweetSpotMinNm = 300,
                SweetSpotMaxNm = 2500,
                NoAirMarketBelowNm = 50,
                NoAirMarketFloorFactor = 0.01,
                ShortHopFloorFactor = 0.08,
                LongHaulDecayPerNm = 0.00035,
                MinDistanceFactor = 0.12,
                BaseDemandPerCatchmentPoint = 45.0,
                ReputationBaselineScore = 50.0,
                ReputationSensitivity = 0.5,
                ReputationFloor = 0.3,
                MonthlySeasonality = new[] { 0.90, 0.88, 0.95, 1.00, 1.05, 1.15, 1.22, 1.20, 1.02, 0.95, 0.88, 1.05 },
                DayOfWeekMultiplier = new[] { 0.95, 1.05, 0.95, 0.90, 1.00, 1.15, 1.10 },
            },
            Fuel = new FuelConfig
            {
                BasePricePerKg = 0.85m,
                DefaultRegionalMultiplier = 1.00m,
                RegionalMultipliers = new Dictionary<string, decimal>
                {
                    ["United Kingdom"] = 1.00m,
                    ["United States"] = 0.85m,
                    ["Germany"] = 1.05m,
                    ["France"] = 1.05m,
                    ["United Arab Emirates"] = 0.70m,
                    ["Japan"] = 1.15m,
                    ["Australia"] = 1.10m,
                    ["Brazil"] = 1.20m,
                    ["South Africa"] = 1.10m,
                },
                VolatilityAmplitude = 0.06,
                NoiseWindowDays = 5,
            },
            Costs = new CostConfig
            {
                LandingFeeRate = new AirportSizeRateTable { Large = 9.50m, Medium = 5.00m, Small = 2.25m, Other = 1.00m },
                HandlingFeeRate = new AirportSizeRateTable { Large = 6.50m, Medium = 3.50m, Small = 1.75m, Other = 0.75m },
                ParkingFeeRate = new AirportSizeRateTable { Large = 1.20m, Medium = 0.65m, Small = 0.30m, Other = 0.15m },
                PassengerChargeRate = new AirportSizeRateTable { Large = 12.00m, Medium = 7.00m, Small = 3.50m, Other = 1.50m },
                TurnaroundFeeRate = new AirportSizeRateTable { Large = 450m, Medium = 220m, Small = 90m, Other = 40m },
                MaintenanceAccrualPerHour = 210m,
                CrewCostPerHour = 340m,
                MinimumCrewDutyHours = 1.0,
            },
            AirlineStartup = new AirlineStartupConfig
            {
                StartingCapital = 2_000_000m,
                LeaseDepositMonths = 1.0,
                StartingPilotMonthlySalary = 9_000m,
            },
            FleetFinance = new FleetFinanceConfig
            {
                MonthlyInsurancePerAircraft = 6_000m,
            },
            StrategyProfiles = new List<StrategyProfileConfig>
            {
                // Elasticity and baseline load factor are chosen so the demand-cap mechanism
                // (see FareDemandModel) produces a revenue peak at ~1.1-1.5x the reference fare
                // for every profile - see the exploit tests in FareDemandModelExploitTests.
                new(AirlineStrategyProfile.LowCost, ReferenceFareMultiplier: 0.75m, BaselineLoadFactor: 0.76, Elasticity: 1.60, CostMultiplier: 0.85),
                new(AirlineStrategyProfile.Domestic, ReferenceFareMultiplier: 1.00m, BaselineLoadFactor: 0.78, Elasticity: 1.30, CostMultiplier: 1.00),
                new(AirlineStrategyProfile.International, ReferenceFareMultiplier: 1.15m, BaselineLoadFactor: 0.75, Elasticity: 1.15, CostMultiplier: 1.05),
                new(AirlineStrategyProfile.Premium, ReferenceFareMultiplier: 1.60m, BaselineLoadFactor: 0.68, Elasticity: 1.05, CostMultiplier: 1.35),
                // Neutral all-rounder: no fare, cost or reputation-facing bias in either direction.
                // Elasticity sits strictly between Domestic and Premium (still > 1.0, per Validate()
                // above, so the anti-exploit property holds here too) and baseline load factor sits
                // at the midpoint of the other four profiles' range - it is deliberately unremarkable
                // rather than a hidden best-in-class choice.
                new(AirlineStrategyProfile.Balanced, ReferenceFareMultiplier: 1.00m, BaselineLoadFactor: 0.73, Elasticity: 1.18, CostMultiplier: 1.00),
            },
        };

        config.Validate();
        return config;
    }
}

public sealed record ReferenceFareConfig
{
    public decimal FarePerNm { get; init; } = 0.12m;

    /// <summary>Raised from 35 to 65 in the fuel-honesty-fix pass: at 0.12/nm the formula fare for
    /// a short domestic hop (e.g. 275 nm -> ~£33) sat below the old floor, masking how cheap
    /// short-haul yield actually was and leaving fixed per-sector costs structurally unaffordable.
    /// See docs/PLAN.md "Status after the fuel-honesty fix".</summary>
    public decimal MinimumFare { get; init; } = 65m;
}

/// <summary>
/// Figures used once, when a new airline is founded. <see cref="StartingCapital"/> is a single
/// ledger line (LedgerCategory.StartingCapital); the deposit is a separate LeasePayment line of
/// <c>leaseRate x LeaseDepositMonths</c> for whichever aircraft type the player chose, so it
/// scales with the starter aircraft rather than being a flat figure that only suits one type. See
/// docs/PLAN.md "Economic balance" and "The progression loop" for the target this is tuned to.
/// </summary>
public sealed record AirlineStartupConfig
{
    public decimal StartingCapital { get; init; } = 2_000_000m;

    /// <summary>Months of the starter aircraft's monthly lease rate taken up-front as a deposit -
    /// standard practice for a real operating lease. Reduced from 2.0 to 1.0 in the 2026-08-08
    /// progression-loop rebalance: at the game-balanced starter lease (see
    /// AircraftTypeSeeder.cs), a 1-month deposit on a second aircraft is affordable within roughly
    /// 7-10 flights at a genuinely casual one-leg-a-day pace, which is the plan's explicit target -
    /// see docs/PLAN.md "The progression loop". A single month is still a normal real-world lease
    /// deposit term, unlike the lease rate itself.</summary>
    public double LeaseDepositMonths { get; init; } = 1.0;

    public decimal StartingPilotMonthlySalary { get; init; } = 9_000m;
}

/// <summary>Recurring monthly fleet costs that are not per-flight and not already covered by
/// <see cref="Lease.MonthlyRate"/> or <see cref="Pilot.MonthlySalary"/>. Together with the lease
/// payment and pilot salaries, this is the "fixed costs" side of the balance - see
/// docs/PLAN.md "The progression loop".</summary>
public sealed record FleetFinanceConfig
{
    /// <summary>DELIBERATE GAME-BALANCE FIGURE, NOT A REAL RATE - reduced from 50,000 (roughly
    /// 12% of the realistic figure) alongside the starter A320/B738's <c>MonthlyLeaseRate</c>
    /// (AircraftTypeSeeder.cs, also cut to ~8% of a real rate) in the 2026-08-08 progression-loop
    /// rebalance confirmed by the user: at real-world lease/insurance rates, one aircraft flown
    /// casually (~1 leg/day) can never be profitable, which the plan's "The progression loop"
    /// section requires. Do NOT "correct" this toward a realistic figure - see
    /// docs/PLAN.md "Status after the progression-loop rebalance" for the numbers this was derived
    /// from and why reverting it reopens an unplayable grind.</summary>
    public decimal MonthlyInsurancePerAircraft { get; init; } = 6_000m;
}

/// <summary>Per-strategy tuning: how the reference fare is derived, how elastic demand is to
/// price, the load factor a well-priced route settles at, and a cost-level multiplier applied
/// to service-level charges (handling/parking/passenger fees) - not to fuel or landing fees,
/// which are physical/regulatory and identical regardless of airline strategy.</summary>
public sealed record StrategyProfileConfig(
    AirlineStrategyProfile Profile,
    decimal ReferenceFareMultiplier,
    double BaselineLoadFactor,
    double Elasticity,
    double CostMultiplier);

public sealed class DemandConfig
{
    public double CatchmentLarge { get; init; } = 10.0;
    public double CatchmentMedium { get; init; } = 3.0;
    public double CatchmentSmall { get; init; } = 0.6;
    public double CatchmentOther { get; init; } = 0.2;

    public double SweetSpotMinNm { get; init; } = 300;
    public double SweetSpotMaxNm { get; init; } = 2500;

    /// <summary>
    /// Below this distance, essentially nobody books a scheduled flight - once check-in and taxi
    /// time are counted, driving wins, and scheduled sub-50 nm passenger routes barely exist
    /// outside a handful of island lifelines. The market approaches nil at (theoretical) zero
    /// distance (<see cref="NoAirMarketFloorFactor"/>) and ramps up to the ordinary short-hop
    /// curve (<see cref="ShortHopFloorFactor"/>) by the time distance reaches this threshold - see
    /// <see cref="DemandCalculator"/>'s cubic ramp for why it stays low across most of this band
    /// rather than granting a meaningfully sized market at, say, half the threshold distance. This
    /// is the real fix for the micro-sector exploit (a demand curve that doesn't believe in
    /// geography), not a cost or fare lever - see
    /// FlightEconomicsIntegrityTests.MicroSectorLoop_EvenAtTheBestPossibleFare_IsNetNegative.
    /// </summary>
    public double NoAirMarketBelowNm { get; init; } = 50;

    /// <summary>Distance factor at (theoretical) zero distance, i.e. how close to "no market at
    /// all" the curve reaches for the shortest conceivable hop. See <see cref="NoAirMarketBelowNm"/>.</summary>
    public double NoAirMarketFloorFactor { get; init; } = 0.01;

    /// <summary>Distance factor at <see cref="NoAirMarketBelowNm"/>, where the near-zero curve
    /// hands off to the ordinary short-hop ramp toward the sweet spot. This is no longer the
    /// factor at zero distance (see <see cref="NoAirMarketFloorFactor"/> for that) - it is one of
    /// two levers (with CostConfig's fixed per-sector charges) that keep an ordinary short hop
    /// (well above the no-air-market threshold) from being trivially exploitable.</summary>
    public double ShortHopFloorFactor { get; init; } = 0.08;

    public double LongHaulDecayPerNm { get; init; } = 0.00035;
    public double MinDistanceFactor { get; init; } = 0.12;

    public double BaseDemandPerCatchmentPoint { get; init; } = 45.0;

    public double ReputationBaselineScore { get; init; } = 50.0;
    public double ReputationSensitivity { get; init; } = 0.5;
    public double ReputationFloor { get; init; } = 0.3;

    /// <summary>Jan..Dec, index 0 = January.</summary>
    public IReadOnlyList<double> MonthlySeasonality { get; init; } = new[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };

    /// <summary>Indexed by <see cref="DayOfWeek"/> (Sunday=0..Saturday=6).</summary>
    public IReadOnlyList<double> DayOfWeekMultiplier { get; init; } = new[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
}

public sealed class FuelConfig
{
    public decimal BasePricePerKg { get; init; } = 0.85m;
    public IReadOnlyDictionary<string, decimal> RegionalMultipliers { get; init; } = new Dictionary<string, decimal>();
    public decimal DefaultRegionalMultiplier { get; init; } = 1.00m;

    /// <summary>Maximum fractional swing the random walk can apply either side of the regional
    /// base price (0.06 = +/-6%).</summary>
    public double VolatilityAmplitude { get; init; } = 0.06;

    /// <summary>Trailing window (days) averaged to produce the walk - larger means smoother,
    /// slower-moving prices.</summary>
    public int NoiseWindowDays { get; init; } = 5;
}

public sealed class CostConfig
{
    public AirportSizeRateTable LandingFeeRate { get; init; } = new();
    public AirportSizeRateTable HandlingFeeRate { get; init; } = new();
    public AirportSizeRateTable ParkingFeeRate { get; init; } = new();
    public AirportSizeRateTable PassengerChargeRate { get; init; } = new();

    /// <summary>Flat per-sector ground-ops/gate charge - unlike the fees above, deliberately NOT
    /// scaled by weight or passenger count: a turnaround has a minimum overhead (stand allocation,
    /// push-back crew, minimum handling call-out) regardless of how small the aircraft or how
    /// short the sector. This is what makes flying a trivial sector on repeat lossy even when a
    /// small/light aircraft would otherwise dodge the weight-based fees above.</summary>
    public AirportSizeRateTable TurnaroundFeeRate { get; init; } = new();

    public decimal MaintenanceAccrualPerHour { get; init; } = 210m;
    public decimal CrewCostPerHour { get; init; } = 340m;

    /// <summary>Crew are paid for a minimum duty block regardless of how short the sector is -
    /// a real crew doesn't get sent home and re-hired for a 6-minute hop. CrewCost uses
    /// max(flightHours, MinimumCrewDutyHours), so ultra-short sectors still carry a meaningful
    /// crew cost instead of one that shrinks toward zero with block time.</summary>
    public double MinimumCrewDutyHours { get; init; } = 1.0;
}

/// <summary>Rate table keyed by airport size, used for every weight- or size-based charge
/// (landing, handling, parking, passenger fees). Explicit fields rather than a
/// Dictionary&lt;AirportSizeCategory,decimal&gt; so the JSON shape stays simple and readable;
/// airports outside Large/Medium/Small (heliport, seaplane, closed) fall back to Other.</summary>
public sealed class AirportSizeRateTable
{
    public decimal Large { get; init; }
    public decimal Medium { get; init; }
    public decimal Small { get; init; }
    public decimal Other { get; init; }

    public decimal RateFor(AirportSizeCategory size) => size switch
    {
        AirportSizeCategory.Large => Large,
        AirportSizeCategory.Medium => Medium,
        AirportSizeCategory.Small => Small,
        _ => Other,
    };
}

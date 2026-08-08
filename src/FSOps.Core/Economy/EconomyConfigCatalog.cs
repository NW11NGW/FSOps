using System.Text.Json;
using System.Text.Json.Serialization;
using FSOps.Core.Entities;

namespace FSOps.Core.Economy;

/// <summary>
/// Loads economy-config.json's playstyle-aware shape and resolves a fully-populated
/// <see cref="EconomyConfig"/> per <see cref="AirlinePlaystyle"/> - see docs/PLAN.md "Playstyle -
/// Casual vs True-life". The file carries a shared base (fares, demand, fuel, costs, strategy
/// profiles - identical for every playstyle) plus a small <c>casual</c> and <c>trueLife</c> block
/// that each override only the handful of figures that actually differ: starting capital, the
/// lease deposit term, every aircraft type's lease rate, and monthly insurance. A playstyle is a
/// <b>named set of overrides, not a code path</b> - nothing downstream branches on it; callers
/// just resolve the right already-merged <see cref="EconomyConfig"/> once (typically right after
/// loading the airline that owns the request) and hand it to the same economy engine every other
/// airline uses. Pure, like <see cref="EconomyConfig.FromJson"/> - takes the file's text as a
/// plain string and never touches the filesystem itself.
/// </summary>
public sealed class EconomyConfigCatalog
{
    private readonly IReadOnlyDictionary<AirlinePlaystyle, EconomyConfig> _resolved;

    private EconomyConfigCatalog(IReadOnlyDictionary<AirlinePlaystyle, EconomyConfig> resolved)
    {
        _resolved = resolved;
    }

    /// <summary>
    /// The fully resolved, already-validated config for one playstyle. Throws if the catalogue
    /// somehow has no entry for a live enum value - the same "config authoring bug, not a silent
    /// default" stance as <see cref="EconomyConfig.GetStrategy"/>.
    /// </summary>
    public EconomyConfig Get(AirlinePlaystyle playstyle)
    {
        if (!_resolved.TryGetValue(playstyle, out var config))
        {
            throw new InvalidOperationException($"economy-config.json has no override block for playstyle '{playstyle}'.");
        }

        return config;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parses economy-config.json's text: the shared base fields (same shape as
    /// <see cref="EconomyConfig"/> itself - "casual"/"trueLife" are simply unmapped JSON
    /// properties from that parse's point of view) plus the two named override blocks, merges
    /// each into a resolved <see cref="EconomyConfig"/>, and validates every one - so a bad config
    /// for either playstyle fails fast at startup, exactly like <see cref="EconomyConfig.FromJson"/>.
    /// </summary>
    public static EconomyConfigCatalog FromJson(string json)
    {
        var baseConfig = JsonSerializer.Deserialize<EconomyConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("economy-config.json parsed to null.");
        var overrides = JsonSerializer.Deserialize<PlaystyleOverridesDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("economy-config.json parsed to null.");

        var resolved = new Dictionary<AirlinePlaystyle, EconomyConfig>();
        foreach (var playstyle in Enum.GetValues<AirlinePlaystyle>())
        {
            var block = playstyle switch
            {
                AirlinePlaystyle.Casual => overrides.Casual,
                AirlinePlaystyle.TrueLife => overrides.TrueLife,
                _ => throw new InvalidOperationException($"EconomyConfigCatalog has no loader case for playstyle '{playstyle}'."),
            };

            if (block is null)
            {
                throw new InvalidOperationException($"economy-config.json is missing the '{playstyle}' playstyle block.");
            }

            var config = Resolve(baseConfig, block);
            config.Validate();
            resolved[playstyle] = config;
        }

        return new EconomyConfigCatalog(resolved);
    }

    /// <summary>
    /// Hand-rolled defaults mirroring the real economy-config.json, used when no config file is
    /// present (e.g. a stripped deployment) and as the canonical values for tests. Casual matches
    /// <see cref="EconomyConfig.Default"/> field-for-field; True-life is derived the same way the
    /// shipped file's "trueLife" block is - see that block's own comments for the reasoning behind
    /// each figure.
    /// </summary>
    public static EconomyConfigCatalog Default()
    {
        var baseConfig = EconomyConfig.Default();

        var resolved = new Dictionary<AirlinePlaystyle, EconomyConfig>
        {
            [AirlinePlaystyle.Casual] = baseConfig,
            [AirlinePlaystyle.TrueLife] = Resolve(baseConfig, new PlaystyleOverrides
            {
                AirlineStartup = new PlaystyleAirlineStartupOverrides
                {
                    StartingCapital = 2_500_000m,
                    LeaseDepositMonths = 2.0,
                },
                // Only the two starter types (A320/B738) actually differ from Casual - the other
                // four (A319/A321/B737/B739) were never game-balanced, so True-life charges the
                // same realistic rate Casual does for those. See EconomyConfig.Default()'s LeaseRates
                // for the Casual list this mirrors.
                LeaseRates = new List<LeaseRateConfig>
                {
                    new("A319", 350_000m),
                    new("A320", 380_000m),
                    new("A321", 420_000m),
                    new("B737", 340_000m),
                    new("B738", 390_000m),
                    new("B739", 410_000m),
                },
                FleetFinance = new PlaystyleFleetFinanceOverrides { MonthlyInsurancePerAircraft = 50_000m },
                Maintenance = new PlaystyleMaintenanceOverrides { ACheckDowntimeHours = 24, CCheckDowntimeHours = 336 },
                // Realistic borrowing costs to match every other True-life figure - a higher base
                // rate and the plan's 8% hard cap, both above Casual's 2.5%/5.0% (see LoanConfig's
                // own doc). See docs/PLAN.md "Loan interest is set by the simulation, never by the
                // player".
                Loan = new PlaystyleLoanOverrides { BaseAnnualRatePct = 4.0, CapAnnualRatePct = 8.0 },
            }),
        };

        foreach (var config in resolved.Values)
        {
            config.Validate();
        }

        return new EconomyConfigCatalog(resolved);
    }

    private static EconomyConfig Resolve(EconomyConfig baseConfig, PlaystyleOverrides overrides) => new()
    {
        MaxLoadFactor = baseConfig.MaxLoadFactor,
        CaptiveFareCeilingMultiple = baseConfig.CaptiveFareCeilingMultiple,
        PostCaptiveElasticity = baseConfig.PostCaptiveElasticity,
        ReferenceFare = baseConfig.ReferenceFare,
        Demand = baseConfig.Demand,
        Fuel = baseConfig.Fuel,
        Costs = baseConfig.Costs,
        StrategyProfiles = baseConfig.StrategyProfiles,
        AirlineStartup = new AirlineStartupConfig
        {
            // Shared across playstyles - the founding pilot's salary is already a realistic
            // figure and needs no game-balance treatment (see docs/PLAN.md "The starter lease is
            // a deliberate game-balance number").
            StartingPilotMonthlySalary = baseConfig.AirlineStartup.StartingPilotMonthlySalary,
            StartingCapital = overrides.AirlineStartup.StartingCapital,
            LeaseDepositMonths = overrides.AirlineStartup.LeaseDepositMonths,
        },
        // Entirely from the playstyle's own override block, same as StartingCapital/LeaseDepositMonths
        // above - every leasable type gets an explicit per-playstyle rate here, not merged field-by-
        // field with the base, so a playstyle block that forgets a type fails loudly via LeaseRateFor
        // rather than silently inheriting the wrong figure.
        LeaseRates = overrides.LeaseRates,
        FleetFinance = new FleetFinanceConfig
        {
            MonthlyInsurancePerAircraft = overrides.FleetFinance.MonthlyInsurancePerAircraft,
        },
        // Interval/cost/condition figures are shared across playstyles (docs/PLAN.md "same cycle...
        // and same costs"); only the two downtime figures come from the playstyle's own override -
        // see MaintenanceConfig's class doc.
        Maintenance = new MaintenanceConfig
        {
            ACheckIntervalHours = baseConfig.Maintenance.ACheckIntervalHours,
            CCheckIntervalHours = baseConfig.Maintenance.CCheckIntervalHours,
            ACheckCost = baseConfig.Maintenance.ACheckCost,
            CCheckCost = baseConfig.Maintenance.CCheckCost,
            ConditionDecayPerHour = baseConfig.Maintenance.ConditionDecayPerHour,
            ACheckConditionRestorePercent = baseConfig.Maintenance.ACheckConditionRestorePercent,
            ACheckDowntimeHours = overrides.Maintenance.ACheckDowntimeHours,
            CCheckDowntimeHours = overrides.Maintenance.CCheckDowntimeHours,
        },
        // Entirely from the playstyle's own override block, same reasoning as LeaseRates/FleetFinance
        // above - a playstyle block that forgets "loan" fails Validate()'s Loan.CapAnnualRatePct
        // check (both fields resolve to 0) rather than silently borrowing the other playstyle's or a
        // hand-rolled default's rates.
        Loan = new LoanConfig
        {
            BaseAnnualRatePct = overrides.Loan.BaseAnnualRatePct,
            CapAnnualRatePct = overrides.Loan.CapAnnualRatePct,
        },
        UsedAircraft = baseConfig.UsedAircraft,
    };

    /// <summary>Shape used only to pick the "casual"/"trueLife" properties out of the same JSON
    /// text the base config is parsed from - every other property here is simply ignored.</summary>
    private sealed class PlaystyleOverridesDocument
    {
        public PlaystyleOverrides? Casual { get; init; }

        public PlaystyleOverrides? TrueLife { get; init; }
    }

    private sealed class PlaystyleOverrides
    {
        public PlaystyleAirlineStartupOverrides AirlineStartup { get; init; } = new();

        public IReadOnlyList<LeaseRateConfig> LeaseRates { get; init; } = Array.Empty<LeaseRateConfig>();

        public PlaystyleFleetFinanceOverrides FleetFinance { get; init; } = new();

        public PlaystyleMaintenanceOverrides Maintenance { get; init; } = new();

        public PlaystyleLoanOverrides Loan { get; init; } = new();
    }

    private sealed class PlaystyleAirlineStartupOverrides
    {
        public decimal StartingCapital { get; init; }

        public double LeaseDepositMonths { get; init; }
    }

    private sealed class PlaystyleFleetFinanceOverrides
    {
        public decimal MonthlyInsurancePerAircraft { get; init; }
    }

    private sealed class PlaystyleMaintenanceOverrides
    {
        public double ACheckDowntimeHours { get; init; }

        public double CCheckDowntimeHours { get; init; }
    }

    private sealed class PlaystyleLoanOverrides
    {
        public double BaseAnnualRatePct { get; init; }

        public double CapAnnualRatePct { get; init; }
    }
}

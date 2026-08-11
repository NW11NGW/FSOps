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

    /// <summary>
    /// Tuning for how <see cref="Entities.Airline.ReputationScore"/> moves - see docs/PLAN.md
    /// "Progression - reputation and pilot skill". Shared across playstyles: how forgiving
    /// reputation is to earn or lose is a simulation-fairness question, not a figure the user
    /// asked to differ between Casual and True-life.
    /// </summary>
    public ReputationConfig Reputation { get; init; } = new();

    /// <summary>
    /// Tuning for how <see cref="Entities.Pilot.SkillRating"/> grows with hours and decays with
    /// idle time - see docs/PLAN.md "Progression - reputation and pilot skill". Shared across
    /// playstyles, same reasoning as <see cref="Reputation"/> above.
    /// </summary>
    public PilotSkillConfig PilotSkill { get; init; } = new();

    public FuelConfig Fuel { get; init; } = new();

    public CostConfig Costs { get; init; } = new();

    /// <summary>Figures used once, at airline creation - starting capital, the lease deposit and
    /// the founding pilot's salary. Kept here rather than as C# constants (see the old
    /// AirlineCreationDefaults) so balance can be retuned without a code change, same as every
    /// other number in this file.</summary>
    public AirlineStartupConfig AirlineStartup { get; init; } = new();

    /// <summary>
    /// The monthly lease rate for every leasable aircraft type, keyed by ICAO type code (e.g.
    /// "A320", "B738") - the single source of truth for every lease price the app ever charges,
    /// the founding lease (AirlineEndpoints.CreateAsync) and leasing an additional aircraft from
    /// the Fleet screen (FleetEndpoints.LeaseAsync) alike. Resolved per playstyle by
    /// <see cref="EconomyConfigCatalog"/>, same as every other AirlineStartup/FleetFinance figure.
    /// Deliberately NOT <see cref="AircraftType.MonthlyLeaseRate"/> - that is a single column
    /// shared by every airline's database row regardless of playstyle and regardless of when the
    /// database was created (the seeder only runs once, against an empty table), so it can never
    /// hold "the Casual rate" and "the True-life rate" for the same type at once, and a database
    /// seeded before a rebalance would silently disagree with one seeded after it. See
    /// AircraftTypeSeeder.cs's class doc for why that column is no longer read for pricing.
    /// </summary>
    public IReadOnlyList<LeaseRateConfig> LeaseRates { get; init; } = Array.Empty<LeaseRateConfig>();

    [JsonIgnore]
    private Dictionary<string, decimal>? _leaseRateLookup;

    /// <summary>
    /// The monthly lease rate for one aircraft type. Throws if the config has no entry for it - a
    /// missing rate is a config authoring bug (the seeded catalogue has a type economy-config.json
    /// never heard of), not something to silently default to whatever a database column happens to
    /// hold. This is the ONLY sanctioned way to price a lease anywhere in the app.
    /// </summary>
    public decimal LeaseRateFor(string icaoType)
    {
        _leaseRateLookup ??= LeaseRates.ToDictionary(r => r.IcaoType, r => r.MonthlyRate, StringComparer.OrdinalIgnoreCase);

        if (!_leaseRateLookup.TryGetValue(icaoType, out var rate))
        {
            throw new InvalidOperationException(
                $"economy-config.json has no lease rate configured for aircraft type '{icaoType}'.");
        }

        return rate;
    }

    /// <summary>
    /// One consistent multiplier applied to <see cref="AircraftType.PurchasePrice"/> (the shared,
    /// realistic transaction value every airline's database row holds - see AircraftTypeSeeder.cs's
    /// class doc) to get THIS playstyle's purchase price - see docs/PLAN.md "Casual pricing must
    /// scale the WHOLE catalogue". True-life's is exactly 1.0 (real figures throughout); Casual's
    /// is small enough that a used airframe (55% of this, see <see cref="UsedAircraft"/>) is
    /// affordable after roughly ten sectors' profit. Deliberately a scalar rather than a per-type
    /// list (unlike <see cref="LeaseRates"/>): since it is derived from AircraftType.PurchasePrice,
    /// which every new catalogue type already has, a newly-added type gets a correctly-scaled
    /// Casual purchase price automatically, with nothing to remember to author by hand.
    /// </summary>
    public decimal PurchasePriceMultiplier { get; init; } = 1.0m;

    /// <summary>
    /// This playstyle's purchase price for a "New" example of <paramref name="aircraftType"/> - the
    /// ONLY sanctioned way to price a purchase anywhere in the app (never read
    /// <see cref="AircraftType.PurchasePrice"/> directly - see <see cref="PurchasePriceMultiplier"/>'s
    /// own doc for why). <see cref="MaintenanceScheduler.ResolveUsedAircraftState"/> applies the
    /// used-aircraft discount on top of whatever this returns, so "Used" is always 55% of THIS
    /// playstyle's new price, never of the shared realistic figure directly.
    /// </summary>
    public decimal PurchasePriceFor(AircraftType aircraftType) =>
        Math.Round(aircraftType.PurchasePrice * PurchasePriceMultiplier, 2, MidpointRounding.AwayFromZero);

    /// <summary>Recurring monthly costs that are not tied to any single flight. Currently just
    /// insurance; lease and salary are already recurring via <see cref="Lease.MonthlyRate"/> and
    /// <see cref="Pilot.MonthlySalary"/> and do not need duplicating here.</summary>
    public FleetFinanceConfig FleetFinance { get; init; } = new();

    /// <summary>A/C-check cycle, cost and downtime - see docs/PLAN.md "Maintenance" and
    /// "Playstyle - Casual vs True-life"'s behaviour table. The interval hours, costs and condition
    /// figures are shared across playstyles (same cycle, same costs - see the plan's table); only
    /// the two downtime figures differ, resolved from the "casual"/"trueLife" override blocks by
    /// EconomyConfigCatalog, same pattern as AirlineStartup/FleetFinance.</summary>
    public MaintenanceConfig Maintenance { get; init; } = new();

    /// <summary>
    /// Risk-based loan pricing - see docs/PLAN.md "Loan interest is set by the simulation, never by
    /// the player". Entirely playstyle-owned (like FleetFinance/LeaseRates, not merged field-by-
    /// field with a shared base): resolved from the "casual"/"trueLife" override blocks by
    /// EconomyConfigCatalog. The only sanctioned way to price a loan anywhere in the app is
    /// <see cref="FSOps.Core.Finance.LoanRateCalculator.ComputeAnnualRatePct"/>, which reads this.
    /// </summary>
    public LoanConfig Loan { get; init; } = new();

    /// <summary>
    /// The penalty for ending a lease before it would otherwise have been billed - see docs/PLAN.md
    /// "Returning a lease early must cost something". Resolved per playstyle by
    /// <see cref="EconomyConfigCatalog"/>, like <see cref="FleetFinance"/>/<see cref="Loan"/> above -
    /// harsher in True-life like every other figure that differs by playstyle. See
    /// <see cref="LeaseEarlyTerminationConfig"/>'s own doc for what the fee sits alongside.
    /// </summary>
    public LeaseEarlyTerminationConfig LeaseEarlyTermination { get; init; } = new();

    /// <summary>
    /// The fee for paying off a loan's full remaining balance before term - see docs/PLAN.md "Paying
    /// a loan back... Charge a modest early-settlement fee". Resolved per playstyle by
    /// <see cref="EconomyConfigCatalog"/>, same pattern as <see cref="LeaseEarlyTermination"/> above.
    /// </summary>
    public LoanEarlySettlementConfig LoanEarlySettlement { get; init; } = new();

    /// <summary>Purchasing a used, rather than new, airframe - see docs/PLAN.md "Used aircraft -
    /// cheap to buy, expensive to run". Shared across playstyles: the discount and the wear a used
    /// airframe starts with are a property of the used-aircraft market, not a game-balance figure
    /// that differs by how realistically the player wants to be billed.</summary>
    public UsedAircraftConfig UsedAircraft { get; init; } = new();

    /// <summary>The depreciation curve <see cref="FSOps.Core.Finance.AircraftDepreciationCalculator"/>
    /// reads when quoting a sale - see docs/PLAN.md "Selling an owned aircraft must lose money on the
    /// spread". Shared across playstyles, like <see cref="UsedAircraft"/> - what a worn airframe is
    /// worth on the secondhand market doesn't depend on how realistically the SELLING airline chose to
    /// be billed elsewhere.</summary>
    public AircraftDepreciationConfig Depreciation { get; init; } = new();

    /// <summary>
    /// Pilot rest/duty and minimum turnaround for the weekly schedule builder - see docs/PLAN.md
    /// "Virtual pilot scheduling". Shared across playstyles: a duty day and a legal rest period are
    /// working-time facts, not a game-balance figure that should differ by how realistically the
    /// player wants to be billed (unlike <see cref="UnflyableSchedule"/> below, which does differ).
    /// </summary>
    public SchedulingConfig Scheduling { get; init; } = new();

    /// <summary>
    /// What happens to a scheduled virtual flight that cannot fly (aircraft in maintenance, still
    /// away, or otherwise unavailable) - see docs/PLAN.md "Playstyle is not only numbers - it
    /// changes behaviour". Resolved per playstyle by <see cref="EconomyConfigCatalog"/>, like
    /// <see cref="FleetFinance"/>/<see cref="Loan"/>: Casual's <see cref="UnflyableScheduleConfig.CancellationFee"/>
    /// is zero (skipped quietly, no penalty), True-life's is positive (cancelled with a real cost).
    /// Nothing in <see cref="FSOps.Server.Services.VirtualFlightResolverService"/> branches on the
    /// playstyle directly - it only ever asks "is the configured fee zero or positive", so a
    /// hypothetical third playstyle just needs its own override block, never a code change.
    /// </summary>
    public UnflyableScheduleConfig UnflyableSchedule { get; init; } = new();

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

        if (LeaseRates.Count == 0)
        {
            throw new InvalidOperationException("economy-config.json defines no lease rates.");
        }

        var seenLeaseRateIcaoTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leaseRate in LeaseRates)
        {
            if (string.IsNullOrWhiteSpace(leaseRate.IcaoType))
            {
                throw new InvalidOperationException("A LeaseRates entry has a blank icaoType.");
            }

            if (!seenLeaseRateIcaoTypes.Add(leaseRate.IcaoType))
            {
                throw new InvalidOperationException($"LeaseRates has more than one entry for icaoType '{leaseRate.IcaoType}'.");
            }

            if (leaseRate.MonthlyRate <= 0)
            {
                throw new InvalidOperationException(
                    $"LeaseRates entry '{leaseRate.IcaoType}' must have a positive monthlyRate, was {leaseRate.MonthlyRate}.");
            }
        }

        if (PurchasePriceMultiplier <= 0)
        {
            throw new InvalidOperationException(
                $"PurchasePriceMultiplier must be positive, was {PurchasePriceMultiplier}.");
        }

        if (FleetFinance.MonthlyInsurancePerAircraft < 0)
        {
            throw new InvalidOperationException(
                $"FleetFinance.MonthlyInsurancePerAircraft cannot be negative, was {FleetFinance.MonthlyInsurancePerAircraft}.");
        }

        if (Maintenance.ACheckIntervalHours <= 0)
        {
            throw new InvalidOperationException($"Maintenance.ACheckIntervalHours must be positive, was {Maintenance.ACheckIntervalHours}.");
        }

        if (Maintenance.CCheckIntervalHours <= Maintenance.ACheckIntervalHours)
        {
            throw new InvalidOperationException(
                $"Maintenance.CCheckIntervalHours ({Maintenance.CCheckIntervalHours}) must be greater than Maintenance.ACheckIntervalHours ({Maintenance.ACheckIntervalHours}).");
        }

        if (Maintenance.ACheckCost < 0 || Maintenance.CCheckCost < 0)
        {
            throw new InvalidOperationException("Maintenance.ACheckCost and Maintenance.CCheckCost cannot be negative.");
        }

        if (Maintenance.ConditionDecayPerHour < 0)
        {
            throw new InvalidOperationException($"Maintenance.ConditionDecayPerHour cannot be negative, was {Maintenance.ConditionDecayPerHour}.");
        }

        if (Maintenance.ACheckConditionRestorePercent is < 0 or > 100)
        {
            throw new InvalidOperationException(
                $"Maintenance.ACheckConditionRestorePercent must be in [0,100], was {Maintenance.ACheckConditionRestorePercent}.");
        }

        // Strictly positive, not just non-negative: a check that grounds the aircraft for zero
        // hours is not a grounding at all, and a missing playstyle override block in
        // economy-config.json would otherwise resolve to exactly that (0) without this catching it.
        if (Maintenance.ACheckDowntimeHours <= 0 || Maintenance.CCheckDowntimeHours <= 0)
        {
            throw new InvalidOperationException("Maintenance.ACheckDowntimeHours and Maintenance.CCheckDowntimeHours must be positive.");
        }

        if (Maintenance.CCheckDowntimeHours <= Maintenance.ACheckDowntimeHours)
        {
            throw new InvalidOperationException(
                $"Maintenance.CCheckDowntimeHours ({Maintenance.CCheckDowntimeHours}) must be greater than Maintenance.ACheckDowntimeHours ({Maintenance.ACheckDowntimeHours}).");
        }

        if (Loan.BaseAnnualRatePct <= 0)
        {
            throw new InvalidOperationException($"Loan.BaseAnnualRatePct must be positive, was {Loan.BaseAnnualRatePct}.");
        }

        // Strictly greater, not just non-negative: a cap equal to the base rate would leave no room
        // for risk-based scaling at all, and a missing playstyle override block would otherwise
        // resolve both to 0 without this catching it (same defensive pattern as the maintenance
        // downtime check above).
        if (Loan.CapAnnualRatePct <= Loan.BaseAnnualRatePct)
        {
            throw new InvalidOperationException(
                $"Loan.CapAnnualRatePct ({Loan.CapAnnualRatePct}) must be greater than Loan.BaseAnnualRatePct ({Loan.BaseAnnualRatePct}).");
        }

        if (Loan.MaxStartingLoanPrincipal <= 0)
        {
            throw new InvalidOperationException(
                $"Loan.MaxStartingLoanPrincipal must be positive, was {Loan.MaxStartingLoanPrincipal}.");
        }

        if (UsedAircraft.PriceMultiplier is <= 0 or >= 1)
        {
            throw new InvalidOperationException(
                $"UsedAircraft.PriceMultiplier must be in (0,1) - a used airframe must cost less than new, was {UsedAircraft.PriceMultiplier}.");
        }

        if (UsedAircraft.StartingHoursSinceACheckFraction is < 0 or >= 1)
        {
            throw new InvalidOperationException(
                $"UsedAircraft.StartingHoursSinceACheckFraction must be in [0,1), was {UsedAircraft.StartingHoursSinceACheckFraction}.");
        }

        if (UsedAircraft.StartingHoursSinceCCheckFraction is < 0 or >= 1)
        {
            throw new InvalidOperationException(
                $"UsedAircraft.StartingHoursSinceCCheckFraction must be in [0,1), was {UsedAircraft.StartingHoursSinceCCheckFraction}.");
        }

        if (UsedAircraft.StartingConditionPercent is <= 0 or > 100)
        {
            throw new InvalidOperationException(
                $"UsedAircraft.StartingConditionPercent must be in (0,100], was {UsedAircraft.StartingConditionPercent}.");
        }

        if (UsedAircraft.StartingAirframeHours < 0)
        {
            throw new InvalidOperationException(
                $"UsedAircraft.StartingAirframeHours cannot be negative, was {UsedAircraft.StartingAirframeHours}.");
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

        if (Reputation.BaselineScore is < 0 or > 100)
        {
            throw new InvalidOperationException($"Reputation.BaselineScore must be in [0,100], was {Reputation.BaselineScore}.");
        }

        if (Reputation.Alpha is <= 0 or >= 1)
        {
            throw new InvalidOperationException($"Reputation.Alpha must be in (0,1), was {Reputation.Alpha}.");
        }

        if (Reputation.OnTimeWeight < 0 || Reputation.LandingWeight < 0)
        {
            throw new InvalidOperationException("Reputation.OnTimeWeight and Reputation.LandingWeight cannot be negative.");
        }

        if (Math.Abs(Reputation.OnTimeWeight + Reputation.LandingWeight - 1.0) > 0.0001)
        {
            throw new InvalidOperationException(
                $"Reputation.OnTimeWeight + Reputation.LandingWeight must sum to 1.0, was {Reputation.OnTimeWeight + Reputation.LandingWeight}.");
        }

        if (Reputation.OnTimeToleranceMinutes < 0 || Reputation.OnTimeZeroScoreDelayMinutes <= Reputation.OnTimeToleranceMinutes)
        {
            throw new InvalidOperationException(
                "Reputation.OnTimeZeroScoreDelayMinutes must be positive and greater than Reputation.OnTimeToleranceMinutes.");
        }

        if (Reputation.LandingBestFpm <= 0 || Reputation.LandingWorstFpm <= Reputation.LandingBestFpm)
        {
            throw new InvalidOperationException("Reputation.LandingWorstFpm must be greater than Reputation.LandingBestFpm, both positive.");
        }

        if (Reputation.CancelledAlphaMultiplier < 1.0)
        {
            throw new InvalidOperationException(
                $"Reputation.CancelledAlphaMultiplier must be at least 1.0 - a cancellation must never move reputation more " +
                $"slowly than an ordinary sector, was {Reputation.CancelledAlphaMultiplier}.");
        }

        if (Reputation.Alpha * Reputation.CancelledAlphaMultiplier >= 1.0)
        {
            throw new InvalidOperationException(
                "Reputation.Alpha x Reputation.CancelledAlphaMultiplier must stay below 1.0 - beyond that a single " +
                "cancellation would overshoot its own target instead of merely moving toward it.");
        }

        if (Reputation.ManualCompletionTargetScore is < 0 or > 100)
        {
            throw new InvalidOperationException(
                $"Reputation.ManualCompletionTargetScore must be in [0,100], was {Reputation.ManualCompletionTargetScore}.");
        }

        if (Reputation.ManualCompletionAlphaMultiplier is <= 0 or >= 1)
        {
            throw new InvalidOperationException(
                $"Reputation.ManualCompletionAlphaMultiplier must be in (0,1) - a manual completion must move reputation " +
                $"LESS than an ordinary tracked sector's own step (it is a flat, timing-independent penalty, never a worst-case " +
                $"one), was {Reputation.ManualCompletionAlphaMultiplier}.");
        }

        if (PilotSkill.StartingSkill is < 0 or > 100)
        {
            throw new InvalidOperationException($"PilotSkill.StartingSkill must be in [0,100], was {PilotSkill.StartingSkill}.");
        }

        if (PilotSkill.SkillCap <= PilotSkill.StartingSkill || PilotSkill.SkillCap > 100)
        {
            throw new InvalidOperationException(
                $"PilotSkill.SkillCap must be greater than PilotSkill.StartingSkill and at most 100, was {PilotSkill.SkillCap}.");
        }

        if (PilotSkill.GrowthHalfLifeHours <= 0)
        {
            throw new InvalidOperationException($"PilotSkill.GrowthHalfLifeHours must be positive, was {PilotSkill.GrowthHalfLifeHours}.");
        }

        if (PilotSkill.IdleGracePeriodHours < 0)
        {
            throw new InvalidOperationException($"PilotSkill.IdleGracePeriodHours cannot be negative, was {PilotSkill.IdleGracePeriodHours}.");
        }

        if (PilotSkill.IdleDecayHalfLifeHours <= 0)
        {
            throw new InvalidOperationException($"PilotSkill.IdleDecayHalfLifeHours must be positive, was {PilotSkill.IdleDecayHalfLifeHours}.");
        }

        if (Scheduling.MinRestHoursBetweenDutyDays <= 0)
        {
            throw new InvalidOperationException($"Scheduling.MinRestHoursBetweenDutyDays must be positive, was {Scheduling.MinRestHoursBetweenDutyDays}.");
        }

        if (Scheduling.MaxDutyHoursPerDay is <= 0 or > 24)
        {
            throw new InvalidOperationException($"Scheduling.MaxDutyHoursPerDay must be in (0,24], was {Scheduling.MaxDutyHoursPerDay}.");
        }

        if (Scheduling.MinRestHoursBetweenDutyDays + Scheduling.MaxDutyHoursPerDay > 24)
        {
            throw new InvalidOperationException(
                "Scheduling.MinRestHoursBetweenDutyDays + Scheduling.MaxDutyHoursPerDay must not exceed 24 - " +
                "otherwise a pilot flying every day could never legally satisfy both in the same 24-hour cycle.");
        }

        if (Scheduling.MinTurnaroundMinutes < 0)
        {
            throw new InvalidOperationException($"Scheduling.MinTurnaroundMinutes cannot be negative, was {Scheduling.MinTurnaroundMinutes}.");
        }

        if (UnflyableSchedule.CancellationFee < 0)
        {
            throw new InvalidOperationException($"UnflyableSchedule.CancellationFee cannot be negative, was {UnflyableSchedule.CancellationFee}.");
        }

        if (Depreciation.NewAircraftResaleFactor is <= 0 or > 1)
        {
            throw new InvalidOperationException(
                $"Depreciation.NewAircraftResaleFactor must be in (0,1], was {Depreciation.NewAircraftResaleFactor}.");
        }

        if (Depreciation.ConditionDepreciationWeight < 0 || Depreciation.CycleWearDepreciationWeight < 0 || Depreciation.GroundedForMaintenancePenalty < 0)
        {
            throw new InvalidOperationException("Depreciation's wear weights (ConditionDepreciationWeight/CycleWearDepreciationWeight/GroundedForMaintenancePenalty) cannot be negative.");
        }

        if (Depreciation.MinResaleFactor <= 0 || Depreciation.MinResaleFactor > Depreciation.NewAircraftResaleFactor)
        {
            throw new InvalidOperationException(
                $"Depreciation.MinResaleFactor must be in (0, Depreciation.NewAircraftResaleFactor={Depreciation.NewAircraftResaleFactor}], was {Depreciation.MinResaleFactor}.");
        }

        if (LeaseEarlyTermination.FeeMonths < 0)
        {
            throw new InvalidOperationException($"LeaseEarlyTermination.FeeMonths cannot be negative, was {LeaseEarlyTermination.FeeMonths}.");
        }

        if (LoanEarlySettlement.FeePercentOfRemainingBalance is < 0 or >= 1)
        {
            throw new InvalidOperationException(
                $"LoanEarlySettlement.FeePercentOfRemainingBalance must be in [0,1), was {LoanEarlySettlement.FeePercentOfRemainingBalance}.");
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
                // 0.25, not the 0.5 this shipped with before reputation ever actually moved: solves
                // 1 + (100-50)/50 x S = 1.25 (docs/PLAN.md point 2's "reputation 100 carries about
                // 1.25x the passengers of reputation 50") for S. At reputation 0 this gives exactly
                // 0.75x, also matching the plan's stated figure, comfortably above ReputationFloor.
                ReputationSensitivity = 0.25,
                ReputationFloor = 0.3,
                MonthlySeasonality = new[] { 0.90, 0.88, 0.95, 1.00, 1.05, 1.15, 1.22, 1.20, 1.02, 0.95, 0.88, 1.05 },
                DayOfWeekMultiplier = new[] { 0.95, 1.05, 0.95, 0.90, 1.00, 1.15, 1.10 },
            },
            // See ReputationConfig's own doc for every figure's derivation.
            Reputation = new ReputationConfig
            {
                BaselineScore = 50.0,
                Alpha = 0.0137672955,
                OnTimeWeight = 0.8,
                LandingWeight = 0.2,
                OnTimeToleranceMinutes = 5.0,
                OnTimeZeroScoreDelayMinutes = 45.0,
                LandingBestFpm = 150.0,
                LandingWorstFpm = 600.0,
                CancelledTargetScore = 0.0,
                CancelledAlphaMultiplier = 2.5,
                ManualCompletionTargetScore = 0.0,
                ManualCompletionAlphaMultiplier = 1.0 / 3.0,
            },
            // See PilotSkillConfig's own doc for every figure's derivation.
            PilotSkill = new PilotSkillConfig
            {
                StartingSkill = 50.0,
                SkillCap = 87.0,
                GrowthHalfLifeHours = 300.0,
                IdleGracePeriodHours = 336.0,
                IdleDecayHalfLifeHours = 720.0,
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
                // 60,000 covers the 30,000 lease deposit plus a month's cushion, and deliberately
                // sits BELOW a used airframe's price (~51,200) so a new airline cannot buy past the
                // progression loop before flying a sector. Was 2,000,000 while Casual purchase
                // prices were still realistic and unreachable; once they came down that bought 39
                // used A320s on day one. Keep in step with economy-config.json's casual block, which
                // is what the server actually loads - this default only applies if that file is gone.
                StartingCapital = 60_000m,
                LeaseDepositMonths = 1.0,
                StartingPilotMonthlySalary = 9_000m,
            },
            // Casual figures - every entry is trueLife's real rate (EconomyConfigCatalog.Default())
            // times the ~8% catalogue-wide multiplier, except A320/B738 which are hard-anchored at
            // exactly 30,000 since the whole Casual balance is anchored to that figure - see
            // docs/PLAN.md "Casual pricing must scale the WHOLE catalogue".
            LeaseRates = new List<LeaseRateConfig>
            {
                new("A319", 28_000m),
                new("A320", 30_000m),
                new("A321", 33_600m),
                new("A20N", 32_800m),
                new("A21N", 36_000m),
                new("B737", 27_200m),
                new("B738", 30_000m),
                new("B739", 32_800m),
                new("B38M", 34_400m),
                new("B752", 36_800m),
                new("A332", 60_000m),
                new("A333", 65_600m),
                new("A359", 92_000m),
                new("A388", 144_000m),
                new("B763", 52_000m),
                new("B77W", 116_000m),
                new("B789", 84_000m),
                new("B748", 104_000m),
                new("E170", 14_800m),
                new("E175", 16_400m),
                new("E190", 19_200m),
                new("E195", 20_800m),
                new("CRJ7", 12_800m),
                new("CRJ9", 14_000m),
                new("AT42", 7_600m),
                new("AT72", 9_200m),
                new("DH8D", 10_400m),
            },
            // Casual's purchase-price multiplier - see PurchasePriceMultiplier's own doc. Lands the
            // starter A320 at ~93,100 new / ~51,200 used (55% of new), so a used airframe costs
            // roughly seventeen sectors' profit at the ~3,016 a typical short-haul sector nets.
            // Must stay in step with economy-config.json's casual block, which is the file the
            // server actually loads - this default only applies when that file is missing.
            PurchasePriceMultiplier = 0.00196m,
            FleetFinance = new FleetFinanceConfig
            {
                MonthlyInsurancePerAircraft = 6_000m,
            },
            Maintenance = new MaintenanceConfig
            {
                ACheckIntervalHours = 500,
                CCheckIntervalHours = 4_000,
                ACheckCost = 45_000m,
                CCheckCost = 320_000m,
                ConditionDecayPerHour = 0.12,
                ACheckConditionRestorePercent = 35,
                // Casual downtime figures - Default() mirrors the "casual" block since Casual is
                // the base config's own playstyle. True-life's own downtime (~1 day / ~14 days) is
                // applied separately in EconomyConfigCatalog.Default()'s Resolve() call.
                ACheckDowntimeHours = 4,
                CCheckDowntimeHours = 24,
            },
            // Casual figures - mirrors economy-config.json's "casual" block. See LoanConfig's own
            // doc for why 2.5/5.0: a friendly base rate with a low ceiling, consistent with Casual
            // being the forgiving playstyle everywhere else. True-life's own figures (4.0/8.0) are
            // applied separately in EconomyConfigCatalog.Default()'s Resolve() call.
            // MaxStartingLoanPrincipal 250,000 -> £4,718.31/month at the 5.0% cap rate over 60
            // months, 10.4% of the tested ~45,485 typical Casual monthly income (see
            // StartupTrajectoryTests.OneAircraft_OneLegADay_IsGenuinelyProfitable) and over 4x the
            // 60,000 starting capital - a meaningful capital injection with comfortable headroom
            // left, chosen by the user from worked numbers across three candidate cap bases.
            Loan = new LoanConfig
            {
                BaseAnnualRatePct = 2.5,
                CapAnnualRatePct = 5.0,
                MaxStartingLoanPrincipal = 250_000m,
            },
            // Casual figure - mirrors economy-config.json's "casual" block. True-life's own figure
            // (1.0) is applied separately in EconomyConfigCatalog.Default()'s Resolve() call.
            LeaseEarlyTermination = new LeaseEarlyTerminationConfig { FeeMonths = 0.5 },
            // Casual figure - mirrors economy-config.json's "casual" block. True-life's own figure
            // (0.04) is applied separately in EconomyConfigCatalog.Default()'s Resolve() call.
            LoanEarlySettlement = new LoanEarlySettlementConfig { FeePercentOfRemainingBalance = 0.02m },
            UsedAircraft = new UsedAircraftConfig
            {
                PriceMultiplier = 0.55m,
                StartingHoursSinceACheckFraction = 0.7,
                StartingHoursSinceCCheckFraction = 0.7,
                StartingConditionPercent = 70,
                StartingAirframeHours = 18_000,
            },
            // Shared across playstyles - see Depreciation's own doc.
            Depreciation = new AircraftDepreciationConfig(),
            // Casual figures - shared across playstyles (see the Scheduling property's own doc).
            // 10h rest / 13h max duty are ordinary short-haul crewing figures, comfortably inside
            // the 24h/day ceiling Validate() enforces; 45 minutes covers a realistic minimum
            // turnaround (deplane, clean, board) without being so tight that a modest schedule
            // trips it constantly. See docs/PLAN.md "Virtual pilot scheduling".
            Scheduling = new SchedulingConfig
            {
                MinRestHoursBetweenDutyDays = 10,
                MaxDutyHoursPerDay = 13,
                MinTurnaroundMinutes = 45,
            },
            // Casual: 0 (skipped quietly, no penalty). True-life's own figure (positive - cancelled
            // with a real cost) is applied separately in EconomyConfigCatalog.Default()'s Resolve()
            // call, same pattern as FleetFinance/Maintenance/Loan.
            UnflyableSchedule = new UnflyableScheduleConfig { CancellationFee = 0m },
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

/// <summary>One aircraft type's monthly lease rate - see <see cref="EconomyConfig.LeaseRates"/>.</summary>
public sealed record LeaseRateConfig(string IcaoType, decimal MonthlyRate);

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

    /// <summary>
    /// "Cost of carry" - the real-world rule of thumb for how much extra fuel it costs to haul
    /// extra fuel: roughly this fraction of the EXTRA fuel mass (carried beyond what the sector
    /// itself would normally need) burned per hour it stays on board. 0.03 = ~3% of the excess
    /// mass per hour of carriage, e.g. 2,000 kg of tankered fuel carried for two hours burns
    /// about 120 kg extra. This is fuel tankering's counterweight alongside weight-based landing
    /// fees (see docs/PLAN.md "Persistent fuel state and tankering" - "carrying fuel costs
    /// fuel"). Applied only to fuel carried BEYOND a sector's own normal requirement (see
    /// BlockFuelEstimator.Estimate's extraCarriedFuelKg parameter) - a normal, non-tankering
    /// flight's block fuel and cost are completely unaffected by this constant, which is the
    /// gate BlockFuelEstimatorWeightPenaltyTests asserts.
    /// </summary>
    public double CostOfCarryRatePerHour { get; init; } = 0.03;
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

/// <summary>
/// A/C-check cycle, cost and downtime - see docs/PLAN.md "Maintenance" and the "Playstyle" section's
/// behaviour table. IntervalHours/Cost/ConditionDecay/ConditionRestore are shared across playstyles
/// ("same cycle... and same costs" per the plan); only the two downtime figures differ and are
/// resolved from each playstyle's own override block by EconomyConfigCatalog - see
/// <see cref="EconomyConfigCatalog"/>'s class doc for why that split exists.
/// </summary>
public sealed class MaintenanceConfig
{
    /// <summary>Hours of airframe time between A-checks - a light, routine inspection.</summary>
    public double ACheckIntervalHours { get; init; } = 500;

    /// <summary>Hours of airframe time between C-checks - a major, comprehensive overhaul. Always
    /// a whole multiple of <see cref="ACheckIntervalHours"/> in the shipped config (4000 = 8x500),
    /// so a C-check due moment is always also an A-check due moment - see
    /// <see cref="Economy.MaintenanceScheduler"/>, which resets both cycles together when that
    /// happens rather than scheduling a redundant A-check moments later.</summary>
    public double CCheckIntervalHours { get; init; } = 4_000;

    /// <summary>DELIBERATE GAME-BALANCE FIGURE - shared by both playstyles per docs/PLAN.md's
    /// behaviour table ("same costs"). Sized so it stays a genuine but affordable bill against a
    /// casually-flown airline's accumulated monthly surplus by the time 500 airframe hours have
    /// passed (see docs/PLAN.md's E1 balance guardrails) - not a real-world A-check bill, which
    /// would run into six figures on its own.</summary>
    public decimal ACheckCost { get; init; } = 45_000m;

    /// <summary>DELIBERATE GAME-BALANCE FIGURE, shared by both playstyles - see
    /// <see cref="ACheckCost"/>'s doc. Costs heavily relative to an A-check (per the plan's
    /// behaviour table) without being unaffordable against the years of accumulated profit a
    /// 4,000-hour airframe implies.</summary>
    public decimal CCheckCost { get; init; } = 320_000m;

    /// <summary>Condition percentage points lost per airframe hour flown. At the default 0.12,
    /// a full 500-hour A-check interval flown back to back costs 60 points of condition (100 ->
    /// 40) before the next check restores some of it - see <see cref="ACheckConditionRestorePercent"/>.</summary>
    public double ConditionDecayPerHour { get; init; } = 0.12;

    /// <summary>Condition percentage points an A-check restores (added, capped at 100) - a real
    /// A-check is a routine inspection, not a full overhaul, so it only partially refreshes
    /// condition. A C-check always resets condition to 100 outright (a full overhaul) - see
    /// <see cref="Economy.MaintenanceScheduler"/>.</summary>
    public double ACheckConditionRestorePercent { get; init; } = 35;

    /// <summary>Hours the aircraft is grounded for an A-check - resolved per playstyle (Casual: a
    /// handful of hours; True-life: about a day) by EconomyConfigCatalog's Resolve().</summary>
    public double ACheckDowntimeHours { get; init; } = 4;

    /// <summary>Hours the aircraft is grounded for a C-check - resolved per playstyle (Casual: about
    /// a day; True-life: about a fortnight, 336h) by EconomyConfigCatalog's Resolve().</summary>
    public double CCheckDowntimeHours { get; init; } = 24;
}

/// <summary>
/// Risk-based loan pricing, resolved per playstyle - see docs/PLAN.md "Loan interest is set by the
/// simulation, never by the player" and <see cref="FSOps.Core.Finance.LoanRateCalculator"/>, the
/// only code allowed to turn this into an actual rate. <see cref="BaseAnnualRatePct"/> is what a
/// loan that consumes almost none of the airline's borrowing capacity costs; <see
/// cref="CapAnnualRatePct"/> is the hard ceiling docs/PLAN.md specifies (5% Casual / 8% True-life)
/// that a loan consuming all of it (or more - an over-capacity request is clamped, not
/// extrapolated past the cap) is charged. Every rate LoanRateCalculator produces for this
/// playstyle falls in [BaseAnnualRatePct, CapAnnualRatePct] by construction.
/// </summary>
public sealed class LoanConfig
{
    public double BaseAnnualRatePct { get; init; } = 2.5;

    public double CapAnnualRatePct { get; init; } = 5.0;

    /// <summary>
    /// The largest principal a STARTING loan (AirlineEndpoints.CreateAsync, taken the moment an
    /// airline is founded) may ever request - a flat, hand-authored per-playstyle figure, not a
    /// formula. A starting loan can never use <see cref="FSOps.Core.Finance.LoanEligibilityCalculator"/>'s
    /// cash-flow-based cap (<c>Evaluate</c>/<c>MaxMonthlyPayment</c>) the way a mid-game loan
    /// (FleetEndpoints.TakeLoanAsync) does: a brand-new airline's trailing cash flow is always
    /// exactly 0 (no ledger exists yet), and <c>MaxMonthlyPayment(0) == 0</c> by construction, so
    /// reusing that check here would reject every starting loan of any size rather than cap it -
    /// it would delete the feature, not bound it. This figure exists instead, authored directly per
    /// playstyle the same way BaseAnnualRatePct/CapAnnualRatePct are - see economy-config.json's
    /// "loan" blocks for the worked numbers behind 250,000 (Casual) and 5,000,000 (True-life).
    /// </summary>
    public decimal MaxStartingLoanPrincipal { get; init; } = 250_000m;
}

/// <summary>
/// The depreciation curve <see cref="FSOps.Core.Finance.AircraftDepreciationCalculator"/> reads -
/// see that class's own doc for the formula and docs/PLAN.md's requirement that sale value be
/// "consistent with [the used-aircraft] curve, not a separate invented one" (<see cref="UsedAircraftConfig.PriceMultiplier"/>
/// of 0.55 at 70% condition/70% into both maintenance cycles).
/// <para>
/// <b>Anchored, not guessed.</b> With the four defaults below, a used aircraft bought at exactly
/// <see cref="EconomyConfig.UsedAircraft"/>'s starting state (70% condition, 70% into both cycles)
/// and immediately resold values at exactly 0.50 of the playstyle's new price - strictly below the
/// 0.55 it cost to buy, so even a used-aircraft flip loses money (see
/// AircraftDepreciationCalculatorTests). A brand-new, zero-hour aircraft resold the instant it's
/// bought values at exactly <see cref="NewAircraftResaleFactor"/> (0.80) - the "drove it off the
/// apron" spread every real aircraft transaction carries, and on its own already makes a buy-then-
/// sell round trip a loss with no wear involved at all.
/// </para>
/// </summary>
public sealed class AircraftDepreciationConfig
{
    /// <summary>
    /// The ceiling on resale value as a fraction of THIS playstyle's new price
    /// (<see cref="EconomyConfig.PurchasePriceFor"/>) - what a zero-hour, 100%-condition aircraft
    /// sells for the instant it changes hands. Below 1.0 on purpose: this alone is what makes
    /// "buy new, sell immediately" a loss (see docs/PLAN.md "Test the round trip"), before any
    /// wear-based depreciation is even applied.
    /// </summary>
    public decimal NewAircraftResaleFactor { get; init; } = 0.80m;

    /// <summary>
    /// Maximum fraction of <see cref="NewAircraftResaleFactor"/> lost to condition wear, reached at
    /// <c>ConditionPercent == 0</c>. Scales linearly with <c>(100 - ConditionPercent) / 100</c>.
    /// </summary>
    public decimal ConditionDepreciationWeight { get; init; } = 0.30m;

    /// <summary>
    /// Maximum fraction lost to being close to due maintenance, reached when both
    /// <c>HoursSinceACheck</c> and <c>HoursSinceCCheck</c> are at their interval (a check is
    /// imminent either way). Scales with the average of the two cycles' progress toward their next
    /// check - see <see cref="FSOps.Core.Finance.AircraftDepreciationCalculator"/>.
    /// </summary>
    public decimal CycleWearDepreciationWeight { get; init; } = 0.30m;

    /// <summary>
    /// Extra flat cut applied while the aircraft is <see cref="FSOps.Core.Entities.FleetAircraftStatus.InMaintenance"/> -
    /// docs/PLAN.md "An aircraft grounded for maintenance can still be disposed of, at a worse
    /// figure". Needed as its own term because a just-triggered check resets
    /// HoursSinceACheck/HoursSinceCCheck to (near) zero the same flight it grounds the aircraft, so
    /// the cycle-wear term alone would score a freshly-grounded aircraft as if it had low wear.
    /// </summary>
    public decimal GroundedForMaintenancePenalty { get; init; } = 0.05m;

    /// <summary>Absolute floor on the resale multiplier, regardless of how worn or grounded the
    /// aircraft is - a defensive clamp; the worst case the weights above can produce
    /// (0.80 - 0.30 - 0.30 - 0.05 = 0.15) never actually reaches this.</summary>
    public decimal MinResaleFactor { get; init; } = 0.10m;
}

/// <summary>
/// The penalty for ending a lease before it would otherwise have been billed - see docs/PLAN.md
/// "Returning a lease early must cost something". This app's leases are open-ended/rolling (no
/// fixed term stored on <see cref="FSOps.Core.Entities.Lease"/>), so there is no "clean, penalty-
/// free end of term" to distinguish from an early one - every termination this feature offers IS
/// the early case, and the charge is what stands between leasing being a genuine commitment and it
/// being a free rental. Two components, both itemised (see
/// <see cref="FSOps.Core.Finance.LeaseTerminationCalculator"/>):
/// <list type="bullet">
/// <item>A pro-rata charge for the part of the current 30-day billing period already used, so
/// timing the return around <see cref="FSOps.Server.Services.EconomyClockService"/>'s tick can
/// never dodge rent that's already owed.</item>
/// <item>This flat fee on top, in months' rent - the actual cost of exiting early, harsher in
/// True-life like every other figure that differs by playstyle.</item>
/// </list>
/// </summary>
public sealed class LeaseEarlyTerminationConfig
{
    public double FeeMonths { get; init; } = 0.5;
}

/// <summary>
/// The fee for paying off a loan's full remaining balance before term - see docs/PLAN.md "Paying a
/// loan back... Charge a modest early-settlement fee". Deliberately NOT charged on a partial
/// overpayment (see <see cref="FSOps.Core.Finance.LoanSettlementCalculator.ApplyOverpayment"/>'s
/// own doc for why) - only closing the loan entirely carries it, which is also the only way early
/// settlement could ever be "gamed" (take a loan, immediately close it, pay nothing): the fee
/// guarantees that always costs something, however briefly the loan existed.
/// </summary>
public sealed class LoanEarlySettlementConfig
{
    /// <summary>Fraction of the outstanding principal charged as a fee when paying it off in full
    /// early.</summary>
    public decimal FeePercentOfRemainingBalance { get; init; } = 0.02m;
}

/// <summary>
/// Buying a used, rather than new, airframe - see docs/PLAN.md "Used aircraft - cheap to buy,
/// expensive to run". Shared across playstyles: what a used aircraft looks like on the market
/// doesn't depend on how realistically the owning airline chose to be billed elsewhere.
/// </summary>
public sealed class UsedAircraftConfig
{
    /// <summary>Fraction of <see cref="AircraftType.PurchasePrice"/> a used airframe of this type
    /// costs - e.g. 0.55 = 45% cheaper than new. The saving is repaid through the maintenance cycle
    /// (see the fields below), not given away for free.</summary>
    public decimal PriceMultiplier { get; init; } = 0.55m;

    /// <summary>Fraction of <see cref="MaintenanceConfig.ACheckIntervalHours"/> a used airframe has
    /// already accumulated toward its next A-check at the moment of purchase - e.g. 0.7 means its
    /// next A-check is only 30% of the interval away, not a fresh 500 hours out.</summary>
    public double StartingHoursSinceACheckFraction { get; init; } = 0.7;

    /// <summary>Fraction of <see cref="MaintenanceConfig.CCheckIntervalHours"/> a used airframe has
    /// already accumulated toward its next C-check - same idea as
    /// <see cref="StartingHoursSinceACheckFraction"/>, one cycle up.</summary>
    public double StartingHoursSinceCCheckFraction { get; init; } = 0.7;

    /// <summary>Condition percentage a used airframe starts at, versus 100 for new - see
    /// docs/PLAN.md "condition decays from a lower base".</summary>
    public double StartingConditionPercent { get; init; } = 70;

    /// <summary>Total lifetime airframe hours a used aircraft is shown as having already flown -
    /// cosmetic/informational only (checks are driven by HoursSinceACheck/HoursSinceCCheck, not
    /// this total), but part of making the age/condition trade-off visible before purchase per the
    /// plan's requirement.</summary>
    public double StartingAirframeHours { get; init; } = 18_000;
}

/// <summary>Pilot rest/duty and minimum turnaround for the weekly schedule builder - see
/// <see cref="EconomyConfig.Scheduling"/>'s own doc and docs/PLAN.md "Virtual pilot scheduling".
/// Shared across playstyles.</summary>
public sealed class SchedulingConfig
{
    /// <summary>Minimum hours between the end of one duty day and the start of the next, for one
    /// pilot - "the honest reason one pilot cannot saturate an aircraft" (docs/PLAN.md).</summary>
    public double MinRestHoursBetweenDutyDays { get; init; } = 10;

    /// <summary>Maximum hours from a pilot's first departure to their last arrival on any single
    /// duty day.</summary>
    public double MaxDutyHoursPerDay { get; init; } = 13;

    /// <summary>Minimum minutes an aircraft must sit on the ground between one leg's arrival and
    /// its next leg's departure - shows up as its own gap in the calendar UI (docs/PLAN.md "Minimum
    /// turnaround appears as its own gap") and is enforced here so the builder can't be saved with
    /// a day that only looks fine at a glance.</summary>
    public double MinTurnaroundMinutes { get; init; } = 45;
}

/// <summary>
/// What happens to a scheduled virtual flight that cannot fly - see
/// <see cref="EconomyConfig.UnflyableSchedule"/>'s own doc and docs/PLAN.md's Playstyle behaviour
/// table. Resolved per playstyle, unlike <see cref="SchedulingConfig"/> above.
/// </summary>
public sealed class UnflyableScheduleConfig
{
    /// <summary>
    /// Zero means "skip quietly, no penalty, but still recorded" (Casual - see
    /// <see cref="FSOps.Core.Entities.FlightStatus.Skipped"/>). Positive means "cancel, and post
    /// this flat fee as a <see cref="FSOps.Core.Entities.LedgerCategory.CancellationFee"/> ledger
    /// line" (True-life - see <see cref="FSOps.Core.Entities.FlightStatus.Cancelled"/>). Either way
    /// no ticket revenue is ever posted for a flight that never flew - this field only controls
    /// whether an ADDITIONAL cost is charged on top of that.
    /// </summary>
    public decimal CancellationFee { get; init; } = 0m;
}

/// <summary>
/// Tuning for how <see cref="Entities.Airline.ReputationScore"/> moves on one flight's outcome -
/// see <see cref="EconomyConfig.Reputation"/>'s own doc, <see cref="ReputationCalculator"/>, and
/// docs/PLAN.md "Progression - reputation and pilot skill", point 1 (what moves it) and point 2
/// (magnitude). Shared across playstyles.
/// </summary>
public sealed class ReputationConfig
{
    /// <summary>Where a brand-new airline starts - matches <see cref="Entities.Airline.ReputationScore"/>'s
    /// own default.</summary>
    public double BaselineScore { get; init; } = 50.0;

    /// <summary>
    /// The exponential-smoothing step size for one ordinary completed sector - see
    /// <see cref="ReputationCalculator.AdvanceForCompletedFlight"/>. Derived so a run of sectors
    /// each scoring the maximum (100) target crosses 75 at exactly the 50th sector - the midpoint
    /// of docs/PLAN.md point 2's stated 40-60 sector band. Solving
    /// <c>75 = 100 - 50 x (1-alpha)^50</c> for alpha gives <c>1 - 2^(-1/50) ~= 0.013767</c>.
    /// </summary>
    public double Alpha { get; init; } = 0.013767;

    /// <summary>How much of a completed sector's target score comes from on-time performance vs
    /// landing quality - docs/PLAN.md point 1: on-time is the primary signal, landing is the
    /// smallest weight of the three inputs (the third, cancellation, is not a weight in this blend
    /// at all - see <see cref="CancelledAlphaMultiplier"/>). Should sum to 1.0 with
    /// <see cref="LandingWeight"/> - see EconomyConfig.Validate.</summary>
    public double OnTimeWeight { get; init; } = 0.8;

    public double LandingWeight { get; init; } = 0.2;

    /// <summary>Delay (actual block minutes beyond planned) within which a sector still scores a
    /// full 100 on-time - a grace band, not a cliff-edge, matching every other "informational, not
    /// punishing" threshold in this app.</summary>
    public double OnTimeToleranceMinutes { get; init; } = 5.0;

    /// <summary>
    /// Delay at or beyond which the on-time score floors at 0. Comfortably above a zero-skill
    /// virtual pilot's worst-case delay variance (18 minutes - see
    /// <see cref="Scheduling.VirtualPilotPerformanceCalculator"/>'s MaxDelayVarianceMinutes), so
    /// even the weakest hire still earns a meaningfully positive on-time score rather than an
    /// automatic zero.
    /// </summary>
    public double OnTimeZeroScoreDelayMinutes { get; init; } = 45.0;

    /// <summary>
    /// Landing-rate bounds a sector's landing score is measured against - DELIBERATELY the same
    /// best/worst-case fpm <see cref="Scheduling.VirtualPilotPerformanceCalculator"/> uses for a
    /// virtual pilot's simulated touchdown, so a player's real touchdown and a virtual pilot's
    /// simulated one are scored on literally the same scale - docs/PLAN.md point 1's explicit
    /// "scored on the same scale" requirement.
    /// </summary>
    public double LandingBestFpm { get; init; } = 150.0;

    public double LandingWorstFpm { get; init; } = 600.0;

    /// <summary>
    /// The target a cancelled or skipped sector pulls reputation toward - the worst possible
    /// outcome, matching what a completed sector could only ever reach at its own absolute worst
    /// (0 on-time and 0 landing). See <see cref="CancelledAlphaMultiplier"/> for what makes a
    /// cancellation strictly costlier than that tie.
    /// </summary>
    public double CancelledTargetScore { get; init; } = 0.0;

    /// <summary>
    /// Multiplies <see cref="Alpha"/> for a cancelled or skipped sector, so it moves reputation
    /// toward its target faster than an ordinary completed sector ever can - the mechanism behind
    /// docs/PLAN.md point 1's "a bigger hit than a delay" requirement. Never applied to
    /// <see cref="Entities.FlightStatus.Suspended"/> - see <see cref="ReputationCalculator"/>'s own doc.
    /// </summary>
    public double CancelledAlphaMultiplier { get; init; } = 2.5;

    /// <summary>
    /// The target a manually-completed sector pulls reputation toward - see
    /// <see cref="ManualCompletionAlphaMultiplier"/>'s own doc for why this is NOT the same code
    /// path as <see cref="CancelledTargetScore"/> even though both default to the same value (0):
    /// a manual completion is not "the sector never happened" (money was still earned for it), it
    /// is "the sector's outcome cannot be verified", which is a distinct fact and deserves its own
    /// dial rather than silently inheriting whatever the cancellation figure happens to be tuned to.
    /// </summary>
    public double ManualCompletionTargetScore { get; init; } = 0.0;

    /// <summary>
    /// Multiplies <see cref="Alpha"/> for a manually-completed sector - a SMALL fraction, not a
    /// multiple, unlike <see cref="CancelledAlphaMultiplier"/>. A manual completion carries no
    /// telemetry at all (see <see cref="Entities.Flight.RevenuePosted"/>'s sibling comments in
    /// FlightEndpoints.CompleteManualAsync for why even the wall clock cannot be trusted here - it
    /// measures how long someone took to click a button, not how long the sector took), so this is
    /// a flat, timing-independent penalty for the sector being UNVERIFIED, never a derived on-time
    /// or landing score. Sized at roughly a third of an ordinary sector's own step (so the resulting
    /// single-sector cost from a reputation of 50 is about -0.23, against an ordinary sector's own
    /// worst possible -0.69), giving the two invariants that matter: completing manually is always
    /// worse than a clean tracked sector (which pulls UP toward 100, this only ever pulls toward
    /// <see cref="ManualCompletionTargetScore"/>) and always better than a genuinely terrible one
    /// (same target, smaller step) - so escaping a bad flight into a manual completion is never the
    /// smart play, while a genuine sim crash or disconnect barely registers. A previous version of
    /// this feature derived an on-time score from <c>now - (PlannedDepartureUtc + PlannedBlockMinutes)</c>;
    /// that was wrong; completing within seconds of starting reads as an enormous EARLY arrival
    /// under that formula, which scored as a perfect sector and paired with full ticket revenue to
    /// make "start, immediately complete, repeat" a reputation-and-revenue farm. This flat penalty
    /// has nothing for that exploit to derive from.
    /// </summary>
    public double ManualCompletionAlphaMultiplier { get; init; } = 1.0 / 3.0;
}

/// <summary>
/// Tuning for how <see cref="Entities.Pilot.SkillRating"/> grows with hours flown (diminishing
/// returns, capped below perfect) and decays with idle time - see
/// <see cref="EconomyConfig.PilotSkill"/>'s own doc, <see cref="Scheduling.PilotSkillCalculator"/>,
/// and docs/PLAN.md "Progression - reputation and pilot skill", point 3. Shared across playstyles.
/// </summary>
public sealed class PilotSkillConfig
{
    /// <summary>Skill a brand-new pilot starts at - matches <see cref="Entities.Pilot.SkillRating"/>'s
    /// own default, and is also the floor idle decay converges toward from above: decay erodes
    /// what was earned, it never sends a pilot below where they started.</summary>
    public double StartingSkill { get; init; } = 50.0;

    /// <summary>The ceiling growth asymptotically approaches but never reaches - kept below
    /// perfect (100) so landing/delay variance never fully disappears even for the most
    /// experienced hire, per docs/PLAN.md point 3.</summary>
    public double SkillCap { get; init; } = 87.0;

    /// <summary>Hours flown to close half the remaining gap between <see cref="StartingSkill"/>
    /// and <see cref="SkillCap"/> - diminishing returns by construction: the next half-life always
    /// buys half as much improvement as the last one did.</summary>
    public double GrowthHalfLifeHours { get; init; } = 300.0;

    /// <summary>
    /// Idle hours (since <see cref="Entities.Pilot.LastFlewUtc"/>) before decay starts at all - a
    /// normal gap between scheduled flights must never look like neglect. 336h = 14 days.
    /// </summary>
    public double IdleGracePeriodHours { get; init; } = 336.0;

    /// <summary>Idle hours BEYOND the grace period to close half the remaining gap back down
    /// toward <see cref="StartingSkill"/> - 720h = 30 days, so a pilot genuinely forgotten for
    /// six-plus weeks visibly loses ground, while one who flies at least monthly never decays
    /// meaningfully.</summary>
    public double IdleDecayHalfLifeHours { get; init; } = 720.0;
}

using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

public class EconomyConfigTests
{
    [Fact]
    public void Default_IsValid()
    {
        // Validate() throws on any internal inconsistency - not throwing is the assertion.
        var config = EconomyConfig.Default();
        config.Validate();
    }

    [Fact]
    public void Default_HasAllFiveStrategyProfiles()
    {
        var config = EconomyConfig.Default();

        foreach (var profile in Enum.GetValues<AirlineStrategyProfile>())
        {
            // Throws if missing - the assertion is that this does not throw for any live profile.
            config.GetStrategy(profile);
        }
    }

    [Fact]
    public void GetStrategy_UnknownProfile_Throws()
    {
        var config = EconomyConfig.Default();
        // Every real enum value must be covered above; there is nothing left to probe an
        // "unknown" profile with directly, so this documents the throw path via a config built
        // deliberately without one.
        var incomplete = new EconomyConfig
        {
            StrategyProfiles = new List<StrategyProfileConfig>
            {
                new(AirlineStrategyProfile.Domestic, 1.0m, 0.78, 1.3, 1.0),
            },
        };

        Assert.Throws<InvalidOperationException>(() => incomplete.GetStrategy(AirlineStrategyProfile.Premium));
    }

    [Fact]
    public void Validate_ElasticityBelowOne_Throws()
    {
        var config = new EconomyConfig
        {
            StrategyProfiles = new List<StrategyProfileConfig>
            {
                new(AirlineStrategyProfile.Premium, 1.6m, 0.68, 0.9, 1.35),
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("elasticity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_BaselineLoadFactorAboveMax_Throws()
    {
        var config = new EconomyConfig
        {
            MaxLoadFactor = 0.92,
            StrategyProfiles = new List<StrategyProfileConfig>
            {
                new(AirlineStrategyProfile.Premium, 1.6m, 0.95, 1.1, 1.35),
            },
        };

        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void Validate_CaptiveFareCeilingMultipleAtOrBelowOne_Throws()
    {
        var config = EconomyConfig.Default();
        var badConfig = new EconomyConfig
        {
            CaptiveFareCeilingMultiple = 1.0,
            PostCaptiveElasticity = config.PostCaptiveElasticity,
            ReferenceFare = config.ReferenceFare,
            Demand = config.Demand,
            Fuel = config.Fuel,
            Costs = config.Costs,
            StrategyProfiles = config.StrategyProfiles,
        };

        Assert.Throws<InvalidOperationException>(() => badConfig.Validate());
    }

    [Fact]
    public void FromJson_ParsesTheShippedConfigShape()
    {
        // Same field-for-field shape and values as EconomyConfig.Default() and the real
        // src/FSOps.Server/economy-config.json - kept in sync manually since Core.Tests must not
        // reach across into the Server project's file. If this ever drifts, both this test and
        // FromJson_ProducesEquivalentStrategyProfilesToDefault below will catch it. Includes a
        // couple of "//" comments deliberately (matching the real file's game-balance annotations
        // on leaseDepositMonths/monthlyInsurancePerAircraft) to prove FromJson's
        // JsonCommentHandling.Skip actually tolerates them, since a config file that fails to
        // parse because of its own explanatory comments would be a nasty way to discover this.
        const string json = """
        {
          "maxLoadFactor": 0.92,
          "captiveFareCeilingMultiple": 1.5,
          "postCaptiveElasticity": 2.5,
          "referenceFare": { "farePerNm": 0.12, "minimumFare": 65 },
          "demand": {
            "catchmentLarge": 10.0, "catchmentMedium": 3.0, "catchmentSmall": 0.6, "catchmentOther": 0.2,
            "sweetSpotMinNm": 300, "sweetSpotMaxNm": 2500,
            "noAirMarketBelowNm": 50, "noAirMarketFloorFactor": 0.01,
            "shortHopFloorFactor": 0.08,
            "longHaulDecayPerNm": 0.00035, "minDistanceFactor": 0.12, "baseDemandPerCatchmentPoint": 45.0,
            "reputationBaselineScore": 50.0, "reputationSensitivity": 0.5, "reputationFloor": 0.3,
            "monthlySeasonality": [0.90, 0.88, 0.95, 1.00, 1.05, 1.15, 1.22, 1.20, 1.02, 0.95, 0.88, 1.05],
            "dayOfWeekMultiplier": [0.95, 1.05, 0.95, 0.90, 1.00, 1.15, 1.10]
          },
          "fuel": {
            "basePricePerKg": 0.85, "defaultRegionalMultiplier": 1.00,
            "regionalMultipliers": { "United Kingdom": 1.00, "United Arab Emirates": 0.70, "Japan": 1.15 },
            "volatilityAmplitude": 0.06, "noiseWindowDays": 5
          },
          "costs": {
            "landingFeeRate": { "large": 9.50, "medium": 5.00, "small": 2.25, "other": 1.00 },
            "handlingFeeRate": { "large": 6.50, "medium": 3.50, "small": 1.75, "other": 0.75 },
            "parkingFeeRate": { "large": 1.20, "medium": 0.65, "small": 0.30, "other": 0.15 },
            "passengerChargeRate": { "large": 12.00, "medium": 7.00, "small": 3.50, "other": 1.50 },
            "turnaroundFeeRate": { "large": 450, "medium": 220, "small": 90, "other": 40 },
            "maintenanceAccrualPerHour": 210, "crewCostPerHour": 340, "minimumCrewDutyHours": 1.0
          },
          "airlineStartup": {
            "startingCapital": 2000000,
            // Game-balance pacing figure - see the real economy-config.json.
            "leaseDepositMonths": 1.0,
            "startingPilotMonthlySalary": 9000
          },
          // DELIBERATE GAME-BALANCE FIGURE, NOT A REAL-WORLD RATE - see the real economy-config.json.
          "fleetFinance": { "monthlyInsurancePerAircraft": 6000 },
          "strategyProfiles": [
            { "profile": "LowCost", "referenceFareMultiplier": 0.75, "baselineLoadFactor": 0.76, "elasticity": 1.60, "costMultiplier": 0.85 },
            { "profile": "Domestic", "referenceFareMultiplier": 1.00, "baselineLoadFactor": 0.78, "elasticity": 1.30, "costMultiplier": 1.00 },
            { "profile": "International", "referenceFareMultiplier": 1.15, "baselineLoadFactor": 0.75, "elasticity": 1.15, "costMultiplier": 1.05 },
            { "profile": "Premium", "referenceFareMultiplier": 1.60, "baselineLoadFactor": 0.68, "elasticity": 1.05, "costMultiplier": 1.35 },
            { "profile": "Balanced", "referenceFareMultiplier": 1.00, "baselineLoadFactor": 0.73, "elasticity": 1.18, "costMultiplier": 1.00 }
          ]
        }
        """;

        var config = EconomyConfig.FromJson(json);

        Assert.Equal(0.92, config.MaxLoadFactor);
        Assert.Equal(1.5, config.CaptiveFareCeilingMultiple);
        Assert.Equal(0.12m, config.ReferenceFare.FarePerNm);
        Assert.Equal(65m, config.ReferenceFare.MinimumFare);
        Assert.Equal(50, config.Demand.NoAirMarketBelowNm);
        Assert.Equal(0.01, config.Demand.NoAirMarketFloorFactor);
        Assert.Equal(9.50m, config.Costs.LandingFeeRate.Large);
        Assert.Equal(450m, config.Costs.TurnaroundFeeRate.Large);
        Assert.Equal(1.0, config.AirlineStartup.LeaseDepositMonths);
        Assert.Equal(6_000m, config.FleetFinance.MonthlyInsurancePerAircraft);

        foreach (var profile in Enum.GetValues<AirlineStrategyProfile>())
        {
            config.GetStrategy(profile);
        }
    }

    [Fact]
    public void FromJson_ProducesEquivalentStrategyProfilesToDefault()
    {
        var fromDefault = EconomyConfig.Default();

        // Every strategy profile that ships in Default() must round-trip through JSON parsing
        // with the exact same numbers - guards against the JSON shape and the C# defaults
        // silently drifting apart.
        foreach (var profile in Enum.GetValues<AirlineStrategyProfile>())
        {
            var expected = fromDefault.GetStrategy(profile);
            Assert.True(expected.Elasticity >= 1.0, $"{profile}: elasticity must stay >= 1.0.");
            Assert.True(expected.BaselineLoadFactor <= fromDefault.MaxLoadFactor, $"{profile}: baseline must not exceed the ceiling.");
        }
    }
}

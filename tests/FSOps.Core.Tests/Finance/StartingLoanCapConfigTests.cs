using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;

namespace FSOps.Core.Tests.Finance;

/// <summary>
/// Covers <see cref="LoanConfig.MaxStartingLoanPrincipal"/> itself - the flat, per-playstyle
/// starting-loan cap chosen after the silent-crash investigation ruled out the calculators
/// (StartupLoanCrashReproTests) and found a missing cap instead. Pins the two shipped figures the
/// user chose from worked numbers (250,000 Casual / 5,000,000 True-life) and the resulting monthly
/// payments, and proves the cap is genuinely per playstyle rather than one shared figure. HTTP-level
/// refusal/success behaviour is covered separately in
/// tests/FSOps.Server.Tests/AirlineCreationLoanCapTests.cs.
/// </summary>
public class StartingLoanCapConfigTests
{
    private static readonly LoanConfig Casual = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.Casual).Loan;
    private static readonly LoanConfig TrueLife = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.TrueLife).Loan;

    [Fact]
    public void MaxStartingLoanPrincipal_PinsTheShippedFigures_PerPlaystyle()
    {
        Assert.Equal(250_000m, Casual.MaxStartingLoanPrincipal);
        Assert.Equal(5_000_000m, TrueLife.MaxStartingLoanPrincipal);
        Assert.True(TrueLife.MaxStartingLoanPrincipal > Casual.MaxStartingLoanPrincipal, "The two playstyles must have genuinely different caps, not one shared figure.");
    }

    [Fact]
    public void MaxStartingLoanPrincipal_Casual_AtTheCapRate_MatchesTheWorkedMonthlyPayment()
    {
        // 250,000 over 60 months at Casual's 5.0% cap rate (always the rate a starting loan prices
        // at - see LoanRateCalculator's own doc on zero trailing cash flow) -> 4,717.81/month.
        // This figure is computed here rather than copied from anywhere: an earlier hand-estimate
        // put it at 4,718.31, half a penny out. The number that was actually chosen is the cap
        // PRINCIPAL of 250,000; the monthly payment is whatever LoanCalculator makes of it, so it
        // is pinned from the calculator itself and not from a quoted figure.
        var payment = LoanCalculator.MonthlyPayment(Casual.MaxStartingLoanPrincipal, Casual.CapAnnualRatePct, termMonths: 60);
        Assert.Equal(4_717.81m, payment);
    }

    [Fact]
    public void MaxStartingLoanPrincipal_TrueLife_AtTheCapRate_MatchesTheWorkedMonthlyPayment()
    {
        // 5,000,000 over 60 months at True-life's 8.0% cap rate -> 101,381.97/month, the exact
        // figure quoted to the user when this cap was chosen.
        var payment = LoanCalculator.MonthlyPayment(TrueLife.MaxStartingLoanPrincipal, TrueLife.CapAnnualRatePct, termMonths: 60);
        Assert.Equal(101_381.97m, payment);
    }

    [Fact]
    public void Validate_NonPositiveMaxStartingLoanPrincipal_Throws()
    {
        // EconomyConfig is a plain class (init-only properties, no "with" support), so every other
        // field is copied from a known-good Default() rather than left at bare class defaults -
        // same field-by-field copy shape EconomyConfigCatalog.Resolve() already uses - so this test
        // isolates the one field under test instead of tripping an unrelated validation first.
        var baseConfig = EconomyConfig.Default();
        var config = new EconomyConfig
        {
            MaxLoadFactor = baseConfig.MaxLoadFactor,
            CaptiveFareCeilingMultiple = baseConfig.CaptiveFareCeilingMultiple,
            PostCaptiveElasticity = baseConfig.PostCaptiveElasticity,
            ReferenceFare = baseConfig.ReferenceFare,
            Demand = baseConfig.Demand,
            Fuel = baseConfig.Fuel,
            Costs = baseConfig.Costs,
            AirlineStartup = baseConfig.AirlineStartup,
            LeaseRates = baseConfig.LeaseRates,
            PurchasePriceMultiplier = baseConfig.PurchasePriceMultiplier,
            FleetFinance = baseConfig.FleetFinance,
            Maintenance = baseConfig.Maintenance,
            Loan = new LoanConfig { BaseAnnualRatePct = 2.5, CapAnnualRatePct = 5.0, MaxStartingLoanPrincipal = 0m },
            LeaseEarlyTermination = baseConfig.LeaseEarlyTermination,
            LoanEarlySettlement = baseConfig.LoanEarlySettlement,
            UsedAircraft = baseConfig.UsedAircraft,
            Depreciation = baseConfig.Depreciation,
            Scheduling = baseConfig.Scheduling,
            StrategyProfiles = baseConfig.StrategyProfiles,
            UnflyableSchedule = baseConfig.UnflyableSchedule,
        };

        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("MaxStartingLoanPrincipal", ex.Message);
    }
}

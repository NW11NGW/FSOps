using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;

namespace FSOps.Core.Tests.Finance;

/// <summary>
/// The rate is ALWAYS computed, never supplied: one the player could set would be set to zero,
/// which makes borrowing free and turns loans into an exploit. These tests cover
/// the pure maths in isolation; FleetEndpointsTests covers the same guarantees end-to-end through
/// the actual HTTP-facing request types (which have no rate field at all).
/// </summary>
public class LoanRateCalculatorTests
{
    private static readonly LoanConfig Casual = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.Casual).Loan;
    private static readonly LoanConfig TrueLife = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.TrueLife).Loan;

    [Fact]
    public void ComputeAnnualRatePct_CasualPlaystyle_HasTheDocumentedBaseAndCap()
    {
        // Pins the actual shipped figures. The hard caps by playstyle are 5% in Casual and 8% in
        // True-life. If these ever drift, this test (not just the boundary tests
        // below) should catch it.
        Assert.Equal(5.0, Casual.CapAnnualRatePct);
        Assert.True(Casual.BaseAnnualRatePct > 0);
        Assert.True(Casual.BaseAnnualRatePct < Casual.CapAnnualRatePct);
    }

    [Fact]
    public void ComputeAnnualRatePct_TrueLifePlaystyle_HasTheDocumentedBaseAndCap()
    {
        Assert.Equal(8.0, TrueLife.CapAnnualRatePct);
        Assert.True(TrueLife.BaseAnnualRatePct > 0);
        Assert.True(TrueLife.BaseAnnualRatePct < TrueLife.CapAnnualRatePct);
    }

    [Fact]
    public void ComputeAnnualRatePct_TinyLoanAgainstHealthyCashFlow_IsCloseToTheBaseRate()
    {
        // A loan that barely touches the airline's borrowing capacity (huge cash flow, tiny
        // principal) should sit near the base rate, not anywhere near the cap.
        var rate = LoanRateCalculator.ComputeAnnualRatePct(
            principal: 100m, termMonths: 60, trailing30DayNetOperatingCashFlow: 10_000_000m, Casual);

        Assert.True(rate >= Casual.BaseAnnualRatePct);
        Assert.True(rate < Casual.BaseAnnualRatePct + 0.05, $"Expected a rate close to the base rate ({Casual.BaseAnnualRatePct}%), got {rate}%.");
    }

    [Fact]
    public void ComputeAnnualRatePct_LoanAtExactlyFullCapacity_EqualsTheCapRate()
    {
        // Constructed so the base-rate payment exactly equals the airline's max monthly payment -
        // borrowing to the limit reaches the cap, asserted at the exact boundary,
        // not inferred from the formula's shape.
        const decimal trailingCashFlow = 1_000_000m;
        var maxMonthlyPayment = LoanEligibilityCalculator.MaxMonthlyPayment(trailingCashFlow);

        // Solve for the principal whose base-rate payment equals maxMonthlyPayment by scaling a
        // known payment linearly (MonthlyPayment is linear in principal for a fixed rate/term).
        const decimal probePrincipal = 1_000_000m;
        var probePayment = LoanCalculator.MonthlyPayment(probePrincipal, Casual.BaseAnnualRatePct, termMonths: 60);
        var principalAtFullCapacity = probePrincipal * maxMonthlyPayment / probePayment;

        var rate = LoanRateCalculator.ComputeAnnualRatePct(principalAtFullCapacity, termMonths: 60, trailingCashFlow, Casual);

        Assert.Equal(Casual.CapAnnualRatePct, rate, precision: 2);
    }

    [Fact]
    public void ComputeAnnualRatePct_LoanFarBeyondCapacity_NeverExceedsTheCapRate_Casual()
    {
        // Absurdly oversized relative to capacity - the proportion-of-capacity term would be
        // enormous if left unclamped. The hard cap must hold regardless.
        var rate = LoanRateCalculator.ComputeAnnualRatePct(
            principal: 500_000_000m, termMonths: 12, trailing30DayNetOperatingCashFlow: 1_000m, Casual);

        Assert.True(rate <= Casual.CapAnnualRatePct);
        Assert.Equal(Casual.CapAnnualRatePct, rate);
    }

    [Fact]
    public void ComputeAnnualRatePct_LoanFarBeyondCapacity_NeverExceedsTheCapRate_TrueLife()
    {
        var rate = LoanRateCalculator.ComputeAnnualRatePct(
            principal: 500_000_000m, termMonths: 12, trailing30DayNetOperatingCashFlow: 1_000m, TrueLife);

        Assert.True(rate <= TrueLife.CapAnnualRatePct);
        Assert.Equal(TrueLife.CapAnnualRatePct, rate);
    }

    [Fact]
    public void ComputeAnnualRatePct_ZeroTrailingCashFlow_PricesAtTheCapRate()
    {
        // The exact situation a brand-new airline's starting loan is priced under
        // (AirlineEndpoints.CreateAsync passes 0m - there is no ledger yet) - see
        // LoanRateCalculator's own class doc. Any positive principal against zero capacity
        // consumes the whole band.
        var rate = LoanRateCalculator.ComputeAnnualRatePct(
            principal: 50_000m, termMonths: 60, trailing30DayNetOperatingCashFlow: 0m, Casual);

        Assert.Equal(Casual.CapAnnualRatePct, rate);
    }

    [Fact]
    public void ComputeAnnualRatePct_RateRisesMonotonically_AsBorrowedProportionOfCapacityIncreases()
    {
        const decimal trailingCashFlow = 200_000m;
        const int termMonths = 60;

        var principals = new[] { 10_000m, 100_000m, 500_000m, 1_000_000m, 5_000_000m };
        var rates = principals
            .Select(p => LoanRateCalculator.ComputeAnnualRatePct(p, termMonths, trailingCashFlow, Casual))
            .ToList();

        for (var i = 1; i < rates.Count; i++)
        {
            Assert.True(rates[i] >= rates[i - 1], $"Rate must not fall as the borrowed amount grows: {rates[i - 1]}% then {rates[i]}%.");
        }

        // And the smallest/largest ends of the scale should actually be different, not a flat
        // line pinned at the cap the whole way - otherwise "rises with amount" would be vacuously
        // true.
        Assert.True(rates[0] < rates[^1]);
        Assert.True(rates[0] >= Casual.BaseAnnualRatePct);
        Assert.True(rates[^1] <= Casual.CapAnnualRatePct);
    }

    [Fact]
    public void ComputeAnnualRatePct_EveryResult_FallsWithinBaseToCapRange_AcrossAWideSweep()
    {
        // Broad sweep across both playstyles and a wide range of principal/cash-flow combinations -
        // the rate must never leave [Base, Cap] for any of them.
        foreach (var rateConfig in new[] { Casual, TrueLife })
        {
            foreach (var principal in new[] { 1m, 1_000m, 1_000_000m, 1_000_000_000m })
            {
                foreach (var cashFlow in new[] { -50_000m, 0m, 10_000m, 10_000_000m })
                {
                    var rate = LoanRateCalculator.ComputeAnnualRatePct(principal, termMonths: 36, cashFlow, rateConfig);
                    Assert.True(rate >= rateConfig.BaseAnnualRatePct, $"{rate}% fell below base {rateConfig.BaseAnnualRatePct}% (principal={principal}, cashFlow={cashFlow}).");
                    Assert.True(rate <= rateConfig.CapAnnualRatePct, $"{rate}% exceeded cap {rateConfig.CapAnnualRatePct}% (principal={principal}, cashFlow={cashFlow}).");
                }
            }
        }
    }

    [Fact]
    public void ComputeAnnualRatePct_NegativeTrailingCashFlow_PricesAtTheCapRate()
    {
        // A loss-making trailing period gives zero borrowing capacity (LoanEligibilityCalculator
        // clamps it there) - same outcome as the zero-cash-flow case above.
        var rate = LoanRateCalculator.ComputeAnnualRatePct(
            principal: 20_000m, termMonths: 24, trailing30DayNetOperatingCashFlow: -75_000m, TrueLife);

        Assert.Equal(TrueLife.CapAnnualRatePct, rate);
    }
}

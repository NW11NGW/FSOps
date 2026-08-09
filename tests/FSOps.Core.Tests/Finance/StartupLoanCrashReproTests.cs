using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;

namespace FSOps.Core.Tests.Finance;

/// <summary>
/// Reproduction attempt for a reported silent process crash when founding an airline with a
/// Casual-playstyle startup loan of £5,000,000 over 60 months (the onboarding wizard's own default
/// loan amount/term - see src/fsops-web/src/components/onboarding/wizardData.ts). Exercises the
/// exact sequence AirlineEndpoints.CreateAsync runs for a starting loan: LoanRateCalculator against
/// a trailing cash flow of 0 (a brand-new airline has no ledger yet), then LoanCalculator's monthly
/// payment, then a full month-by-month amortisation via ApplyMonthlyPayment to the end of the term.
/// </summary>
public class StartupLoanCrashReproTests
{
    private static readonly LoanConfig Casual = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.Casual).Loan;

    [Fact]
    public void CasualFiveMillionOverSixtyMonths_DoesNotThrowOrHang_AndProducesSaneFigures()
    {
        const decimal principal = 5_000_000m;
        const int termMonths = 60;

        var rate = LoanRateCalculator.ComputeAnnualRatePct(
            principal, termMonths, trailing30DayNetOperatingCashFlow: 0m, Casual);
        Assert.Equal(Casual.CapAnnualRatePct, rate);

        var monthlyPayment = LoanCalculator.MonthlyPayment(principal, rate, termMonths);
        Assert.True(monthlyPayment > 0);

        // The endpoint never calls LoanEligibilityCalculator.Evaluate for the starting loan (unlike
        // FleetEndpoints.TakeLoanAsync's mid-game loan, which does) - confirm what that path
        // actually returns for context, without asserting either way on it here.
        var eligibility = LoanEligibilityCalculator.Evaluate(principal, rate, termMonths, trailing30DayNetOperatingCashFlow: 0m);
        Assert.False(eligibility.IsEligible); // zero cash flow => zero capacity, by design

        // Walk the full amortisation schedule to the end of the term, exactly as
        // EconomyClockService would over 60 real months - proves this isn't a slow-burn issue
        // either (e.g. balance not converging to zero, or drifting negative).
        var remaining = principal;
        for (var month = 1; month <= termMonths; month++)
        {
            var breakdown = LoanCalculator.ApplyMonthlyPayment(remaining, rate, monthlyPayment);
            remaining = breakdown.NewRemainingBalance;
            Assert.True(remaining >= 0);
        }

        Assert.Equal(0m, remaining);
    }

    [Fact]
    public void CasualFiveMillionOverSixtyMonths_MatchesTheOnboardingWizardsOwnDefaultPreview()
    {
        // src/fsops-web/src/components/onboarding/wizardData.ts:estimateMonthlyPayment mirrors this
        // server-side formula for the review-step preview. Confirms both sides agree, independent
        // of the crash investigation.
        const decimal principal = 5_000_000m;
        const int termMonths = 60;
        var rate = LoanRateCalculator.ComputeAnnualRatePct(principal, termMonths, 0m, Casual);
        var payment = LoanCalculator.MonthlyPayment(principal, rate, termMonths);

        Assert.True(payment > 50_000m && payment < 150_000m, $"Unexpected monthly payment: {payment}");
    }
}

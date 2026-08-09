using FSOps.Core.Economy;

namespace FSOps.Core.Finance;

/// <summary>
/// Pure early-lease-termination arithmetic - see docs/PLAN.md "Returning a lease early must cost
/// something" and <see cref="LeaseEarlyTerminationConfig"/>'s own doc for the two components this
/// combines. No I/O, no ambient clock read - the caller supplies <c>now</c> and the airline's
/// <c>EconomyState.LastProcessedUtc</c>/<c>periodLength</c>, same pattern as every other pure
/// calculator in this namespace (<see cref="LoanCalculator"/>, <see cref="AircraftDepreciationCalculator"/>).
/// </summary>
public static class LeaseTerminationCalculator
{
    /// <summary>
    /// <paramref name="lastProcessedUtc"/> is the airline's <c>EconomyState.LastProcessedUtc</c>
    /// watermark - the start of the current unbilled rolling period every active lease shares (see
    /// <c>EconomyClockService.PostMonthlyChargesAsync</c>, which bills every active lease the same
    /// <paramref name="monthlyRate"/> at the same period boundary regardless of when within it the
    /// lease itself started). The pro-rata charge is what's owed for the days elapsed in THIS
    /// unbilled period, so returning the aircraft one day after taking it out still costs a day's
    /// rent, not zero - closing the "lease, fly, return before the tick lands" loophole docs/PLAN.md
    /// calls out.
    /// </summary>
    public static LeaseTerminationSettlement ComputeSettlement(
        decimal monthlyRate,
        DateTimeOffset lastProcessedUtc,
        DateTimeOffset now,
        TimeSpan periodLength,
        LeaseEarlyTerminationConfig config)
    {
        if (monthlyRate < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monthlyRate), "monthlyRate cannot be negative.");
        }

        if (periodLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(periodLength), "periodLength must be positive.");
        }

        var elapsed = now - lastProcessedUtc;
        var daysIntoCurrentPeriod = Math.Clamp(elapsed.TotalDays, 0.0, periodLength.TotalDays);

        var proRataAmount = Math.Round(monthlyRate * (decimal)(daysIntoCurrentPeriod / periodLength.TotalDays), 2, MidpointRounding.AwayFromZero);
        var earlyTerminationFee = Math.Round(monthlyRate * (decimal)config.FeeMonths, 2, MidpointRounding.AwayFromZero);
        var totalCharge = proRataAmount + earlyTerminationFee;

        return new LeaseTerminationSettlement(daysIntoCurrentPeriod, proRataAmount, earlyTerminationFee, totalCharge);
    }
}

/// <summary>Result of <see cref="LeaseTerminationCalculator.ComputeSettlement"/>. Both amounts are
/// posted as separate itemised ledger lines by the caller - see docs/PLAN.md "Post it as an
/// itemised ledger line", not folded into one figure the player can't decompose.</summary>
public sealed record LeaseTerminationSettlement(
    double DaysIntoCurrentPeriod,
    decimal ProRataAmount,
    decimal EarlyTerminationFee,
    decimal TotalCharge);

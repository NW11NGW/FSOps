namespace FSOps.Core.Finance;

/// <summary>Standard amortizing-loan maths - pure, no I/O.</summary>
public static class LoanCalculator
{
    public static decimal MonthlyPayment(decimal principal, double annualRatePct, int termMonths)
    {
        if (termMonths <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(termMonths), "Term must be at least one month.");
        }

        var monthlyRate = annualRatePct / 100.0 / 12.0;

        if (monthlyRate <= 0)
        {
            return Math.Round(principal / termMonths, 2, MidpointRounding.AwayFromZero);
        }

        var factor = monthlyRate / (1 - Math.Pow(1 + monthlyRate, -termMonths));
        var payment = (double)principal * factor;
        return Math.Round((decimal)payment, 2, MidpointRounding.AwayFromZero);
    }
}

namespace FSOps.Core.Entities;

public class Loan
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public decimal Principal { get; set; }

    public double AnnualInterestRate { get; set; }

    public int TermMonths { get; set; }

    public decimal MonthlyPayment { get; set; }

    public decimal RemainingBalance { get; set; }

    public DateTimeOffset StartUtc { get; set; }

    public bool IsPaidOff { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}

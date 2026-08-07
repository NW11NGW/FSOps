namespace FSOps.Core.Entities;

/// <summary>
/// Append-only. The airline's cash balance is always SUM(Amount) over these rows for the
/// airline - there is deliberately no mutable balance column anywhere, so the number can
/// never drift out of sync with how it got there.
/// </summary>
public class LedgerTransaction
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public DateTimeOffset Utc { get; set; }

    public LedgerCategory Category { get; set; }

    /// <summary>Signed - positive is money in, negative is money out.</summary>
    public decimal Amount { get; set; }

    public Guid? FlightId { get; set; }

    public string Description { get; set; } = string.Empty;
}

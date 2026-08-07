namespace FSOps.Core.Airlines;

/// <summary>Constants used when a new airline is created - kept in one place so the starting
/// economics stay easy to tune and to reason about together.</summary>
public static class AirlineCreationDefaults
{
    /// <summary>
    /// Starting capital granted to every new airline, in the base currency unit. Set well above
    /// the most expensive starter aircraft (currently ~118,000,000 for an A321) so a new airline
    /// can always afford its first aircraft and still have working capital left for fuel,
    /// landing fees and crew before any ticket revenue comes in.
    /// </summary>
    public const decimal StartingCapital = 150_000_000m;

    /// <summary>Monthly salary paid to the player's own starting pilot.</summary>
    public const decimal StartingPilotMonthlySalary = 9_000m;
}

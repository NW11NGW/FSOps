namespace FSOps.Core.Money;

public enum CurrencySymbolPlacement
{
    Before,
    After,
}

/// <summary>
/// One entry in the supported-currency catalogue. DisplayRate converts a base-unit amount into
/// this currency for DISPLAY ONLY - see MoneyFormatter. FSOps is a game economy, not a forex
/// simulator, so these rates are fixed and never fetched or refreshed; changing a user's display
/// currency must never alter what is actually stored in the ledger.
/// </summary>
public sealed record Currency(
    string Code,
    string Symbol,
    string EnglishName,
    CurrencySymbolPlacement Placement,
    int DecimalPlaces,
    decimal DisplayRate);

public static class CurrencyCatalogue
{
    /// <summary>
    /// The base unit every LedgerTransaction.Amount is stored in. Pegged 1:1 to GBP - every
    /// other currency's DisplayRate is expressed relative to this.
    /// </summary>
    public const string BaseCurrencyCode = "GBP";

    public static readonly IReadOnlyList<Currency> All = new List<Currency>
    {
        new("GBP", "£", "British Pound", CurrencySymbolPlacement.Before, 2, 1.00m),
        new("USD", "$", "US Dollar", CurrencySymbolPlacement.Before, 2, 1.27m),
        new("EUR", "€", "Euro", CurrencySymbolPlacement.Before, 2, 1.17m),
        new("CAD", "$", "Canadian Dollar", CurrencySymbolPlacement.Before, 2, 1.71m),
        new("AUD", "$", "Australian Dollar", CurrencySymbolPlacement.Before, 2, 1.93m),
        new("JPY", "¥", "Japanese Yen", CurrencySymbolPlacement.Before, 0, 190.00m),
        new("CHF", "Fr", "Swiss Franc", CurrencySymbolPlacement.Before, 2, 1.14m),
        new("SEK", "kr", "Swedish Krona", CurrencySymbolPlacement.After, 2, 13.60m),
        new("NOK", "kr", "Norwegian Krone", CurrencySymbolPlacement.After, 2, 13.90m),
        new("PLN", "zł", "Polish Zloty", CurrencySymbolPlacement.After, 2, 5.05m),
        new("BRL", "R$", "Brazilian Real", CurrencySymbolPlacement.Before, 2, 6.50m),
        new("ZAR", "R", "South African Rand", CurrencySymbolPlacement.Before, 2, 23.50m),
    }.AsReadOnly();

    public static Currency? TryGet(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = code.Trim().ToUpperInvariant();
        return All.FirstOrDefault(c => c.Code == normalized);
    }
}

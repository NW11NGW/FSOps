using System.Globalization;

namespace FSOps.Core.Money;

/// <summary>
/// Pure display-only conversion and formatting. Never use these to decide what to store - every
/// LedgerTransaction.Amount stays in the base unit forever; only a response DTO or the UI layer
/// should ever run an amount through here.
/// </summary>
public static class MoneyFormatter
{
    public static decimal ConvertFromBase(decimal baseAmount, Currency currency)
    {
        var converted = baseAmount * currency.DisplayRate;
        return Math.Round(converted, currency.DecimalPlaces, MidpointRounding.AwayFromZero);
    }

    public static string Format(decimal baseAmount, Currency currency)
    {
        var converted = ConvertFromBase(baseAmount, currency);
        var numberText = converted.ToString("N" + currency.DecimalPlaces, CultureInfo.InvariantCulture);

        return currency.Placement == CurrencySymbolPlacement.Before
            ? currency.Symbol + numberText
            : numberText + currency.Symbol;
    }
}

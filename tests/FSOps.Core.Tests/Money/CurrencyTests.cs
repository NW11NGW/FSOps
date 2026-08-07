using FSOps.Core.Money;

namespace FSOps.Core.Tests.Money;

public class CurrencyTests
{
    [Fact]
    public void CurrencyCatalogue_ContainsAllRequiredCurrencies()
    {
        var expectedCodes = new[] { "GBP", "USD", "EUR", "CAD", "AUD", "JPY", "CHF", "SEK", "NOK", "PLN", "BRL", "ZAR" };

        foreach (var code in expectedCodes)
        {
            Assert.NotNull(CurrencyCatalogue.TryGet(code));
        }
    }

    [Fact]
    public void TryGet_IsCaseInsensitiveAndTrims()
    {
        Assert.NotNull(CurrencyCatalogue.TryGet(" gbp "));
        Assert.NotNull(CurrencyCatalogue.TryGet("Usd"));
    }

    [Fact]
    public void TryGet_UnknownCode_ReturnsNull()
    {
        Assert.Null(CurrencyCatalogue.TryGet("XXX"));
        Assert.Null(CurrencyCatalogue.TryGet(null));
        Assert.Null(CurrencyCatalogue.TryGet(""));
    }

    [Fact]
    public void Format_Gbp_UsesTwoDecimalPlacesAndSymbolBefore()
    {
        var gbp = CurrencyCatalogue.TryGet("GBP")!;

        Assert.Equal("£1,234.50", MoneyFormatter.Format(1234.5m, gbp));
    }

    [Fact]
    public void Format_Jpy_UsesZeroDecimalPlaces()
    {
        var jpy = CurrencyCatalogue.TryGet("JPY")!;

        // 1000 base units at the fixed 190.00 rate converts to 190,000 yen with no decimal point.
        var formatted = MoneyFormatter.Format(1000m, jpy);

        Assert.Equal("¥190,000", formatted);
        Assert.DoesNotContain(".", formatted);
    }

    [Fact]
    public void Format_CurrencyWithSymbolAfter_PlacesSymbolAfterTheNumber()
    {
        var sek = CurrencyCatalogue.TryGet("SEK")!;

        var formatted = MoneyFormatter.Format(100m, sek);

        Assert.EndsWith("kr", formatted);
    }

    [Fact]
    public void ConvertFromBase_DoesNotMutateBaseAmount()
    {
        var usd = CurrencyCatalogue.TryGet("USD")!;
        var baseAmount = 500m;

        var converted = MoneyFormatter.ConvertFromBase(baseAmount, usd);

        Assert.Equal(500m, baseAmount);
        Assert.Equal(635.00m, converted);
    }

    [Fact]
    public void ConvertFromBase_BaseCurrencyGbp_IsUnchanged()
    {
        var gbp = CurrencyCatalogue.TryGet(CurrencyCatalogue.BaseCurrencyCode)!;

        Assert.Equal(999.99m, MoneyFormatter.ConvertFromBase(999.99m, gbp));
    }
}

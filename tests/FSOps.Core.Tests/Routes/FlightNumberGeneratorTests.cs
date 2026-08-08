using FSOps.Core.Routes;

namespace FSOps.Core.Tests.Routes;

public class FlightNumberGeneratorTests
{
    [Fact]
    public void SuggestOutbound_NoExistingRoutes_ReturnsOddNumber()
    {
        var suggested = FlightNumberGenerator.SuggestOutbound("FSO", existingFlightNumbers: []);

        Assert.True(int.Parse(suggested) % 2 == 1);
    }

    [Fact]
    public void SuggestOutbound_IsDeterministicForSameIcaoCode()
    {
        var first = FlightNumberGenerator.SuggestOutbound("FSO", existingFlightNumbers: []);
        var second = FlightNumberGenerator.SuggestOutbound("FSO", existingFlightNumbers: []);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SuggestOutbound_DifferentIcaoCodes_CanProduceDifferentSeries()
    {
        var a = FlightNumberGenerator.SuggestOutbound("AAA", existingFlightNumbers: []);
        var b = FlightNumberGenerator.SuggestOutbound("ZZZ", existingFlightNumbers: []);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SuggestOutbound_SkipsNumbersAlreadyTaken()
    {
        var first = FlightNumberGenerator.SuggestOutbound("FSO", existingFlightNumbers: []);
        var firstNumber = int.Parse(first);

        var second = FlightNumberGenerator.SuggestOutbound("FSO", existingFlightNumbers: [first]);

        Assert.NotEqual(first, second);
        Assert.Equal(firstNumber + 2, int.Parse(second));
    }

    [Fact]
    public void SuggestOutbound_SkipsMultipleConsecutiveTakenNumbers()
    {
        var first = int.Parse(FlightNumberGenerator.SuggestOutbound("FSO", existingFlightNumbers: []));
        var taken = new[] { first.ToString(), (first + 2).ToString(), (first + 4).ToString() };

        var suggested = FlightNumberGenerator.SuggestOutbound("FSO", taken);

        Assert.Equal(first + 6, int.Parse(suggested));
    }

    [Fact]
    public void SuggestReturn_OddOutbound_SuggestsNextEvenNumber()
    {
        var returnNumber = FlightNumberGenerator.SuggestReturn("FSO", "101", existingFlightNumbers: []);

        Assert.Equal("102", returnNumber);
    }

    [Fact]
    public void SuggestReturn_WithLetterSuffixOnOutbound_ParsesNumericPart()
    {
        var returnNumber = FlightNumberGenerator.SuggestReturn("FSO", "101A", existingFlightNumbers: []);

        Assert.Equal("102", returnNumber);
    }

    [Fact]
    public void SuggestReturn_PreferredNumberTaken_SkipsToNextFreeEven()
    {
        var returnNumber = FlightNumberGenerator.SuggestReturn("FSO", "101", existingFlightNumbers: ["102"]);

        Assert.Equal("104", returnNumber);
    }

    [Fact]
    public void SuggestReturn_UnparsableOutbound_FallsBackToOutboundSuggestion()
    {
        var fallback = FlightNumberGenerator.SuggestOutbound("FSO", existingFlightNumbers: []);
        var returnNumber = FlightNumberGenerator.SuggestReturn("FSO", outboundFlightNumber: null, existingFlightNumbers: []);

        Assert.Equal(fallback, returnNumber);
    }

    [Theory]
    [InlineData("101", true)]
    [InlineData("1", true)]
    [InlineData("9999", true)]
    [InlineData("204A", true)]
    [InlineData("", false)]
    [InlineData("AB101", false)]
    [InlineData("10199", false)]
    [InlineData("101AB", false)]
    [InlineData("101 ", false)]
    public void IsValidFormat_MatchesDigitsWithOptionalLetterSuffix(string value, bool expected)
    {
        Assert.Equal(expected, FlightNumberGenerator.IsValidFormat(value));
    }
}

using FSOps.Core.Airports;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Airports;

public class AirportSearchRankingTests
{
    private static Airport MakeAirport(
        string icao,
        string? iata,
        string name,
        string municipality = "",
        AirportSizeCategory size = AirportSizeCategory.Medium,
        bool scheduledService = false)
    {
        return new Airport
        {
            Icao = icao,
            Iata = iata,
            Name = name,
            Municipality = municipality,
            Country = "GB",
            SizeCategory = size,
            HasScheduledService = scheduledService,
        };
    }

    [Fact]
    public void Classify_ExactIcaoMatch_ReturnsExactIcao()
    {
        var airport = MakeAirport("EGLL", "LHR", "London Heathrow Airport");

        Assert.Equal(AirportMatchKind.ExactIcao, AirportSearchRanking.Classify(airport, "EGLL"));
        Assert.Equal(AirportMatchKind.ExactIcao, AirportSearchRanking.Classify(airport, "egll"));
    }

    [Fact]
    public void Classify_IcaoPrefixMatch_ReturnsIcaoPrefix()
    {
        var airport = MakeAirport("EGKK", "LGW", "London Gatwick Airport");

        Assert.Equal(AirportMatchKind.IcaoPrefix, AirportSearchRanking.Classify(airport, "EGK"));
    }

    [Fact]
    public void Classify_ExactIataMatch_ReturnsExactIata()
    {
        var airport = MakeAirport("KJFK", "JFK", "John F Kennedy International Airport");

        Assert.Equal(AirportMatchKind.ExactIata, AirportSearchRanking.Classify(airport, "JFK"));
    }

    [Fact]
    public void Classify_NameContains_ReturnsNameOrMunicipalityContains()
    {
        var airport = MakeAirport("EGLL", "LHR", "London Heathrow Airport", "London");

        Assert.Equal(AirportMatchKind.NameOrMunicipalityContains, AirportSearchRanking.Classify(airport, "Heathrow"));
        Assert.Equal(AirportMatchKind.NameOrMunicipalityContains, AirportSearchRanking.Classify(airport, "London"));
    }

    [Fact]
    public void Classify_NoMatch_ReturnsNoMatch()
    {
        var airport = MakeAirport("EGLL", "LHR", "London Heathrow Airport");

        Assert.Equal(AirportMatchKind.NoMatch, AirportSearchRanking.Classify(airport, "Paris"));
    }

    [Fact]
    public void Order_ExactIcaoBeatsNameContainsEvenForSmallAirport()
    {
        // A tiny airport whose ICAO literally is "HEATHROW" should still lose to a big airport
        // whose name merely contains the word - exact ICAO always wins the top spot.
        var exactIcao = MakeAirport("XXXX", null, "Somewhere Small Field", size: AirportSizeCategory.Small);
        var nameMatch = MakeAirport("EGLL", "LHR", "Heathrow International", size: AirportSizeCategory.Large, scheduledService: true);

        var ordered = AirportSearchRanking.Order(new[] { nameMatch, exactIcao }, "XXXX").ToList();

        Assert.Single(ordered);
        Assert.Equal("XXXX", ordered[0].Icao);
    }

    [Fact]
    public void Order_WithinSameMatchKind_PrefersLargerScheduledAirports()
    {
        var smallField = MakeAirport("EGXA", null, "Ashcombe Small Field", size: AirportSizeCategory.Small);
        var largeHub = MakeAirport("EGLL", "LHR", "London Heathrow Airport", size: AirportSizeCategory.Large, scheduledService: true);
        var mediumField = MakeAirport("EGXB", null, "Bicester Medium Field", size: AirportSizeCategory.Medium);

        var ordered = AirportSearchRanking.Order(new[] { smallField, largeHub, mediumField }, "EG").ToList();

        Assert.Equal(new[] { "EGLL", "EGXB", "EGXA" }, ordered.Select(a => a.Icao));
    }

    [Fact]
    public void Order_FiltersOutNonMatches()
    {
        var match = MakeAirport("EGLL", "LHR", "London Heathrow Airport");
        var nonMatch = MakeAirport("LFPG", "CDG", "Charles de Gaulle Airport");

        var ordered = AirportSearchRanking.Order(new[] { match, nonMatch }, "EGLL").ToList();

        Assert.Single(ordered);
        Assert.Equal("EGLL", ordered[0].Icao);
    }

    [Fact]
    public void Order_HeathrowSearch_FindsLondonHeathrowByIcao()
    {
        var heathrow = MakeAirport("EGLL", "LHR", "London Heathrow Airport", "London", AirportSizeCategory.Large, true);
        var gatwick = MakeAirport("EGKK", "LGW", "London Gatwick Airport", "London", AirportSizeCategory.Large, true);

        var ordered = AirportSearchRanking.Order(new[] { gatwick, heathrow }, "EGLL").ToList();

        Assert.Equal("EGLL", ordered.First().Icao);
    }
}

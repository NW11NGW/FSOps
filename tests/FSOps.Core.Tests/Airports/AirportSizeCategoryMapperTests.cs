using FSOps.Core.Airports;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Airports;

public class AirportSizeCategoryMapperTests
{
    [Theory]
    [InlineData("large_airport", AirportSizeCategory.Large)]
    [InlineData("medium_airport", AirportSizeCategory.Medium)]
    [InlineData("small_airport", AirportSizeCategory.Small)]
    [InlineData("heliport", AirportSizeCategory.Heliport)]
    [InlineData("seaplane_base", AirportSizeCategory.Seaplane)]
    [InlineData("closed", AirportSizeCategory.Closed)]
    public void Map_KnownOurAirportsType_ReturnsExpectedCategory(string input, AirportSizeCategory expected)
    {
        Assert.Equal(expected, AirportSizeCategoryMapper.Map(input));
    }

    [Theory]
    [InlineData("LARGE_AIRPORT")]
    [InlineData("Large_Airport")]
    [InlineData("  large_airport  ")]
    public void Map_IsCaseInsensitiveAndTrimsWhitespace(string input)
    {
        Assert.Equal(AirportSizeCategory.Large, AirportSizeCategoryMapper.Map(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("balloonport")]
    [InlineData("some_unrecognised_type")]
    public void Map_UnknownOrMissingType_FallsBackToSmall(string? input)
    {
        Assert.Equal(AirportSizeCategory.Small, AirportSizeCategoryMapper.Map(input));
    }
}

using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

public class AircraftTypeMatcherTests
{
    private const string A320FamilyPatterns = "[\"A31[89]\",\"A32[01]\",\"A20N\",\"A19N\",\"A21N\"]";
    private const string B737FamilyPatterns = "[\"B73[6-9]\",\"737\",\"B738\"]";

    [Fact]
    public void IsMatch_TitleMatchesFamilyPattern_ReturnsTrue()
    {
        Assert.True(AircraftTypeMatcher.IsMatch(A320FamilyPatterns, "Airbus A320neo Asobo", "A20N"));
    }

    [Fact]
    public void IsMatch_DifferentFamilyEntirely_ReturnsFalse()
    {
        Assert.False(AircraftTypeMatcher.IsMatch(A320FamilyPatterns, "Boeing 737-800", "B738"));
    }

    [Fact]
    public void IsMatch_VariantWithinSameFamily_StillMatches()
    {
        // A 737-800 flown on a route expecting a 737-700 is still a family-level match.
        Assert.True(AircraftTypeMatcher.IsMatch(B737FamilyPatterns, "Boeing 737-800 Asobo", "B738"));
    }

    [Fact]
    public void IsMatch_IsCaseInsensitive()
    {
        Assert.True(AircraftTypeMatcher.IsMatch(A320FamilyPatterns, "airbus a320neo asobo", "a20n"));
    }

    [Fact]
    public void IsMatch_UnrecognisedAircraft_ReturnsFalse()
    {
        Assert.False(AircraftTypeMatcher.IsMatch(A320FamilyPatterns, "Some Homebuilt Kitplane", "UNKNOWN"));
    }

    [Fact]
    public void IsMatch_EmptyTitleAndModel_ReturnsFalseInsteadOfThrowing()
    {
        Assert.False(AircraftTypeMatcher.IsMatch(A320FamilyPatterns, string.Empty, null));
    }

    [Fact]
    public void IsMatch_MalformedPatternJson_ReturnsFalseInsteadOfThrowing()
    {
        Assert.False(AircraftTypeMatcher.IsMatch("not valid json", "Airbus A320neo Asobo", "A20N"));
    }

    [Fact]
    public void IsMatch_MalformedRegexPattern_IsSkippedRatherThanThrowing()
    {
        var patterns = "[\"[unterminated\", \"A32[01]\"]";
        Assert.True(AircraftTypeMatcher.IsMatch(patterns, "Airbus A320", "A320"));
    }

    [Fact]
    public void HasAircraftData_BothTitleAndAtcModelBlank_ReturnsFalse()
    {
        Assert.False(AircraftTypeMatcher.HasAircraftData(string.Empty, null));
        Assert.False(AircraftTypeMatcher.HasAircraftData(null, null));
        Assert.False(AircraftTypeMatcher.HasAircraftData("   ", "   "));
    }

    [Fact]
    public void HasAircraftData_TitlePresent_ReturnsTrue()
    {
        Assert.True(AircraftTypeMatcher.HasAircraftData("Airbus A320neo Asobo", null));
    }

    [Fact]
    public void HasAircraftData_OnlyAtcModelPresent_ReturnsTrue()
    {
        Assert.True(AircraftTypeMatcher.HasAircraftData(string.Empty, "A20N"));
    }
}

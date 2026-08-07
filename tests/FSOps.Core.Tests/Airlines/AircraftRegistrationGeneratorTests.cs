using FSOps.Core.Airlines;

namespace FSOps.Core.Tests.Airlines;

public class AircraftRegistrationGeneratorTests
{
    [Fact]
    public void Generate_UkCountryCode_UsesGPrefix()
    {
        var registration = AircraftRegistrationGenerator.Generate("GB", "FOA");

        Assert.StartsWith("G-", registration);
    }

    [Fact]
    public void Generate_UsCountryCode_UsesNPrefixWithoutHyphen()
    {
        var registration = AircraftRegistrationGenerator.Generate("US", "FOA");

        Assert.StartsWith("N", registration);
        Assert.DoesNotContain("-", registration);
    }

    [Fact]
    public void Generate_UnmappedCountryCode_FallsBackToGenericPrefix()
    {
        var registration = AircraftRegistrationGenerator.Generate("ZZ", "FOA");

        Assert.StartsWith("FS-", registration);
    }

    [Fact]
    public void Generate_NullCountryCode_FallsBackToGenericPrefix()
    {
        var registration = AircraftRegistrationGenerator.Generate(null, "FOA");

        Assert.StartsWith("FS-", registration);
    }

    [Fact]
    public void Generate_IsCaseInsensitiveOnCountryCode()
    {
        var registration = AircraftRegistrationGenerator.Generate("gb", "FOA");

        Assert.StartsWith("G-", registration);
    }
}

using FSOps.Core.Airlines;

namespace FSOps.Core.Tests.Airlines;

public class AircraftRegistrationGeneratorTests
{
    [Fact]
    public void Generate_UkCountryCode_UsesGPrefixPlusFourLetters()
    {
        var registration = AircraftRegistrationGenerator.Generate("GB", new Random(1));

        Assert.StartsWith("G-", registration);
        Assert.Equal(6, registration.Length); // "G-" + 4 letters
        Assert.All(registration[2..], c => Assert.True(c is >= 'A' and <= 'Z'));
    }

    [Fact]
    public void Generate_GermanyCountryCode_UsesDAPrefixPlusThreeLetters()
    {
        var registration = AircraftRegistrationGenerator.Generate("DE", new Random(2));

        Assert.StartsWith("D-A", registration);
        Assert.Equal(6, registration.Length); // "D-A" + 3 letters
    }

    [Fact]
    public void Generate_FranceCountryCode_UsesFGPrefixPlusThreeLetters()
    {
        var registration = AircraftRegistrationGenerator.Generate("FR", new Random(3));

        Assert.StartsWith("F-G", registration);
        Assert.Equal(6, registration.Length);
    }

    [Fact]
    public void Generate_IrelandCountryCode_UsesEIPrefixPlusThreeLetters()
    {
        var registration = AircraftRegistrationGenerator.Generate("IE", new Random(4));

        Assert.StartsWith("EI-", registration);
        Assert.Equal(6, registration.Length);
    }

    [Fact]
    public void Generate_NetherlandsCountryCode_UsesPHPrefixPlusThreeLetters()
    {
        var registration = AircraftRegistrationGenerator.Generate("NL", new Random(5));

        Assert.StartsWith("PH-", registration);
        Assert.Equal(6, registration.Length);
    }

    [Fact]
    public void Generate_SpainCountryCode_UsesECPrefixPlusThreeLetters()
    {
        var registration = AircraftRegistrationGenerator.Generate("ES", new Random(6));

        Assert.StartsWith("EC-", registration);
        Assert.Equal(6, registration.Length);
    }

    /// <summary>
    /// The bug this whole feature exists to fix: a US registration is NOT "prefix + letters" - it
    /// is "N" + 1-5 characters that must START with a digit. "NOLAF" (what the old airline-code-
    /// derived generator would have produced) is invalid; this asserts the shape is actually right.
    /// </summary>
    [Fact]
    public void Generate_UsCountryCode_StartsWithNThenADigit_NeverALetter()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var registration = AircraftRegistrationGenerator.Generate("US", new Random(seed));

            Assert.StartsWith("N", registration);
            Assert.DoesNotContain("-", registration);
            Assert.True(registration.Length is >= 2 and <= 6, $"'{registration}' must be N + 1-5 characters.");
            Assert.True(char.IsDigit(registration[1]), $"'{registration}': the character after N must be a digit.");
            Assert.NotEqual('0', registration[1]);
        }
    }

    [Fact]
    public void Generate_UsCountryCode_NeverRevertsToDigitsAfterALetter()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var registration = AircraftRegistrationGenerator.Generate("US", new Random(seed));
            var body = registration[1..];

            var firstLetterIndex = body.Select((c, i) => (c, i)).Where(p => char.IsLetter(p.c)).Select(p => (int?)p.i).FirstOrDefault();
            if (firstLetterIndex is { } index)
            {
                Assert.All(body[index..], c => Assert.True(char.IsLetter(c)));
            }
        }
    }

    [Fact]
    public void Generate_UnmappedCountryCode_FallsBackToGenericPrefix()
    {
        var registration = AircraftRegistrationGenerator.Generate("ZZ", new Random(7));

        Assert.StartsWith("FS-", registration);
    }

    [Fact]
    public void Generate_NullCountryCode_FallsBackToGenericPrefix()
    {
        var registration = AircraftRegistrationGenerator.Generate(null, new Random(8));

        Assert.StartsWith("FS-", registration);
    }

    [Fact]
    public void Generate_IsCaseInsensitiveOnCountryCode()
    {
        var registration = AircraftRegistrationGenerator.Generate("gb", new Random(9));

        Assert.StartsWith("G-", registration);
    }

    /// <summary>
    /// The core of the bug fix: two calls for the SAME airline (same country) must be able to
    /// produce DIFFERENT registrations - the old design derived the tail purely from the airline's
    /// ICAO code, so every aircraft in a fleet got the identical base registration.
    /// </summary>
    [Fact]
    public void Generate_RepeatedCallsForTheSameCountry_ProduceDifferentRegistrations()
    {
        var random = new Random(42);
        var results = Enumerable.Range(0, 20).Select(_ => AircraftRegistrationGenerator.Generate("GB", random)).ToHashSet();

        Assert.True(results.Count > 1, "Repeated calls should not all collapse to the same registration.");
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("G-EZBA")]
    [InlineData("N737FS")]
    [InlineData("A1")]
    [InlineData("ABCDEFGHIJ")] // exactly 10 - the maximum
    public void IsValidCustomRegistration_AcceptsSensibleValues(string candidate)
    {
        Assert.True(AircraftRegistrationGenerator.IsValidCustomRegistration(candidate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")] // too short
    [InlineData("ABCDEFGHIJK")] // too long (11)
    [InlineData("G EZBA")] // space not allowed
    [InlineData("G-EZ$A")] // symbol not allowed
    [InlineData("g-ezba")] // lowercase - caller is expected to normalize first
    public void IsValidCustomRegistration_RejectsInvalidValues(string candidate)
    {
        Assert.False(AircraftRegistrationGenerator.IsValidCustomRegistration(candidate));
    }

    /// <summary>
    /// Does NOT enforce a country's own format on a custom entry - docs/PLAN.md is explicit that a
    /// player matching a repaint knows what they want. A US-shaped "N-number-looking" string typed
    /// in for a UK-hubbed airline (or vice versa) must still validate.
    /// </summary>
    [Fact]
    public void IsValidCustomRegistration_DoesNotEnforceAnyCountryFormat()
    {
        Assert.True(AircraftRegistrationGenerator.IsValidCustomRegistration("N123AB")); // US-shaped
        Assert.True(AircraftRegistrationGenerator.IsValidCustomRegistration("ZZ-1234")); // matches nobody's real format
    }
}

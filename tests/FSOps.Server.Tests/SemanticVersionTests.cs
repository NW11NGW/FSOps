using FSOps.Server.Services;

namespace FSOps.Server.Tests;

/// <summary>
/// Version comparison is the one part of the updater that fails silently and invisibly when it is
/// wrong: a string-compared updater works perfectly until the minor version reaches double digits
/// and then simply stops offering updates forever, with no error anywhere. These tests exist mostly
/// to nail that case down.
/// </summary>
public class SemanticVersionTests
{
    private static SemanticVersion Parse(string value)
    {
        Assert.True(SemanticVersion.TryParse(value, out var version), $"'{value}' should parse");
        return version!;
    }

    [Theory]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("v0.1.0", 0, 1, 0)]
    [InlineData("V2.10.3", 2, 10, 3)]
    [InlineData("1.2.3+abc123", 1, 2, 3)]
    [InlineData("1.4", 1, 4, 0)]
    [InlineData("2", 2, 0, 0)]
    public void TryParse_AcceptsTheTagShapesAReleaseActuallyUses(string input, int major, int minor, int patch)
    {
        var version = Parse(input);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.False(version.IsPrerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("1.x.3")]
    [InlineData("1.2.3-")]
    public void TryParse_RejectsAnythingItCannotCompareHonestly(string? input)
    {
        Assert.False(SemanticVersion.TryParse(input, out _));
    }

    [Fact]
    public void TenIsNewerThanNine_WhichStringComparisonWouldGetBackwards()
    {
        var ten = Parse("0.10.0");
        var nine = Parse("0.9.0");

        Assert.True(ten > nine);
        Assert.True(nine < ten);

        // Proof the naive implementation really would have been wrong here, so this test cannot be
        // "simplified" back into string comparison without failing.
        Assert.True(string.CompareOrdinal("0.10.0", "0.9.0") < 0);
    }

    [Theory]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.2.0", "0.1.99")]
    [InlineData("0.1.10", "0.1.9")]
    [InlineData("10.0.0", "9.99.99")]
    [InlineData("1.0.0", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.10", "1.0.0-alpha.9")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")]
    public void LeftIsStrictlyNewerThanRight(string newer, string older)
    {
        Assert.True(Parse(newer) > Parse(older), $"{newer} should be newer than {older}");
        Assert.True(Parse(older) < Parse(newer), $"{older} should be older than {newer}");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+build.1", "1.2.3+build.2")]
    [InlineData("1.2", "1.2.0")]
    public void VersionsThatAreTheSameReleaseCompareEqual(string left, string right)
    {
        Assert.Equal(0, Parse(left).CompareTo(Parse(right)));
        Assert.True(Parse(left) == Parse(right));
        Assert.True(Parse(left) >= Parse(right));
        Assert.True(Parse(left) <= Parse(right));
    }

    [Theory]
    [InlineData("1.0.0-beta.1")]
    [InlineData("v0.2.0-rc.1")]
    [InlineData("0.2.0-alpha")]
    public void PrereleaseTagsAreRecognisedAsSuch_SoTheyCanBeRefused(string tag)
    {
        Assert.True(Parse(tag).IsPrerelease);
    }

    [Fact]
    public void ToString_RoundTripsWithoutTheLeadingVOrBuildMetadata()
    {
        Assert.Equal("1.2.3", Parse("v1.2.3+deadbeef").ToString());
        Assert.Equal("1.2.3-rc.1", Parse("v1.2.3-rc.1").ToString());
    }
}

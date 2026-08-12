using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

/// <summary>
/// J28 - the OurAirports "surface" column is inconsistent free text, not an enum, so this pins down
/// exactly which real-world values are (and are not) treated as soft.
/// </summary>
public class RunwaySurfaceClassifierTests
{
    [Theory]
    [InlineData("GRASS")]
    [InlineData("GRS")]
    [InlineData("TURF")]
    [InlineData("SOD")]
    [InlineData("GRAVEL")]
    [InlineData("GVL")]
    [InlineData("GRVL")]
    [InlineData("GRE")]
    [InlineData("DIRT")]
    [InlineData("EARTH")]
    [InlineData("CLAY")]
    [InlineData("WATER")]
    [InlineData("WAT")]
    [InlineData("grass")] // case-insensitive
    public void IsSoft_KnownSoftTokens_AreSoft(string surface)
    {
        Assert.True(RunwaySurfaceClassifier.IsSoft(surface));
    }

    [Theory]
    [InlineData("ASP")]
    [InlineData("ASPH")]
    [InlineData("CON")]
    [InlineData("CONC")]
    [InlineData("PEM")]
    [InlineData("BIT")]
    [InlineData("MATS")]
    [InlineData("UNK")]
    [InlineData("")]
    [InlineData(null)]
    // Ambiguous single-letter and unrecognised codes - never guessed at, never blocked on.
    [InlineData("G")]
    [InlineData("X")]
    [InlineData("N")]
    public void IsSoft_PavedOrAmbiguousTokens_AreNotSoft(string? surface)
    {
        Assert.False(RunwaySurfaceClassifier.IsSoft(surface));
    }

    /// <summary>
    /// The permissive-by-default principle applied in the direction that matters for a rule that
    /// BLOCKS: a composite naming a recognisable hard surface alongside anything else - most likely
    /// an asphalt/concrete runway with a grass verge, or a part-and-part strip - must resolve to
    /// usable, not soft. There is no reliable way to tell from the code alone that the paved portion
    /// isn't what a player would actually use, and guessing wrong here costs exactly what guessing
    /// wrong on an unknown code costs: an unearned block with no recourse. A hard token anywhere in
    /// the composite wins, even with a soft token also present.
    /// </summary>
    [Theory]
    [InlineData("ASP-GRS")]
    [InlineData("CONC-TURF")]
    [InlineData("ASPH-TURF")]
    [InlineData("PEM-GRASS")]
    public void IsSoft_CompositeCodesNamingAHardSurfaceAlongsideASoftOne_AreNotSoft(string surface)
    {
        Assert.False(RunwaySurfaceClassifier.IsSoft(surface));
    }

    /// <summary>
    /// The mirror image: a composite naming ONLY soft surfaces, or a soft surface alongside a purely
    /// ambiguous token (no recognisable hard component at all), has nothing to make it usable - it
    /// stays soft.
    /// </summary>
    [Theory]
    [InlineData("TURF-G")]
    [InlineData("GRASS / SOD")]
    [InlineData("Turf/Dirt")]
    [InlineData("GRVL-G")]
    [InlineData("Grassed brown clay")]
    public void IsSoft_CompositeCodesNamingOnlySoftOrAmbiguousComponents_AreSoft(string surface)
    {
        Assert.True(RunwaySurfaceClassifier.IsSoft(surface));
    }

    [Theory]
    [InlineData("ASPH/ CONC")]
    [InlineData("ASPH-CONC")]
    [InlineData("Asphalt/Concrete")]
    public void IsSoft_CompositePavedCodes_AreNotSoft(string surface)
    {
        Assert.False(RunwaySurfaceClassifier.IsSoft(surface));
    }
}

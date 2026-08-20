using System.Text.Json;
using System.Text.RegularExpressions;
using FSOps.Core.SimAircraft;

namespace FSOps.Core.Tests.SimAircraft;

/// <summary>
/// The contract-eligible aircraft catalogue, checked for the properties contract generation will
/// rely on. Most of these are the kind of thing that "obviously holds" right up until somebody adds
/// a fiftieth entry by copy-paste.
/// </summary>
public class ContractAircraftCatalogueTests
{
    [Fact]
    public void EveryTypeDesignatorIsUniqueAndUpperCase()
    {
        var designators = ContractAircraftCatalogue.All.Select(a => a.TypeDesignator).ToList();

        Assert.Equal(designators.Count, designators.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(designators, d => Assert.Equal(d.ToUpperInvariant(), d));
        Assert.All(designators, d => Assert.InRange(d.Length, 3, 5));
    }

    /// <summary>
    /// The whole point of the catalogue is that a contract can be flown, which needs numbers a
    /// generator can size a job from. A zero anywhere but Seats is a copy-paste that got missed;
    /// zero Seats is legitimate, and means "freight only" (the A400M).
    /// </summary>
    [Fact]
    public void EveryAircraftHasUsableNumbers()
    {
        Assert.All(ContractAircraftCatalogue.All, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.False(string.IsNullOrWhiteSpace(a.Manufacturer));
            Assert.InRange(a.Seats, 0, 900);
            Assert.InRange(a.PayloadKg, 50, 150_000);
            Assert.InRange(a.RangeNm, 100, 12_000);
            Assert.InRange(a.CruiseTasKts, 60, 600);
        });
    }

    /// <summary>
    /// Match patterns are shipped data used at runtime, and AircraftTypeMatcher swallows a bad regex
    /// as "no match" rather than throwing - which is right for flight tracking and terrible for
    /// noticing a typo. So the typo has to be caught here instead.
    /// </summary>
    [Fact]
    public void EveryMatchPatternListIsValidJsonAndValidRegex()
    {
        Assert.All(ContractAircraftCatalogue.All, a =>
        {
            var patterns = JsonSerializer.Deserialize<string[]>(a.MatchPatterns);
            Assert.NotNull(patterns);
            Assert.NotEmpty(patterns!);

            foreach (var pattern in patterns!)
            {
                var exception = Record.Exception(() => Regex.IsMatch("probe", pattern, RegexOptions.IgnoreCase));
                Assert.Null(exception);
            }
        });
    }

    /// <summary>
    /// An entry whose own designator does not match its own patterns is broken in the way that is
    /// hardest to spot: the scan identifies it by designator and everything looks fine, right up
    /// until a package that only reports a title needs identifying.
    /// </summary>
    [Fact]
    public void EveryAircraftIsFoundByItsOwnDesignatorAndByItsOwnPatterns()
    {
        Assert.All(ContractAircraftCatalogue.All, a =>
        {
            Assert.Same(a, ContractAircraftCatalogue.Find(a.TypeDesignator));
            Assert.Same(a, ContractAircraftCatalogue.Find(a.TypeDesignator.ToLowerInvariant()));

            var byText = ContractAircraftCatalogue.FindByText(a.TypeDesignator, atcModel: null);
            Assert.NotNull(byText);
            Assert.Equal(a.TypeDesignator, byText!.TypeDesignator);
        });
    }

    /// <summary>
    /// Base package ids are how base content is recognised on disk, and a duplicate would mean one
    /// folder claiming to deliver two different aircraft.
    /// </summary>
    [Fact]
    public void BasePackageIdsAreUniqueAcrossTheCatalogue()
    {
        var ids = ContractAircraftCatalogue.All.SelectMany(a => a.BasePackageIds).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// <b>The rule that keeps a Standard-edition player out of a contract they cannot fly.</b> The
    /// simulator ships low-detail <c>passiveaircraft-</c> models so AI traffic can render aircraft
    /// the player has not bought: a Standard install physically contains
    /// <c>fs24-microsoft-passiveaircraft-s340</c> and <c>...-passiveaircraft-c408</c> even though
    /// the flyable Saab 340 and SkyCourier are Premium Deluxe and Deluxe content. Matching one of
    /// those folders would report an aircraft that is not in the hangar.
    /// </summary>
    [Fact]
    public void NoBasePackageIdRefersToAnAiOnlyPassiveAircraftPackage()
    {
        Assert.All(
            ContractAircraftCatalogue.All.SelectMany(a => a.BasePackageIds),
            id => Assert.DoesNotContain("passiveaircraft", id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An add-on is by definition not base content, so it can have no base package id - otherwise a
    /// disk scan could mark it present on evidence that does not exist.
    /// </summary>
    [Fact]
    public void AddOnsCarryNoBasePackageIds()
    {
        Assert.All(
            ContractAircraftCatalogue.All.Where(a => a.ShipsWith == SimAircraftAvailability.AddOn),
            a => Assert.Empty(a.BasePackageIds));
    }

    /// <summary>
    /// The catalogue exists because MSFS 2024 has no general aviation in the fleet catalogue at all,
    /// and "transatlantic in a Cessna" was not expressible. If this ever fails, the catalogue has
    /// drifted back into being an airliner list.
    /// </summary>
    [Fact]
    public void TheCatalogueReachesFromLightSinglesToWidebodies()
    {
        var categories = ContractAircraftCatalogue.All.Select(a => a.Category).Distinct().ToList();

        Assert.Contains(ContractAircraftCategory.LightSingle, categories);
        Assert.Contains(ContractAircraftCategory.LightTwin, categories);
        Assert.Contains(ContractAircraftCategory.UtilityTurboprop, categories);
        Assert.Contains(ContractAircraftCategory.BusinessJet, categories);
        Assert.Contains(ContractAircraftCategory.RegionalAirliner, categories);
        Assert.Contains(ContractAircraftCategory.Narrowbody, categories);
        Assert.Contains(ContractAircraftCategory.Widebody, categories);

        Assert.NotNull(ContractAircraftCatalogue.Find("C172"));
        Assert.NotNull(ContractAircraftCatalogue.Find("C152"));
    }

    /// <summary>
    /// iniBuilds' A350 ULR really does write <c>icao_type_designator = A359 ULR</c>. A lookup that
    /// choked on the trailing word would miss an aircraft sitting in the player's Community folder.
    /// </summary>
    [Theory]
    [InlineData("A359 ULR", "A359")]
    [InlineData("\"A320\"", "A320")]
    [InlineData("  a388  ", "A388")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void NormaliseDesignator_TakesTheBareCode(string? raw, string? expected)
    {
        Assert.Equal(expected, ContractAircraftCatalogue.NormaliseDesignator(raw));
    }

    /// <summary>The three add-ons that are actually in the user's Community folder today.</summary>
    [Theory]
    [InlineData("A320", "A320")]
    [InlineData("A359", "A359")]
    [InlineData("A35K", "A35K")]
    [InlineData("A388", "A388")]
    public void Find_IdentifiesTheAddOnsAScanOfARealCommunityFolderReports(string designator, string expected)
    {
        Assert.Equal(expected, ContractAircraftCatalogue.Find(designator)?.TypeDesignator);
    }

    /// <summary>
    /// The one collision worth pinning: plain A320 and A320neo are different aircraft with different
    /// designators, and a pattern list that let one swallow the other would put the wrong aircraft
    /// on a contract.
    /// </summary>
    [Theory]
    [InlineData("FenixA320 CFM SL", "A320")]
    [InlineData("Airbus A320neo", "A20N")]
    [InlineData("Airbus A321neo", "A21N")]
    [InlineData("Cessna 172 Skyhawk G1000", "C172")]
    [InlineData("Boeing 737 MAX 8", "B38M")]
    [InlineData("Cessna 208B Grand Caravan EX", "C208")]
    public void FindByText_PicksTheRightAircraftFromAFreeformTitle(string title, string expected)
    {
        Assert.Equal(expected, ContractAircraftCatalogue.FindByText(title, atcModel: null)?.TypeDesignator);
    }

    [Fact]
    public void FindByText_ReturnsNullWhenTheSimHasToldUsNothing()
    {
        Assert.Null(ContractAircraftCatalogue.FindByText(null, null));
        Assert.Null(ContractAircraftCatalogue.FindByText("   ", "  "));
        Assert.Null(ContractAircraftCatalogue.FindByText("Generic Quad Jet Airliner", null));
    }
}

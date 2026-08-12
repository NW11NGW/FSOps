using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

/// <summary>
/// Exercises the MatchPatterns actually shipped in AircraftTypeSeeder.cs against a realistic
/// sample of freeform TITLE strings popular add-ons report. Match patterns matter as much as the
/// numbers: each family needs regex broad enough to catch the popular add-ons (PMDG, Fenix,
/// iniBuilds, FlyByWire, Leonardo, Aerosoft) without colliding with another family, which is what
/// makes type verification useful rather than noise. The
/// patterns are duplicated here (rather than referencing the seeder's private consts) because this
/// is deliberately testing the SHAPE of the data as shipped, the same way
/// EconomyConfigTests.FromJson_ParsesTheShippedConfigShape keeps its own copy of economy-config.json's
/// shape - a drift between the two would fail this test, which is the point.
/// </summary>
public class AircraftTypeCataloguePatternsTests
{
    private const string A320FamilyPatterns = """["A31[89]","A32[01]","A20N","A19N","A21N","A32NX"]""";
    private const string B737FamilyPatterns = """["B73[6-9]","737","B738","B37M","B38M","B39M"]""";
    private const string B757Patterns = """["B75[23]","757"]""";
    private const string A330Patterns = """["A33[023]","A338","A339","A330"]""";
    private const string A350Patterns = """["A35[9K]","A350"]""";
    private const string A380Patterns = """["A388","A380"]""";
    private const string B767Patterns = """["B76[234]","767"]""";
    private const string B777Patterns = """["B77[0-9]","B77[LW]","777"]""";
    private const string B787Patterns = """["B78[89X]","787","Dreamliner"]""";
    private const string B747Patterns = """["B74[1-8]","747"]""";
    private const string EJetPatterns = """["E-?170","E-?175","E-?190","E-?195","E75L","E75S","E290","E295"]""";
    private const string CrjPatterns = """["CRJ.{0,3}700","CRJ.{0,3}900","CRJ7","CRJ9"]""";
    private const string AtrPatterns = """["ATR.{0,3}42","ATR.{0,3}72","AT42","AT43","AT45","AT46","AT72","AT73","AT75","AT76"]""";
    private const string Dash8Patterns = """["Q400","DHC-?8","Dash.{0,2}8","DH8D"]""";

    /// <summary>The exact title the user's own installed aircraft reports, pinned as
    /// its own regression test since this is the concrete bug report that started this work.</summary>
    [Fact]
    public void IsMatch_TheUsersActualFenixA320Title_MatchesTheA320Family()
    {
        Assert.True(AircraftTypeMatcher.IsMatch(A320FamilyPatterns, "FenixA320 CFM SL", atcModel: null));
    }

    [Theory]
    [InlineData(A320FamilyPatterns, "PMDG A320neo", "A20N")]
    [InlineData(A320FamilyPatterns, "FlyByWire A32NX", null)]
    [InlineData(A320FamilyPatterns, "iniBuilds A321neo", "A21N")]
    [InlineData(B737FamilyPatterns, "PMDG 737-800", "B738")]
    [InlineData(B737FamilyPatterns, "PMDG 737 MAX 8", "B38M")]
    [InlineData(B757Patterns, "PMDG 757-200", "B752")]
    [InlineData(A330Patterns, "iniBuilds A330-300", "A333")]
    [InlineData(A350Patterns, "iniBuilds A350-900XWB", "A359")]
    [InlineData(A380Patterns, "PMDG A380-800", "A388")]
    [InlineData(B767Patterns, "PMDG 767-300ER", "B763")]
    [InlineData(B777Patterns, "PMDG 777-300ER", "B77W")]
    [InlineData(B787Patterns, "Leonardo 787-9 Dreamliner", "B789")]
    [InlineData(B747Patterns, "PMDG 747-8", "B748")]
    [InlineData(EJetPatterns, "Aerosoft/Digital Design E175", "E175")]
    [InlineData(CrjPatterns, "Aerosoft CRJ 900 Professional", null)]
    [InlineData(CrjPatterns, "Aerosoft CRJ-700", null)]
    [InlineData(AtrPatterns, "Aerosoft/Digital Design ATR 72-600", null)]
    [InlineData(Dash8Patterns, "Q400 Xtreme Prolines", "DH8D")]
    [InlineData(Dash8Patterns, "Just Flight DHC-8 Q400", "DH8D")]
    public void IsMatch_PopularAddOnTitle_MatchesItsFamily(string patterns, string title, string? atcModel)
    {
        Assert.True(AircraftTypeMatcher.IsMatch(patterns, title, atcModel), $"'{title}' should match its own family's patterns.");
    }

    /// <summary>
    /// Breadth must not come at the cost of one family stealing another's - a sample of cross-family titles
    /// that must NOT match a neighbouring family's patterns, guarding against overly broad tokens
    /// (e.g. a bare "7" or "A3" that would swallow half the catalogue).
    /// </summary>
    [Theory]
    [InlineData(B747Patterns, "PMDG 737-800", "B738")] // 747 patterns must not catch a 737
    [InlineData(B757Patterns, "PMDG 767-300ER", "B763")] // 757 patterns must not catch a 767
    [InlineData(B767Patterns, "PMDG 777-300ER", "B77W")] // 767 patterns must not catch a 777
    [InlineData(A350Patterns, "iniBuilds A330-300", "A333")] // 350 patterns must not catch a 330
    [InlineData(A380Patterns, "iniBuilds A350-900", "A359")] // 380 patterns must not catch a 350
    [InlineData(CrjPatterns, "Aerosoft/Digital Design ATR 72-600", null)] // CRJ patterns must not catch an ATR
    [InlineData(AtrPatterns, "Aerosoft CRJ 900", null)] // ATR patterns must not catch a CRJ
    [InlineData(EJetPatterns, "Just Flight DHC-8 Q400", "DH8D")] // E-Jet patterns must not catch a Dash 8
    public void IsMatch_CrossFamilyTitle_DoesNotFalselyMatch(string patterns, string title, string? atcModel)
    {
        Assert.False(AircraftTypeMatcher.IsMatch(patterns, title, atcModel), $"'{title}' should NOT match a different family's patterns.");
    }
}

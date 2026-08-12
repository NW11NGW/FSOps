using System.Text;
using FSOps.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The bundled-data boundary source. Two halves: parsing rules driven by small hand-written
/// fixtures, and a handful of assertions against the real shipped files - because a truncated or
/// corrupted data file parses to zero boundaries, and zero boundaries is indistinguishable on
/// screen from "nobody is online". Nothing here touches the network.
/// </summary>
public class VatSpyBoundarySourceTests
{
    private static VatSpyBoundarySource SourceFor(string directory) =>
        new(directory, NullLogger<VatSpyBoundarySource>.Instance);

    private static Stream StreamOf(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    // -----------------------------------------------------------------------------------
    // VATSpy.dat - the callsign directory
    // -----------------------------------------------------------------------------------

    private const string SampleDirectory = """
        [Countries]
        United Kingdom|EG|

        [Airports]
        EGLL|Heathrow|51.4775|-0.4614|EGTT|0

        [FIRs]
        ;ICAO|NAME|CALLSIGN PREFIX|FIR BOUNDARY
        EGTT|London|LON|EGTT
        EGPX|Scottish|SCO|EGPX
        EGPX|Scottish|SCL|EGPX
        ADR|Adria Radar||ADR
        ADR|Adria Radar (Upper FL335+)|ADR_U|ADR
        ADR-E|Adria Radar (East)|ADR_E|ADR-E

        [UIRs]
        ADR_U|Adria Radar Upper|EGTT,EGPX
        NORTH|Northern Union|EGPX,ADR-E
        """;

    [Fact]
    public void ParseDirectory_RegistersBothTheIcaoAndTheCallsignPrefix()
    {
        var prefixes = VatSpyBoundarySource.ParseDirectory(SampleDirectory);

        // EGTT_CTR and LON_CTR are the same airspace flown two ways, so both must resolve.
        Assert.Equal(new[] { "EGTT" }, prefixes["EGTT"].BoundaryIds);
        Assert.Equal(new[] { "EGTT" }, prefixes["LON"].BoundaryIds);
        Assert.Equal("London", prefixes["LON"].Name);
    }

    [Fact]
    public void ParseDirectory_OneFirWithSeveralPrefixes_RegistersEachOfThem()
    {
        var prefixes = VatSpyBoundarySource.ParseDirectory(SampleDirectory);

        Assert.Equal(new[] { "EGPX" }, prefixes["SCO"].BoundaryIds);
        Assert.Equal(new[] { "EGPX" }, prefixes["SCL"].BoundaryIds);
    }

    [Fact]
    public void ParseDirectory_BlankBoundaryColumn_FallsBackToTheIcao()
    {
        var prefixes = VatSpyBoundarySource.ParseDirectory("""
            [FIRs]
            ZZZZ|Nowhere Control|NOW|
            """);

        Assert.Equal(new[] { "ZZZZ" }, prefixes["NOW"].BoundaryIds);
    }

    [Fact]
    public void ParseDirectory_Uir_ResolvesToTheUnionOfItsMemberFirs()
    {
        var prefixes = VatSpyBoundarySource.ParseDirectory(SampleDirectory);

        Assert.Equal(new[] { "EGPX", "ADR-E" }, prefixes["NORTH"].BoundaryIds);
        Assert.Equal("Northern Union", prefixes["NORTH"].Name);
    }

    [Fact]
    public void ParseDirectory_WhenAUirCollidesWithAFirPrefix_TheFirWins()
    {
        // A real collision in the shipped file, not a hypothetical one: "ADR_U" is both a [FIRs]
        // callsign prefix (Adria Radar upper, geometry ADR) and a [UIRs] ICAO. Matching VAT-Spy's
        // own resolution order errs towards the smaller, more specific claim.
        var prefixes = VatSpyBoundarySource.ParseDirectory(SampleDirectory);

        Assert.Equal(new[] { "ADR" }, prefixes["ADR_U"].BoundaryIds);
    }

    [Fact]
    public void ParseDirectory_IgnoresCommentsBlankLinesAndSectionsItDoesNotUse()
    {
        var prefixes = VatSpyBoundarySource.ParseDirectory(SampleDirectory);

        // "United Kingdom" from [Countries] and the airport row must not have become prefixes.
        Assert.False(prefixes.ContainsKey("UNITED KINGDOM"));
        Assert.False(prefixes.ContainsKey("EGLL"));
        Assert.False(prefixes.ContainsKey(";ICAO"));
    }

    // -----------------------------------------------------------------------------------
    // Callsign prefix matching
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("EGTT_CTR", "EGTT")]
    [InlineData("LON_CTR", "EGTT")]
    [InlineData("LON_S_CTR", "EGTT")]   // no LON_S entry - falls back to LON
    [InlineData("SCO_CTR", "EGPX")]
    [InlineData("ADR_E_CTR", "ADR-E")]  // the entry that first-segment matching would get wrong
    [InlineData("ADR_CTR", "ADR")]
    [InlineData("lon_ctr", "EGTT")]     // the feed is upper-case, but do not depend on it
    public void MatchPrefix_ResolvesLongestFirst(string callsign, string expectedBoundaryId)
    {
        var prefixes = VatSpyBoundarySource.ParseDirectory(SampleDirectory);

        var target = VatSpyBoundarySource.MatchPrefix(callsign, prefixes);

        Assert.NotNull(target);
        Assert.Contains(expectedBoundaryId, target!.BoundaryIds);
    }

    [Fact]
    public void MatchPrefix_TakingTheFirstSegmentWouldBeWrong_AndThisIsWhy()
    {
        // ADR_E is Adria Radar East, a distinct and much smaller region than ADR. Splitting on the
        // first underscore would silently substitute the larger one and draw it confidently.
        var prefixes = VatSpyBoundarySource.ParseDirectory(SampleDirectory);

        var east = VatSpyBoundarySource.MatchPrefix("ADR_E_CTR", prefixes);
        var whole = VatSpyBoundarySource.MatchPrefix("ADR_CTR", prefixes);

        Assert.Equal(new[] { "ADR-E" }, east!.BoundaryIds);
        Assert.Equal(new[] { "ADR" }, whole!.BoundaryIds);
        Assert.NotEqual(east.BoundaryIds, whole.BoundaryIds);
    }

    [Theory]
    [InlineData("ZZZZ_CTR")]
    [InlineData("NOTAPREFIX_FSS")]
    [InlineData("_CTR")]
    [InlineData("")]
    public void MatchPrefix_UnknownCallsign_ResolvesToNothing(string callsign)
    {
        var prefixes = VatSpyBoundarySource.ParseDirectory(SampleDirectory);

        Assert.Null(VatSpyBoundarySource.MatchPrefix(callsign, prefixes));
    }

    // -----------------------------------------------------------------------------------
    // Boundaries.geojson - the geometry
    // -----------------------------------------------------------------------------------

    private const string SampleBoundaries = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature",
              "properties": { "id": "EGTT", "oceanic": "0" },
              "geometry": { "type": "Polygon", "coordinates": [[[0,50],[2,50],[2,52],[0,52],[0,50]]] } },
            { "type": "Feature",
              "properties": { "id": "EGPX" },
              "geometry": { "type": "MultiPolygon", "coordinates": [
                  [[[-8,54],[0,54],[0,59],[-8,59],[-8,54]]],
                  [[[-12,54],[-10,54],[-10,56],[-12,56],[-12,54]]] ] } }
          ]
        }
        """;

    [Fact]
    public void ParseBoundaries_ReadsPolygonAndMultiPolygonFeaturesKeyedByPropertyId()
    {
        using var stream = StreamOf(SampleBoundaries);

        var boundaries = VatSpyBoundarySource.ParseBoundaries(stream);

        Assert.Equal(2, boundaries.Count);
        Assert.Single(boundaries["EGTT"]);
        Assert.Equal(2, boundaries["EGPX"].Count);
        Assert.Equal(new GeoPoint(0, 50), boundaries["EGTT"][0].Rings[0][0]);
    }

    [Fact]
    public void ParseBoundaries_FeaturesSharingAnId_AreMergedRatherThanOverwritten()
    {
        // Upstream splits some regions across several features under one id - the oceanic and
        // domestic halves of one FIR, for instance. Keeping only the last would silently drop half
        // the airspace, and the result would look perfectly plausible.
        using var stream = StreamOf("""
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature", "properties": { "id": "SPLIT" },
                  "geometry": { "type": "Polygon", "coordinates": [[[0,0],[1,0],[1,1],[0,1],[0,0]]] } },
                { "type": "Feature", "properties": { "id": "SPLIT" },
                  "geometry": { "type": "Polygon", "coordinates": [[[9,9],[10,9],[10,10],[9,10],[9,9]]] } }
              ]
            }
            """);

        var boundaries = VatSpyBoundarySource.ParseBoundaries(stream);

        Assert.Equal(2, Assert.Single(boundaries).Value.Count);
    }

    [Fact]
    public void ParseBoundaries_SkipsFeaturesThatAreUnusableRatherThanFailingTheWholeFile()
    {
        using var stream = StreamOf("""
            {
              "type": "FeatureCollection",
              "features": [
                { "type": "Feature", "properties": { "oceanic": "0" },
                  "geometry": { "type": "Polygon", "coordinates": [[[0,0],[1,0],[1,1],[0,0]]] } },
                { "type": "Feature", "properties": { "id": "NOGEOM" } },
                { "type": "Feature", "properties": { "id": "DEGENERATE" },
                  "geometry": { "type": "Polygon", "coordinates": [[[0,0],[1,1]]] } },
                { "type": "Feature", "properties": { "id": "POINT" },
                  "geometry": { "type": "Point", "coordinates": [5,5] } },
                { "type": "Feature", "properties": { "id": "GOOD" },
                  "geometry": { "type": "Polygon", "coordinates": [[[0,0],[1,0],[1,1],[0,1],[0,0]]] } }
              ]
            }
            """);

        var boundaries = VatSpyBoundarySource.ParseBoundaries(stream);

        // A two-point "ring" is not an area; drawing it would produce a sliver, not a shape.
        Assert.Equal(new[] { "GOOD" }, boundaries.Keys);
    }

    // -----------------------------------------------------------------------------------
    // End to end, from files on disk
    // -----------------------------------------------------------------------------------

    private static string WriteFixtureDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fsops-vatspy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Uncompressed on purpose - the source accepts either, and this exercises that fallback.
        File.WriteAllText(Path.Combine(directory, "Boundaries.geojson"), SampleBoundaries);
        File.WriteAllText(Path.Combine(directory, "VATSpy.dat"), SampleDirectory);
        return directory;
    }

    [Fact]
    public void Resolve_JoinsTheDirectoryToTheGeometry()
    {
        var directory = WriteFixtureDirectory();
        try
        {
            var source = SourceFor(directory);

            Assert.True(source.Available);
            var boundary = source.Resolve("LON_CTR");

            Assert.NotNull(boundary);
            Assert.Equal("EGTT", boundary!.Id);
            Assert.Equal("London", boundary.Name);
            Assert.True(AtcBoundaryGeometry.Contains(boundary, 51, 1));
            Assert.False(AtcBoundaryGeometry.Contains(boundary, 55, 1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_Uir_UnionsTheGeometryOfEveryMemberFir()
    {
        var directory = WriteFixtureDirectory();
        try
        {
            var boundary = SourceFor(directory).Resolve("NORTH_CTR");

            // NORTH is EGPX (two polygons) plus ADR-E, which has no geometry in the fixture - so
            // the union is the two that exist, not a failure.
            Assert.NotNull(boundary);
            Assert.Equal(2, boundary!.Polygons.Count);
            Assert.True(AtcBoundaryGeometry.Contains(boundary, 55.95, -3.3725));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_DirectoryEntryPointingAtGeometryThatIsNotThere_ResolvesToNothing()
    {
        var directory = WriteFixtureDirectory();
        try
        {
            // ADR-E is listed in [FIRs] but has no feature in the fixture geojson - a mid-cycle
            // inconsistency upstream. It must resolve to null, never to an empty shape.
            Assert.Null(SourceFor(directory).Resolve("ADR_E_CTR"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingDataDirectory_IsUnavailableAndNeverThrows()
    {
        // A stripped deployment. En-route controllers simply are not shown, which is exactly what
        // FSOps did before this data existed - never an error, and never a guessed shape.
        var source = SourceFor(Path.Combine(Path.GetTempPath(), "fsops-vatspy-tests", "does-not-exist"));

        Assert.False(source.Available);
        Assert.Null(source.Resolve("LON_CTR"));
    }

    [Fact]
    public void MalformedDataFiles_AreUnavailableAndNeverThrow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fsops-vatspy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Boundaries.geojson"), "{ this is not json");
            File.WriteAllText(Path.Combine(directory, "VATSpy.dat"), SampleDirectory);
            var source = SourceFor(directory);

            Assert.False(source.Available);
            Assert.Null(source.Resolve("LON_CTR"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // -----------------------------------------------------------------------------------
    // The real bundled files
    // -----------------------------------------------------------------------------------

    private static VatSpyBoundarySource BundledSource() =>
        SourceFor(Path.Combine(AppContext.BaseDirectory, "data", VatSpyBoundarySource.DataFolderName));

    [Fact]
    public void BundledData_IsPresentAndParses()
    {
        // The check that matters most: a truncated download, a corrupt gzip or a silently changed
        // upstream format all parse to zero boundaries, and zero boundaries looks exactly like
        // "nobody is online" on screen. It has to be asserted, not eyeballed once.
        var source = BundledSource();

        Assert.True(source.Available);
    }

    [Theory]
    [InlineData("EGTT_CTR")]
    [InlineData("LON_CTR")]
    [InlineData("LON_S_CTR")]
    public void BundledData_ResolvesTheLondonFirHoweverItIsCalled(string callsign)
    {
        var boundary = BundledSource().Resolve(callsign);

        Assert.NotNull(boundary);
        // London Heathrow, which is unambiguously inside the London FIR and will stay there.
        Assert.True(AtcBoundaryGeometry.Contains(boundary!, 51.4775, -0.4614));
        // Edinburgh is in the Scottish FIR, not this one.
        Assert.False(AtcBoundaryGeometry.Contains(boundary!, 55.9500, -3.3725));
    }

    [Fact]
    public void BundledData_ResolvesTheScottishFirToADifferentRegion()
    {
        var boundary = BundledSource().Resolve("SCO_CTR");

        Assert.NotNull(boundary);
        Assert.True(AtcBoundaryGeometry.Contains(boundary!, 55.9500, -3.3725));  // EGPH
        Assert.False(AtcBoundaryGeometry.Contains(boundary!, 51.3827, -2.7191)); // EGGD, London FIR
    }

    [Fact]
    public void BundledData_UnknownCallsignResolvesToNothing()
    {
        Assert.Null(BundledSource().Resolve("ZZZZ_CTR"));
        Assert.Null(BundledSource().Resolve("NOTREAL_FSS"));
    }
}

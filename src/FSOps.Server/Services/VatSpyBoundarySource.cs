using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FSOps.Server.Services;

/// <summary>
/// The bundled-data implementation of <see cref="IAtcBoundarySource"/>, reading the VAT-Spy Data
/// Project's two published files from <c>data/vatspy/</c> beside the built assembly - the same
/// place, and for the same reason, as the OurAirports seed CSVs: AppContext.BaseDirectory resolves
/// identically under "dotnet run" and a published exe, where the content root does not.
///
/// <para><b>Two files, because one is useless without the other.</b> Boundaries.geojson holds the
/// polygons but keys them only by an opaque boundary id; VATSpy.dat's [FIRs] and [UIRs] sections
/// are what turn a controller callsign into that id. There is no way to get from "LON_CTR" to a
/// shape without both.</para>
///
/// <para>Both files are shipped verbatim (gzip-compressed, which is not a modification) and parsed
/// at runtime rather than pre-processed into an FSOps-specific format. That is a licence decision
/// as much as a technical one: the source data is CC BY-SA 4.0, and a derived database would carry
/// share-alike into FSOps' own files, whereas redistributing the originals with attribution does
/// not. See data/vatspy/ATTRIBUTION.txt.</para>
///
/// <para>Loading is lazy and never throws. A missing or malformed file leaves
/// <see cref="Available"/> false, every <see cref="Resolve"/> returns null, and en-route
/// controllers simply are not shown - exactly the behaviour FSOps had before this class existed.
/// Nothing here is ever on the path of a flight, so there is no failure mode worth an exception.</para>
/// </summary>
public sealed class VatSpyBoundarySource : IAtcBoundarySource
{
    /// <summary>Sub-folder of the bundled <c>data/</c> directory the two files live in.</summary>
    public const string DataFolderName = "vatspy";

    private const string BoundariesFileName = "Boundaries.geojson";
    private const string DirectoryFileName = "VATSpy.dat";

    private readonly string _directory;
    private readonly ILogger<VatSpyBoundarySource> _logger;
    private readonly Lazy<VatSpyIndex> _index;

    /// <summary>Production constructor - resolves the bundled data beside the assembly.</summary>
    public VatSpyBoundarySource(ILogger<VatSpyBoundarySource> logger)
        : this(Path.Combine(AppContext.BaseDirectory, "data", DataFolderName), logger)
    {
    }

    public VatSpyBoundarySource(string directory, ILogger<VatSpyBoundarySource> logger)
    {
        _directory = directory;
        _logger = logger;
        // ExecutionAndPublication so a burst of concurrent /operations/atc requests on a cold
        // start parses once rather than once per request.
        _index = new Lazy<VatSpyIndex>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool Available => _index.Value.Boundaries.Count > 0 && _index.Value.ByCallsignPrefix.Count > 0;

    public AtcBoundary? Resolve(string callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return null;
        }

        var index = _index.Value;
        var target = MatchPrefix(callsign, index.ByCallsignPrefix);
        if (target is null)
        {
            return null;
        }

        var polygons = new List<AtcBoundaryPolygon>();
        foreach (var boundaryId in target.BoundaryIds)
        {
            if (index.Boundaries.TryGetValue(boundaryId, out var found))
            {
                polygons.AddRange(found);
            }
        }

        // A directory entry pointing at geometry that isn't in the geojson (a mid-cycle
        // inconsistency upstream) resolves to nothing rather than to an empty shape.
        return polygons.Count == 0 ? null : new AtcBoundary(target.Id, target.Name, polygons);
    }

    /// <summary>
    /// Longest-first match of a callsign against the known prefixes. The last segment (the position
    /// type - CTR or FSS) is dropped, then what remains is tried longest-first: "ADR_E_CTR" tries
    /// "ADR_E" and then "ADR"; "LON_S_CTR" tries "LON_S" and then "LON"; "EGTT_CTR" tries "EGTT".
    ///
    /// <para><b>Do not "simplify" this to splitting on the first underscore.</b> That is the
    /// obvious reading of "callsign prefix" and it is wrong, which was found by reading the real
    /// VATSpy.dat rather than trusting a description of its format: upstream prefixes routinely
    /// contain underscores themselves. "ADR_E" is Adria Radar *East* and "AFRN_E" the eastern North
    /// Africa upper region, both distinct entries with their own smaller geometry.</para>
    ///
    /// <para>First-segment matching would resolve ADR_E_CTR to the whole of Adria - a boundary
    /// several times larger than the airspace that controller is actually working - and draw it
    /// with complete confidence, with nothing on screen to suggest it was a guess. No test would
    /// have caught it either, because the code would have been doing exactly what its own
    /// specification said. That failure mode is the entire reason this feature is careful: a wrong
    /// boundary on a map reads as authoritative, and someone deciding whether to fly online
    /// tonight would have believed it.</para>
    /// </summary>
    internal static BoundaryTarget? MatchPrefix(
        string callsign, IReadOnlyDictionary<string, BoundaryTarget> byPrefix)
    {
        var segments = callsign.Trim().ToUpperInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        if (segments.Length == 1)
        {
            return byPrefix.TryGetValue(segments[0], out var only) ? only : null;
        }

        for (var length = segments.Length - 1; length >= 1; length--)
        {
            var candidate = string.Join('_', segments, 0, length);
            if (byPrefix.TryGetValue(candidate, out var target))
            {
                return target;
            }
        }

        return null;
    }

    private VatSpyIndex Load()
    {
        try
        {
            var boundariesPath = ResolveFile(BoundariesFileName);
            var directoryPath = ResolveFile(DirectoryFileName);
            if (boundariesPath is null || directoryPath is null)
            {
                _logger.LogInformation(
                    "VAT-Spy boundary data not found in {Directory} - en-route ATC coverage will not be shown", _directory);
                return VatSpyIndex.Empty;
            }

            using var boundariesStream = OpenRead(boundariesPath);
            var boundaries = ParseBoundaries(boundariesStream);

            using var directoryStream = OpenRead(directoryPath);
            using var reader = new StreamReader(directoryStream, Encoding.UTF8);
            var prefixes = ParseDirectory(reader.ReadToEnd());

            _logger.LogInformation(
                "Loaded {BoundaryCount} ATC boundaries and {PrefixCount} callsign prefixes from VAT-Spy data",
                boundaries.Count, prefixes.Count);
            return new VatSpyIndex(boundaries, prefixes);
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException
            or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            _logger.LogWarning(ex, "VAT-Spy boundary data could not be read - en-route ATC coverage will not be shown");
            return VatSpyIndex.Empty;
        }
    }

    /// <summary>Prefers the shipped <c>.gz</c>, falling back to an uncompressed file so the two
    /// originals can be dropped in as-is while working on them.</summary>
    private string? ResolveFile(string fileName)
    {
        var compressed = Path.Combine(_directory, fileName + ".gz");
        if (File.Exists(compressed)) return compressed;
        var plain = Path.Combine(_directory, fileName);
        return File.Exists(plain) ? plain : null;
    }

    private static Stream OpenRead(string path)
    {
        var file = File.OpenRead(path);
        return path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
    }

    /// <summary>
    /// Reads a GeoJSON FeatureCollection into boundary-id -&gt; polygons. Features sharing an id
    /// are merged rather than overwriting each other: the upstream data splits some regions into
    /// several features under one id (oceanic and domestic parts of the same FIR, or the two halves
    /// of a region that straddles the antimeridian), and keeping only the last would silently drop
    /// half the airspace.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<AtcBoundaryPolygon>> ParseBoundaries(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, List<AtcBoundaryPolygon>>(StringComparer.OrdinalIgnoreCase);

        if (!document.RootElement.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, IReadOnlyList<AtcBoundaryPolygon>>();
        }

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object ||
                !properties.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id) ||
                !feature.TryGetProperty("geometry", out var geometry) ||
                geometry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var polygons = ReadGeometry(geometry);
            if (polygons.Count == 0)
            {
                continue;
            }

            if (!result.TryGetValue(id, out var existing))
            {
                existing = new List<AtcBoundaryPolygon>();
                result[id] = existing;
            }

            existing.AddRange(polygons);
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<AtcBoundaryPolygon>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static List<AtcBoundaryPolygon> ReadGeometry(JsonElement geometry)
    {
        var polygons = new List<AtcBoundaryPolygon>();
        if (!geometry.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !geometry.TryGetProperty("coordinates", out var coordinates) ||
            coordinates.ValueKind != JsonValueKind.Array)
        {
            return polygons;
        }

        switch (typeElement.GetString())
        {
            case "Polygon":
                {
                    var polygon = ReadPolygon(coordinates);
                    if (polygon is not null) polygons.Add(polygon);
                    break;
                }

            case "MultiPolygon":
                foreach (var element in coordinates.EnumerateArray())
                {
                    var polygon = ReadPolygon(element);
                    if (polygon is not null) polygons.Add(polygon);
                }

                break;
        }

        return polygons;
    }

    private static AtcBoundaryPolygon? ReadPolygon(JsonElement polygonElement)
    {
        if (polygonElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var rings = new List<IReadOnlyList<GeoPoint>>();
        foreach (var ringElement in polygonElement.EnumerateArray())
        {
            if (ringElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var ring = new List<GeoPoint>();
            foreach (var position in ringElement.EnumerateArray())
            {
                if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2)
                {
                    continue;
                }

                var lon = position[0];
                var lat = position[1];
                if (lon.ValueKind != JsonValueKind.Number || lat.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                ring.Add(new GeoPoint(lon.GetDouble(), lat.GetDouble()));
            }

            // Fewer than three distinct points is not an area. Dropping the ring is right: a
            // degenerate shape would either render as nothing or, worse, as a sliver.
            if (ring.Count >= 4)
            {
                rings.Add(ring);
            }
        }

        return rings.Count == 0 ? null : new AtcBoundaryPolygon(rings);
    }

    /// <summary>
    /// Reads the [FIRs] and [UIRs] sections of VATSpy.dat into callsign-prefix -&gt; boundary ids.
    ///
    /// <para>[FIRs] rows are <c>ICAO|Name|CallsignPrefix|BoundaryId</c>, either of the last two
    /// possibly blank, in which case the ICAO stands in for it. [UIRs] rows are
    /// <c>ICAO|Name|FirIds</c> where the third field lists the FIRs the upper region is made of -
    /// which is why a UIR resolves to a union of boundaries rather than one.</para>
    ///
    /// <para>Both the ICAO and the callsign prefix are registered as lookup keys, because both
    /// forms are flown: EGTT_CTR and LON_CTR are the same airspace. The callsign prefix is written
    /// second and so wins any collision - it is the form that only ever means a controlling
    /// position, whereas an ICAO could in principle collide with something else.</para>
    /// </summary>
    internal static IReadOnlyDictionary<string, BoundaryTarget> ParseDirectory(string text)
    {
        var result = new Dictionary<string, BoundaryTarget>(StringComparer.OrdinalIgnoreCase);
        var firs = new Dictionary<string, BoundaryTarget>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                section = line.Trim('[', ']').ToUpperInvariant();
                continue;
            }

            var fields = line.Split('|');
            switch (section)
            {
                case "FIRS" when fields.Length >= 4:
                    {
                        var icao = fields[0].Trim().ToUpperInvariant();
                        if (icao.Length == 0) break;
                        var name = fields[1].Trim();
                        var prefix = fields[2].Trim().ToUpperInvariant();
                        var boundaryId = fields[3].Trim();
                        if (boundaryId.Length == 0) boundaryId = icao;
                        if (name.Length == 0) name = icao;

                        var target = new BoundaryTarget(icao, name, new[] { boundaryId });
                        firs[icao] = target;
                        result[icao] = target;
                        if (prefix.Length > 0) result[prefix] = target;
                        break;
                    }

                case "UIRS" when fields.Length >= 3:
                    {
                        var icao = fields[0].Trim().ToUpperInvariant();
                        if (icao.Length == 0) break;
                        var name = fields[1].Trim();
                        if (name.Length == 0) name = icao;

                        var boundaryIds = new List<string>();
                        foreach (var member in fields[2].Split(',', ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var key = member.ToUpperInvariant();
                            // A UIR lists the FIRs it sits above, so its geometry is theirs. Fall
                            // back to treating the member as a boundary id directly when it isn't
                            // a known FIR - upstream occasionally names geometry that has no
                            // [FIRs] row of its own.
                            boundaryIds.AddRange(firs.TryGetValue(key, out var fir) ? fir.BoundaryIds : new[] { key });
                        }

                        if (boundaryIds.Count == 0) break;
                        // A real collision, not a hypothetical one: "ADR_U" is both a [FIRs]
                        // callsign prefix (Adria Radar upper, geometry ADR) and a [UIRs] ICAO
                        // (a six-FIR union). Letting the FIR row win matches how VAT-Spy's own
                        // client resolves a controller - FIRs first, UIRs only as a fallback - and
                        // errs towards the smaller, more specific claim, which is the right way to
                        // be wrong here.
                        if (!result.ContainsKey(icao))
                        {
                            result[icao] = new BoundaryTarget(icao, name, boundaryIds);
                        }

                        break;
                    }
            }
        }

        return result;
    }

    /// <summary>One resolvable callsign prefix: what to call it, and which boundary geometry it
    /// draws from (more than one only for a UIR).</summary>
    internal sealed record BoundaryTarget(string Id, string Name, IReadOnlyList<string> BoundaryIds);

    private sealed record VatSpyIndex(
        IReadOnlyDictionary<string, IReadOnlyList<AtcBoundaryPolygon>> Boundaries,
        IReadOnlyDictionary<string, BoundaryTarget> ByCallsignPrefix)
    {
        public static VatSpyIndex Empty { get; } = new(
            new Dictionary<string, IReadOnlyList<AtcBoundaryPolygon>>(),
            new Dictionary<string, BoundaryTarget>());
    }
}

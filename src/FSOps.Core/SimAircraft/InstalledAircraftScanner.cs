using System.Text.Json;

namespace FSOps.Core.SimAircraft;

/// <summary>
/// Looks at what is actually on the player's disk and reports what it found.
///
/// <para><b>Order of evidence, strongest first.</b> A package in the Community folder is the
/// strongest signal there is - somebody installed it deliberately, and its own configuration names
/// the ICAO type. Base-content package folders are the next best: presence proves the sim has the
/// content, though absence proves nothing because MSFS 2024 streams most of it. Below that there is
/// only the edition setting and the player's own ticks, and neither of those is this class's
/// business.</para>
///
/// <para><b>Everything here fails soft.</b> A missing folder, a folder that is not a Community
/// folder, an unreadable manifest, a package with no aircraft configuration - each of those is a
/// result to report, never an exception to throw and never a reason to conclude the player owns
/// nothing. The one thing this must never do is turn "I could not look" into "you do not have it".</para>
/// </summary>
public sealed class InstalledAircraftScanner
{
    /// <summary>
    /// How many aircraft configurations to read inside one package before giving up on it. Real
    /// aircraft packages hold a handful; FSLTL's traffic base holds 2,551 and none of them is
    /// flyable. The cap keeps a scan of somebody's whole sim folder to a moment's work, and losing
    /// the tail of a package that big costs nothing, because everything in it is AI traffic anyway.
    /// </summary>
    private const int MaxConfigsPerPackage = 200;

    /// <summary>
    /// The folders base content lives in, relative to the Packages root (the parent of Community).
    /// MSFS 2024 keeps streamed content in <c>StreamedPackages</c>; the OneStore folders are where
    /// MSFS 2020 and fully-downloaded MSFS 2024 content sits. Every one of them is optional.
    /// </summary>
    private static readonly string[] BaseContentFolders =
    {
        "StreamedPackages",
        Path.Combine("Official", "OneStore"),
        Path.Combine("Official2024", "OneStore"),
        Path.Combine("Official2020", "OneStore"),
        "OneStore",
        "Community2024",
    };

    public AircraftScanResult Scan(string? communityFolderPath, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(communityFolderPath))
        {
            return AircraftScanResult.NotRun(AircraftScanOutcome.NoFolder, null, utcNow);
        }

        var path = communityFolderPath.Trim();
        if (!SafeDirectoryExists(path))
        {
            return AircraftScanResult.NotRun(AircraftScanOutcome.FolderMissing, path, utcNow);
        }

        var packageFolders = SafeEnumerateDirectories(path);
        var aircraftPackages = new List<ScannedAircraftPackage>();
        var packagesWithManifest = 0;

        foreach (var packageFolder in packageFolders)
        {
            var manifestPath = Path.Combine(packageFolder, "manifest.json");
            var manifest = ReadManifest(manifestPath);
            if (manifest is null)
            {
                continue;
            }

            packagesWithManifest++;

            // Only AIRCRAFT. A LIVERY package repaints an aircraft somebody already has - counting
            // one would claim an aircraft they may not own at all.
            if (!string.Equals(manifest.Value.ContentType, "AIRCRAFT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var folderName = Path.GetFileName(packageFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var title = string.IsNullOrWhiteSpace(manifest.Value.Title) ? folderName : manifest.Value.Title!;

            aircraftPackages.AddRange(IdentifyPackage(packageFolder, folderName, title));
        }

        if (packagesWithManifest == 0)
        {
            return AircraftScanResult.NotRun(AircraftScanOutcome.NotAPackagesFolder, path, utcNow);
        }

        return new AircraftScanResult(
            AircraftScanOutcome.Scanned,
            path,
            utcNow,
            packagesWithManifest,
            aircraftPackages,
            ScanBaseContent(path));
    }

    /// <summary>
    /// Walks one package's aircraft configurations and returns what it delivers. Returns a single
    /// unidentified entry when the package declares itself as an aircraft but nothing readable can
    /// be found inside it - which happens with instrument-only and cockpit-only add-ons, and with
    /// packages whose SimObjects folder is missing or unreadable. Saying "there is an aircraft
    /// package here I could not read" is honest; saying nothing would look like a clean scan.
    /// </summary>
    private static List<ScannedAircraftPackage> IdentifyPackage(string packageFolder, string folderName, string title)
    {
        var found = new List<ScannedAircraftPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawUnmatched = new List<string>();
        var read = 0;

        try
        {
            // Enumerated lazily rather than materialised: FSLTL's traffic base holds thousands of
            // configs in a deep tree, and the cap below is only worth having if the walk stops too.
            // The whole loop is guarded because a lazy walk raises its IO failures from MoveNext,
            // where a try around the call that created it would never see them.
            foreach (var configPath in EnumerateAircraftConfigs(packageFolder))
            {
                if (++read > MaxConfigsPerPackage)
                {
                    break;
                }

                var lines = SafeReadLines(configPath);
                if (lines is null)
                {
                    continue;
                }

                var config = AircraftConfigReader.Parse(lines);
                if (config.IsAiTrafficOnly)
                {
                    continue;
                }

                var aircraft = ContractAircraftCatalogue.Find(config.TypeDesignator)
                    ?? ContractAircraftCatalogue.FindByText(config.Title, config.AtcModel);

                if (aircraft is not null)
                {
                    if (seen.Add(aircraft.TypeDesignator))
                    {
                        found.Add(new ScannedAircraftPackage(folderName, title, config.TypeDesignator, aircraft.TypeDesignator));
                    }

                    continue;
                }

                var raw = ContractAircraftCatalogue.NormaliseDesignator(config.TypeDesignator);
                if (raw is not null && rawUnmatched.Count < 8 && !rawUnmatched.Contains(raw, StringComparer.OrdinalIgnoreCase))
                {
                    rawUnmatched.Add(raw);
                }
            }
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            // Keep whatever was found before the walk failed. A package that becomes unreadable
            // half way through has still told us about the aircraft we already identified.
        }

        if (found.Count > 0)
        {
            return found;
        }

        // Nothing matched. Report the package anyway, carrying whatever designator it did declare so
        // somebody reading the list can see what FSOps did not recognise.
        return new List<ScannedAircraftPackage>
        {
            new(folderName, title, rawUnmatched.Count > 0 ? string.Join(", ", rawUnmatched) : null, null),
        };
    }

    /// <summary>
    /// Looks for base-content package folders alongside Community. Only folder names are read -
    /// MSFS 2024 keeps streamed content in opaque <c>.fsarchive</c> files with no configuration to
    /// parse - so this matches against the catalogue's known base package ids.
    ///
    /// <para>Those ids are always the flyable packages, never a <c>passiveaircraft-</c> one. That
    /// distinction is load-bearing rather than tidy: a Standard install carries
    /// <c>passiveaircraft-</c> models of the Deluxe and Premium Deluxe aircraft so AI traffic can
    /// use them, and matching one would report an aircraft the player cannot load.</para>
    /// </summary>
    private static List<string> ScanBaseContent(string communityFolderPath)
    {
        var packagesRoot = SafeParentOf(communityFolderPath);
        if (packagesRoot is null)
        {
            return new List<string>();
        }

        var designators = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in BaseContentFolders)
        {
            var folder = Path.Combine(packagesRoot, relative);
            if (!SafeDirectoryExists(folder))
            {
                continue;
            }

            foreach (var packageFolder in SafeEnumerateDirectories(folder))
            {
                var name = Path.GetFileName(packageFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!ContractAircraftCatalogue.ByBasePackageId.TryGetValue(name, out var aircraft))
                {
                    continue;
                }

                if (seen.Add(aircraft.TypeDesignator))
                {
                    designators.Add(aircraft.TypeDesignator);
                }
            }
        }

        designators.Sort(StringComparer.OrdinalIgnoreCase);
        return designators;
    }

    private readonly record struct PackageManifest(string? ContentType, string? Title);

    private static PackageManifest? ReadManifest(string manifestPath)
    {
        string json;
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            json = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new PackageManifest(
                root.TryGetProperty("content_type", out var contentType) && contentType.ValueKind == JsonValueKind.String
                    ? contentType.GetString()
                    : null,
                root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                    ? title.GetString()
                    : null);
        }
        catch (JsonException)
        {
            // A manifest that is not valid JSON still means "a package folder is here" - which is
            // what stops one broken add-on turning a real Community folder into "not a Community
            // folder" and wiping out everything else the scan found.
            return new PackageManifest(null, null);
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return false;
        }
    }

    private static string? SafeParentOf(string path)
    {
        try
        {
            return Directory.GetParent(path)?.FullName;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateAircraftConfigs(string path) =>
        // IgnoreInaccessible keeps one locked or permission-denied subfolder from aborting the whole
        // walk part-way through, which would otherwise read as "this package has nothing".
        Directory.EnumerateFiles(path, "aircraft.cfg", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        });

    private static IReadOnlyList<string>? SafeReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return null;
        }
    }

    private static bool IsExpectedIoFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}

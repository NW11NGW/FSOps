using System.Text.Json.Serialization;

namespace FSOps.Core.SimAircraft;

/// <summary>
/// How a scan ended. Every one of these except <see cref="Scanned"/> means "could not look", and
/// none of them ever means "you own nothing" - see <see cref="AircraftScanResult"/>.
/// </summary>
public enum AircraftScanOutcome
{
    /// <summary>No Community folder has been configured and none could be found automatically.</summary>
    NoFolder = 0,

    /// <summary>A folder is configured but is not there any more (moved sim, unplugged drive).</summary>
    FolderMissing = 1,

    /// <summary>
    /// The folder exists but holds no packages at all - no subfolder in it has a manifest.json.
    /// Almost always somebody pointing FSOps at the sim's install folder rather than at Community.
    /// </summary>
    NotAPackagesFolder = 2,

    /// <summary>The folder was read.</summary>
    Scanned = 3,
}

/// <summary>
/// One aircraft package a scan looked at. Recorded whether or not it could be identified, because
/// "there is an aircraft package here that FSOps does not recognise" is useful to show somebody -
/// it is the difference between a scan that found nothing and a scan that did not run.
/// </summary>
/// <param name="PackageFolder">The folder name inside Community, e.g. <c>fnx-aircraft-320</c>.</param>
/// <param name="PackageTitle">The package's own title from its manifest, e.g. "Fenix Airbus A320".</param>
/// <param name="RawDesignator">
/// Whatever the package's aircraft configuration declared, before normalising - null when it
/// declared nothing readable.
/// </param>
/// <param name="TypeDesignator">
/// The catalogue entry this resolved to, or null when the package could not be matched to anything
/// FSOps knows about.
/// </param>
public sealed record ScannedAircraftPackage(
    string PackageFolder,
    string PackageTitle,
    string? RawDesignator,
    string? TypeDesignator);

/// <summary>
/// What a scan of the player's simulator folders found.
///
/// <para><b>A scan is evidence, never a verdict.</b> It can prove an aircraft IS present; it can
/// never prove one is absent. MSFS 2024 streams most of its base content and only keeps on disk
/// what has actually been used, so an aircraft the player owns and has simply not flown yet leaves
/// no trace to find. Nothing in FSOps may turn a scan miss into an exclusion - the edition setting
/// and the player's own ticks decide that.</para>
/// </summary>
/// <param name="Outcome">Whether the scan could look at all.</param>
/// <param name="CommunityFolderPath">The folder that was read, or the one that could not be.</param>
/// <param name="ScannedUtc">When this scan ran.</param>
/// <param name="PackagesInspected">Every package folder in Community, of any content type.</param>
/// <param name="AircraftPackages">
/// The aircraft packages found, identified or not. Liveries and scenery are not in here: a package
/// declaring <c>content_type: "LIVERY"</c> adds a paint job to an aircraft somebody already has,
/// and counting one as an aircraft would claim an aircraft the player may not own.
/// </param>
/// <param name="BasePackageTypeDesignators">
/// Catalogue designators recognised from base-content package folders present on disk (the
/// streamed and official package folders). Presence is real evidence; absence is not.
/// </param>
public sealed record AircraftScanResult(
    AircraftScanOutcome Outcome,
    string? CommunityFolderPath,
    DateTimeOffset ScannedUtc,
    int PackagesInspected,
    IReadOnlyList<ScannedAircraftPackage> AircraftPackages,
    IReadOnlyList<string> BasePackageTypeDesignators)
{
    /// <summary>
    /// Every catalogue designator this scan actually proved is installed, deduplicated. Derived
    /// rather than stored, and kept out of the persisted JSON so the stored record can never
    /// disagree with the two lists it is computed from.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> IdentifiedTypeDesignators =>
        AircraftPackages
            .Select(p => p.TypeDesignator)
            .Where(d => d is not null)
            .Concat(BasePackageTypeDesignators)
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static AircraftScanResult NotRun(AircraftScanOutcome outcome, string? path, DateTimeOffset utcNow) =>
        new(outcome, path, utcNow, 0, Array.Empty<ScannedAircraftPackage>(), Array.Empty<string>());
}

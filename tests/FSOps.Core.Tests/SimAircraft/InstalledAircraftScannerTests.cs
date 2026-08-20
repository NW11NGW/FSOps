using FSOps.Core.SimAircraft;

namespace FSOps.Core.Tests.SimAircraft;

/// <summary>
/// The scanner, against folders built to look like the real thing.
///
/// <para>Most of these tests are about the ways a scan fails, because that is where the damage is.
/// A scan that cannot find the folder, is pointed at the wrong folder, or meets a package it cannot
/// read must come back saying so - never as an empty result that reads like "you own nothing", and
/// never as an exception that takes the settings page down.</para>
/// </summary>
public class InstalledAircraftScannerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fsops-scanner-" + Guid.NewGuid().ToString("N"));

    private readonly InstalledAircraftScanner _scanner = new();

    public InstalledAircraftScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a passing test over.
        }
    }

    [Fact]
    public void Scan_WithNoFolderConfigured_SaysSoRatherThanReturningNothingFound()
    {
        var result = _scanner.Scan(null, Now);

        Assert.Equal(AircraftScanOutcome.NoFolder, result.Outcome);
        Assert.Empty(result.IdentifiedTypeDesignators);
        Assert.Equal(Now, result.ScannedUtc);
    }

    [Fact]
    public void Scan_WithABlankFolder_IsTheSameAsNoFolder()
    {
        Assert.Equal(AircraftScanOutcome.NoFolder, _scanner.Scan("   ", Now).Outcome);
    }

    [Fact]
    public void Scan_WithAFolderThatIsNotThere_ReportsItMissing()
    {
        var result = _scanner.Scan(Path.Combine(_root, "moved-to-another-drive"), Now);

        Assert.Equal(AircraftScanOutcome.FolderMissing, result.Outcome);
        Assert.Empty(result.IdentifiedTypeDesignators);
    }

    /// <summary>
    /// Somebody pointing FSOps at the simulator's install folder rather than at Community is the
    /// most likely wrong answer there is, and it must not look like a clean scan of an empty hangar.
    /// </summary>
    [Fact]
    public void Scan_WithAFolderThatHoldsNoPackages_SaysItIsNotAPackagesFolder()
    {
        var notCommunity = Path.Combine(_root, "FlightSimulator");
        Directory.CreateDirectory(Path.Combine(notCommunity, "bin"));
        File.WriteAllText(Path.Combine(notCommunity, "readme.txt"), "not a package");

        var result = _scanner.Scan(notCommunity, Now);

        Assert.Equal(AircraftScanOutcome.NotAPackagesFolder, result.Outcome);
        Assert.Empty(result.IdentifiedTypeDesignators);
    }

    [Fact]
    public void Scan_FindsAnAddOnAircraftFromItsOwnConfiguration()
    {
        var community = CreateCommunity();
        WritePackage(community, "fnx-aircraft-320", "AIRCRAFT", "Fenix Airbus A320");
        WriteAircraftConfig(
            community,
            "fnx-aircraft-320",
            Path.Combine("SimObjects", "Airplanes", "FNX_32X", "attachments", "fnx", "Part_Exterior_Fuselage_A320", "config"),
            new[]
            {
                "[GENERAL]",
                "atc_model = \"TT:ATCCOM.AC_MODEL A320.0.text\"",
                "icao_type_designator = \"A320\"",
            });

        var result = _scanner.Scan(community, Now);

        Assert.Equal(AircraftScanOutcome.Scanned, result.Outcome);
        Assert.Equal(new[] { "A320" }, result.IdentifiedTypeDesignators);

        var package = Assert.Single(result.AircraftPackages);
        Assert.Equal("fnx-aircraft-320", package.PackageFolder);
        Assert.Equal("Fenix Airbus A320", package.PackageTitle);
        Assert.Equal("A320", package.TypeDesignator);
    }

    /// <summary>One package, several presets, several designators - iniBuilds' A350 ships both.</summary>
    [Fact]
    public void Scan_FindsEveryDistinctAircraftInsideOnePackage()
    {
        var community = CreateCommunity();
        WritePackage(community, "inibuilds-aircraft-a350", "AIRCRAFT", "A350 Airliner");
        WriteAircraftConfig(community, "inibuilds-aircraft-a350", Path.Combine("SimObjects", "Airplanes", "A350", "presets", "a900"), new[]
        {
            "[GENERAL]",
            "atc_model = \"A350-900\"",
            "icao_type_designator =A359",
        });
        WriteAircraftConfig(community, "inibuilds-aircraft-a350", Path.Combine("SimObjects", "Airplanes", "A350", "presets", "a900ulr"), new[]
        {
            "[GENERAL]",
            "icao_type_designator =A359 ULR",
        });
        WriteAircraftConfig(community, "inibuilds-aircraft-a350", Path.Combine("SimObjects", "Airplanes", "A350", "presets", "a1000"), new[]
        {
            "[GENERAL]",
            "icao_type_designator =A35K",
        });

        var result = _scanner.Scan(community, Now);

        Assert.Equal(new[] { "A359", "A35K" }, result.IdentifiedTypeDesignators);
    }

    /// <summary>
    /// A livery package repaints an aircraft somebody already has. Counting one would claim an
    /// aircraft they may not own at all - and the user's own Community folder holds four of them.
    /// </summary>
    [Fact]
    public void Scan_IgnoresLiveryAndSceneryPackages()
    {
        var community = CreateCommunity();
        WritePackage(community, "fnx-aircraft-320-liveries", "LIVERY", "Fenix A320 Liveries");
        WriteAircraftConfig(community, "fnx-aircraft-320-liveries", Path.Combine("SimObjects", "Airplanes", "FNX"), new[]
        {
            "[GENERAL]",
            "icao_type_designator = \"A320\"",
        });
        WritePackage(community, "pyreegue-airport-egph-edinburgh", "SCENERY", "EGPH Edinburgh Airport II");

        var result = _scanner.Scan(community, Now);

        Assert.Equal(AircraftScanOutcome.Scanned, result.Outcome);
        Assert.Equal(2, result.PackagesInspected);
        Assert.Empty(result.AircraftPackages);
        Assert.Empty(result.IdentifiedTypeDesignators);
    }

    /// <summary>
    /// FSLTL's traffic base is an AIRCRAFT package holding thousands of AI-only models. None of them
    /// is flyable, so none of them may end up in the hangar.
    /// </summary>
    [Fact]
    public void Scan_IgnoresAiTrafficModelsInsideAnAircraftPackage()
    {
        var community = CreateCommunity();
        WritePackage(community, "fsltl-traffic-base", "AIRCRAFT", "FSLTL Traffic Base");
        WriteAircraftConfig(community, "fsltl-traffic-base", Path.Combine("SimObjects", "Airplanes", "FSLTL_A320"), new[]
        {
            "[GENERAL]",
            "icao_type_designator = \"A320\"",
            "[FLTSIM.0]",
            "title = \"FSLTL A320 BAW\"",
            "isAirTraffic = 1",
            "isUserSelectable = 0",
        });

        var result = _scanner.Scan(community, Now);

        Assert.Empty(result.IdentifiedTypeDesignators);

        // Still reported, so the player can see FSOps looked at it and found nothing flyable.
        var package = Assert.Single(result.AircraftPackages);
        Assert.Null(package.TypeDesignator);
    }

    /// <summary>
    /// Instrument-only and cockpit-only add-ons declare themselves as aircraft but have no aircraft
    /// configuration to read. Saying "there is a package here I could not identify" is honest;
    /// saying nothing at all would look like a clean scan.
    /// </summary>
    [Fact]
    public void Scan_ReportsAnAircraftPackageWithNoReadableConfiguration()
    {
        var community = CreateCommunity();
        WritePackage(community, "flybywire-aircraft-a380-842", "AIRCRAFT", "A380X Instruments (Stable)");

        var result = _scanner.Scan(community, Now);

        Assert.Equal(AircraftScanOutcome.Scanned, result.Outcome);
        var package = Assert.Single(result.AircraftPackages);
        Assert.Equal("A380X Instruments (Stable)", package.PackageTitle);
        Assert.Null(package.TypeDesignator);
        Assert.Null(package.RawDesignator);
        Assert.Empty(result.IdentifiedTypeDesignators);
    }

    /// <summary>
    /// A package whose configuration names a type FSOps has never heard of is carried through so it
    /// can be shown. This is what somebody sees when they install something the catalogue is missing.
    /// </summary>
    [Fact]
    public void Scan_CarriesThroughADesignatorItDoesNotRecognise()
    {
        var community = CreateCommunity();
        WritePackage(community, "someone-aircraft-an225", "AIRCRAFT", "Antonov An-225 Mriya");
        WriteAircraftConfig(community, "someone-aircraft-an225", Path.Combine("SimObjects", "Airplanes", "An225"), new[]
        {
            "[GENERAL]",
            "icao_type_designator = \"A225\"",
        });

        var package = Assert.Single(_scanner.Scan(community, Now).AircraftPackages);

        Assert.Null(package.TypeDesignator);
        Assert.Equal("A225", package.RawDesignator);
    }

    /// <summary>
    /// One broken add-on must not be able to turn a real Community folder into "not a Community
    /// folder" and wipe out everything else the scan found.
    /// </summary>
    [Fact]
    public void Scan_SurvivesAPackageWhoseManifestIsNotValidJson()
    {
        var community = CreateCommunity();
        Directory.CreateDirectory(Path.Combine(community, "broken-package"));
        File.WriteAllText(Path.Combine(community, "broken-package", "manifest.json"), "{ this is not json");

        WritePackage(community, "fnx-aircraft-320", "AIRCRAFT", "Fenix Airbus A320");
        WriteAircraftConfig(community, "fnx-aircraft-320", Path.Combine("SimObjects", "Airplanes", "FNX"), new[]
        {
            "[GENERAL]",
            "icao_type_designator = \"A320\"",
        });

        var result = _scanner.Scan(community, Now);

        Assert.Equal(AircraftScanOutcome.Scanned, result.Outcome);
        Assert.Equal(2, result.PackagesInspected);
        Assert.Equal(new[] { "A320" }, result.IdentifiedTypeDesignators);
    }

    /// <summary>
    /// Base content is recognised by folder name, because MSFS 2024 keeps streamed content in
    /// opaque archives with nothing to parse.
    /// </summary>
    [Fact]
    public void Scan_RecognisesBaseContentPackagesAlongsideCommunity()
    {
        var community = CreateCommunity();
        WritePackage(community, "placeholder", "SCENERY", "Something");

        var streamed = Path.Combine(_root, "Packages", "StreamedPackages");
        Directory.CreateDirectory(Path.Combine(streamed, "fs24-asobo-aircraft-c172sp-classic"));
        Directory.CreateDirectory(Path.Combine(streamed, "fs24-asobo-aircraft-b737max"));
        Directory.CreateDirectory(Path.Combine(streamed, "fs20-asobo-aircraft-c152-livery-kenmore"));

        var result = _scanner.Scan(community, Now);

        Assert.Equal(new[] { "B38M", "C172" }, result.BasePackageTypeDesignators);
        Assert.Equal(new[] { "B38M", "C172" }, result.IdentifiedTypeDesignators);
    }

    /// <summary>
    /// <b>The one that keeps a Standard-edition player honest.</b> A Standard install physically
    /// contains <c>passiveaircraft-</c> folders for the Deluxe and Premium Deluxe aircraft, so AI
    /// traffic can render them. Matching one would report the flyable Saab 340 or SkyCourier as
    /// installed for somebody who cannot load either.
    /// </summary>
    [Fact]
    public void Scan_NeverTreatsAPassiveAiOnlyBasePackageAsAnInstalledAircraft()
    {
        var community = CreateCommunity();
        WritePackage(community, "placeholder", "SCENERY", "Something");

        var streamed = Path.Combine(_root, "Packages", "StreamedPackages");
        Directory.CreateDirectory(Path.Combine(streamed, "fs24-microsoft-passiveaircraft-s340"));
        Directory.CreateDirectory(Path.Combine(streamed, "fs24-microsoft-passiveaircraft-c408"));
        Directory.CreateDirectory(Path.Combine(streamed, "fs24-asobo-passiveaircraft-atrfamily"));

        var result = _scanner.Scan(community, Now);

        Assert.Empty(result.BasePackageTypeDesignators);
    }

    private string CreateCommunity()
    {
        var community = Path.Combine(_root, "Packages", "Community");
        Directory.CreateDirectory(community);
        return community;
    }

    private static void WritePackage(string community, string folder, string contentType, string title)
    {
        var packageFolder = Path.Combine(community, folder);
        Directory.CreateDirectory(packageFolder);
        File.WriteAllText(
            Path.Combine(packageFolder, "manifest.json"),
            $$"""{"content_type":"{{contentType}}","title":"{{title}}","creator":"Test"}""");
    }

    private static void WriteAircraftConfig(string community, string folder, string relativePath, string[] lines)
    {
        var configFolder = Path.Combine(community, folder, relativePath);
        Directory.CreateDirectory(configFolder);
        File.WriteAllLines(Path.Combine(configFolder, "aircraft.cfg"), lines);
    }
}

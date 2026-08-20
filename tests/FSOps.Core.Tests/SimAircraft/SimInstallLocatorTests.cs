using FSOps.Core.SimAircraft;

namespace FSOps.Core.Tests.SimAircraft;

/// <summary>
/// Finding the Community folder without asking. The interesting case is the one a hardcoded path
/// cannot handle: somebody who moved their packages to a second drive, which is the normal thing to
/// do once a sim install runs to several hundred gigabytes.
/// </summary>
public class SimInstallLocatorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fsops-locator-" + Guid.NewGuid().ToString("N"));

    public SimInstallLocatorTests() => Directory.CreateDirectory(_root);

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
    public void ReadInstalledPackagesPath_ReadsTheKeyTheSimulatorActuallyWrites()
    {
        // Copied verbatim from a real Microsoft Store MSFS 2024 UserCfg.opt.
        var userCfg = WriteUserCfg(
            "InstalledPackagesPath \"C:\\Users\\someone\\AppData\\Local\\Packages\\Microsoft.Limitless_8wekyb3d8bbwe\\LocalCache\\Packages\"");

        Assert.Equal(
            @"C:\Users\someone\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages",
            SimInstallLocator.ReadInstalledPackagesPath(userCfg));
    }

    [Fact]
    public void ReadInstalledPackagesPath_ReturnsNullForAFileThatIsNotThereOrDoesNotCarryTheKey()
    {
        Assert.Null(SimInstallLocator.ReadInstalledPackagesPath(Path.Combine(_root, "nope.opt")));
        Assert.Null(SimInstallLocator.ReadInstalledPackagesPath(WriteUserCfg("SomeOtherKey \"value\"")));
        Assert.Null(SimInstallLocator.ReadInstalledPackagesPath(WriteUserCfg("InstalledPackagesPath \"\"")));
    }

    /// <summary>
    /// The point of reading UserCfg.opt at all: the packages folder is wherever the player put it,
    /// and no list of default paths will ever contain a second drive.
    /// </summary>
    [Fact]
    public void FindCommunityFolders_PrefersWhereverTheSimulatorSaysItsPackagesAre()
    {
        var moved = Path.Combine(_root, "SecondDrive", "MSFS", "Packages");
        Directory.CreateDirectory(Path.Combine(moved, "Community"));
        var userCfg = WriteUserCfg($"InstalledPackagesPath \"{moved}\"");

        var defaultCommunity = Path.Combine(_root, "Default", "Packages", "Community");
        Directory.CreateDirectory(defaultCommunity);

        var found = SimInstallLocator.FindCommunityFolders(new[] { userCfg }, new[] { defaultCommunity });

        Assert.Equal(Path.Combine(moved, "Community"), found[0]);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void FindCommunityFolders_FallsBackToTheDefaultPathsWhenNoUserCfgCanBeRead()
    {
        var defaultCommunity = Path.Combine(_root, "Default", "Packages", "Community");
        Directory.CreateDirectory(defaultCommunity);

        var found = SimInstallLocator.FindCommunityFolders(
            new[] { Path.Combine(_root, "missing.opt") },
            new[] { defaultCommunity });

        Assert.Equal(new[] { defaultCommunity }, found);
    }

    /// <summary>
    /// No simulator on this machine is an ordinary answer, not an error. It must come back as an
    /// empty list so the caller can say "could not find it" rather than anything about ownership.
    /// </summary>
    [Fact]
    public void FindCommunityFolders_ReturnsAnEmptyListWhenNothingIsInstalled()
    {
        var found = SimInstallLocator.FindCommunityFolders(
            new[] { Path.Combine(_root, "missing.opt") },
            new[] { Path.Combine(_root, "also-missing", "Community") });

        Assert.Empty(found);
    }

    [Fact]
    public void FindCommunityFolders_NeverReturnsTheSameFolderTwice()
    {
        var packages = Path.Combine(_root, "Packages");
        var community = Path.Combine(packages, "Community");
        Directory.CreateDirectory(community);
        var userCfg = WriteUserCfg($"InstalledPackagesPath \"{packages}\"");

        Assert.Equal(new[] { community }, SimInstallLocator.FindCommunityFolders(new[] { userCfg }, new[] { community }));
    }

    private string WriteUserCfg(string line)
    {
        var path = Path.Combine(_root, $"UserCfg-{Guid.NewGuid():N}.opt");
        File.WriteAllLines(path, new[] { "{Version 1}", string.Empty, line, "AccessibilityOptions {" });
        return path;
    }
}

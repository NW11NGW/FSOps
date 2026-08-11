using FSOps.Desktop;

namespace FSOps.Desktop.Tests;

/// <summary>
/// Where the shell looks for FSOps.Server.exe. The ordering is the whole point: a developer
/// machine has both an installed copy and a bin folder, and picking the wrong one produces an app
/// that appears to work while running last week's server.
/// </summary>
public class ServerExecutableLocatorTests
{
    private const string InstallFolder = @"C:\Program Files\FSOps";
    private const string InstalledServer = @"C:\Program Files\FSOps\FSOps.Server.exe";

    private static Func<string, bool> Existing(params string[] paths) =>
        candidate => paths.Contains(candidate, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_copy_next_to_the_shell_is_used_in_a_normal_install()
    {
        var found = ServerExecutableLocator.Locate(
            InstallFolder,
            "Release",
            overridePath: null,
            fileExists: Existing(InstalledServer),
            directoryExists: _ => false);

        Assert.Equal(InstalledServer, found);
    }

    [Fact]
    public void An_explicit_override_wins_over_everything_else()
    {
        const string Override = @"D:\builds\FSOps.Server.exe";

        var found = ServerExecutableLocator.Locate(
            InstallFolder,
            "Release",
            overridePath: Override,
            fileExists: Existing(Override, InstalledServer),
            directoryExists: _ => false);

        Assert.Equal(Override, found);
    }

    [Fact]
    public void An_override_pointing_at_nothing_falls_through_rather_than_failing()
    {
        var found = ServerExecutableLocator.Locate(
            InstallFolder,
            "Release",
            overridePath: @"D:\gone\FSOps.Server.exe",
            fileExists: Existing(InstalledServer),
            directoryExists: _ => false);

        Assert.Equal(InstalledServer, found);
    }

    [Fact]
    public void A_missing_server_reports_nothing_rather_than_an_invented_path()
    {
        var found = ServerExecutableLocator.Locate(
            InstallFolder,
            "Release",
            overridePath: null,
            fileExists: _ => false,
            directoryExists: _ => false);

        Assert.Null(found);
    }

    [Fact]
    public void The_development_fallback_is_only_ever_tried_after_the_local_copy()
    {
        // Sibling-project discovery exists so the shell runs from its own bin folder during
        // development. If it ever came first, an installed app that happened to sit inside a
        // checkout would silently run a stale dev build.
        var candidates = ServerExecutableLocator
            .CandidatePaths(InstallFolder, "Release", overridePath: null, directoryExists: _ => true)
            .ToList();

        Assert.Equal(InstalledServer, candidates[0]);
    }

    [Fact]
    public void The_development_fallback_walks_up_to_the_solution_file_and_finds_the_server_bin_folder()
    {
        // Built against a real directory tree rather than the test assembly's own location, because
        // the repository builds with --artifacts-path in places that are not under the checkout -
        // a test that depended on where it happened to be running would pass or fail by accident.
        var root = Path.Combine(Path.GetTempPath(), "fsops-locator-" + Guid.NewGuid().ToString("N"));
        var shellBin = Path.Combine(root, "src", "FSOps.Desktop", "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(shellBin);
        File.WriteAllText(Path.Combine(root, "FSOps.sln"), string.Empty);

        try
        {
            var candidates = ServerExecutableLocator
                .CandidatePaths(shellBin, "Debug", overridePath: null, directoryExists: _ => true)
                .ToList();

            var debug = Path.Combine(root, "src", "FSOps.Server", "bin", "Debug", "net8.0", "FSOps.Server.exe");
            var release = Path.Combine(root, "src", "FSOps.Server", "bin", "Release", "net8.0", "FSOps.Server.exe");

            Assert.Contains(debug, candidates);
            Assert.Contains(release, candidates);

            // The shell's own configuration is preferred; the other one is a second chance only.
            Assert.True(candidates.IndexOf(debug) < candidates.IndexOf(release));

            // And still behind the copy sitting next to the executable.
            Assert.Equal(Path.Combine(shellBin, "FSOps.Server.exe"), candidates[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void No_solution_file_anywhere_above_means_no_development_candidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "fsops-locator-" + Guid.NewGuid().ToString("N"), "a", "b", "c");
        Directory.CreateDirectory(root);

        try
        {
            var candidates = ServerExecutableLocator
                .CandidatePaths(root, "Release", overridePath: null, directoryExists: _ => true)
                .ToList();

            Assert.Single(candidates);
            Assert.Equal(Path.Combine(root, "FSOps.Server.exe"), candidates[0]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, recursive: true);
        }
    }
}

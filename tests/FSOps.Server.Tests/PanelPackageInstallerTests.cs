using System.Text.Json;
using FSOps.Server.Services;

namespace FSOps.Server.Tests;

/// <summary>
/// PanelPackageInstaller - see src/fsops-ingame-panel/README.md and docs/PLAN.md "The Community
/// folder is captured at onboarding and reused to install the panel". Every test runs against a
/// throwaway temp directory created and destroyed by the test itself - NEVER against a real
/// Community folder or anywhere near %LOCALAPPDATA%\FSOps, per the project's data-safety rules.
/// </summary>
public sealed class PanelPackageInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _communityFolder;
    private readonly string _templateDirectory;

    public PanelPackageInstallerTests()
    {
        _root = Directory.CreateTempSubdirectory("fsops-panel-test-").FullName;
        _communityFolder = Path.Combine(_root, "Packages", "Community");
        Directory.CreateDirectory(_communityFolder);
        _templateDirectory = Path.Combine(_root, "template-source");
        CreateFakeTemplate(_templateDirectory, includeSpb: false);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only - never worth failing a test suite over a locked temp file.
        }
    }

    private static void CreateFakeTemplate(string templateDirectory, bool includeSpb)
    {
        var panelDir = Path.Combine(templateDirectory, "html_ui", "InGamePanels", "FSOpsPanel");
        Directory.CreateDirectory(panelDir);

        File.WriteAllText(Path.Combine(templateDirectory, "manifest.json"), """
            { "dependencies": [], "content_type": "CORE", "title": "FSOps In-Game Panel", "manufacturer": "", "creator": "", "package_version": "1.0.0", "minimum_game_version": "1.0.0", "release_notes": { "neutral": { "LastUpdate": "", "OlderHistory": "" } }, "total_package_size": "0" }
            """);
        File.WriteAllText(Path.Combine(panelDir, "FSOpsPanel.html"), "<html></html>");
        File.WriteAllText(Path.Combine(panelDir, "FSOpsPanel.js"), "// panel js");
        File.WriteAllText(Path.Combine(panelDir, "FSOpsPanel.config.js"), "window.FSOPS_PANEL_PORT = 5977;\n");

        if (includeSpb)
        {
            var spbDir = Path.Combine(templateDirectory, "InGamePanels");
            Directory.CreateDirectory(spbDir);
            File.WriteAllBytes(Path.Combine(spbDir, "FSOpsPanel.spb"), [1, 2, 3, 4]);
        }
    }

    // -----------------------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------------------

    [Fact]
    public void ValidateCommunityFolder_RefusesWhenPathIsEmpty()
    {
        var result = PanelPackageInstaller.ValidateCommunityFolder(null);
        Assert.False(result.Valid);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void ValidateCommunityFolder_RefusesAFolderNotNamedCommunity_AndExplainsWhy()
    {
        var wrongFolder = Path.Combine(_root, "Packages");
        var result = PanelPackageInstaller.ValidateCommunityFolder(wrongFolder);

        Assert.False(result.Valid);
        Assert.Contains("Community", result.Reason);
        Assert.Null(result.ResolvedPath);
    }

    [Fact]
    public void ValidateCommunityFolder_RefusesADriveRoot()
    {
        var driveRoot = Path.GetPathRoot(_root)!;
        var result = PanelPackageInstaller.ValidateCommunityFolder(driveRoot);
        Assert.False(result.Valid);
    }

    [Fact]
    public void ValidateCommunityFolder_AcceptsAFolderNamedCommunity()
    {
        var result = PanelPackageInstaller.ValidateCommunityFolder(_communityFolder);
        Assert.True(result.Valid);
        Assert.Null(result.Reason);
        Assert.Equal(Path.GetFullPath(_communityFolder), result.ResolvedPath);
    }

    [Fact]
    public void ValidateCommunityFolder_IsCaseInsensitiveAboutTheFolderName()
    {
        var lower = Path.Combine(_root, "Packages", "community");
        Directory.CreateDirectory(lower);
        var result = PanelPackageInstaller.ValidateCommunityFolder(lower);
        Assert.True(result.Valid);
    }

    // -----------------------------------------------------------------------------------
    // Install / repair
    // -----------------------------------------------------------------------------------

    [Fact]
    public void InstallOrRepair_WritesOnlyBeneathCommunityFsopsPanel_AndNeverTouchesSiblings()
    {
        var sentinelPath = Path.Combine(_communityFolder, "some-other-addon", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinelPath)!);
        File.WriteAllText(sentinelPath, "untouched");

        var result = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(_communityFolder, "fsops-panel"), result.InstalledPath);
        Assert.True(Directory.Exists(Path.Combine(_communityFolder, "fsops-panel")));
        Assert.Equal("untouched", File.ReadAllText(sentinelPath));
    }

    [Fact]
    public void InstallOrRepair_CopiesEveryTemplateFile()
    {
        var result = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var target = result.InstalledPath!;
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(target, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.html")));
        Assert.True(File.Exists(Path.Combine(target, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.js")));
        Assert.Equal(4, result.FilesWritten); // manifest.json + html + js + config.js
    }

    [Fact]
    public void InstallOrRepair_RewritesOnlyTheConfigFile_WithTheGivenPort()
    {
        var result = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "6100");

        var configPath = Path.Combine(result.InstalledPath!, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.config.js");
        var content = File.ReadAllText(configPath);
        Assert.Contains("window.FSOPS_PANEL_PORT = 6100;", content);
    }

    [Fact]
    public void InstallOrRepair_RerunningAfterAPortChange_UpdatesOnlyThePort_NoTemplateChangesNeeded()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var second = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "9999");

        Assert.True(second.Success);
        var configPath = Path.Combine(second.InstalledPath!, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.config.js");
        Assert.Contains("9999", File.ReadAllText(configPath));
        // The rest of the template is untouched/still present - no SDK, no recompile needed.
        Assert.True(File.Exists(Path.Combine(second.InstalledPath!, "manifest.json")));
    }

    [Fact]
    public void InstallOrRepair_IsIdempotent_SafeToRunRepeatedly()
    {
        var first = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var second = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.FilesWritten, second.FilesWritten);
        Assert.Equal(first.InstalledVersion, second.InstalledVersion);
    }

    [Fact]
    public void InstallOrRepair_GeneratesLayoutJson_IncludingManifest_ExcludingItself()
    {
        var result = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var layoutPath = Path.Combine(result.InstalledPath!, "layout.json");
        Assert.True(File.Exists(layoutPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(layoutPath));
        var paths = doc.RootElement.GetProperty("content")
            .EnumerateArray()
            .Select(e => e.GetProperty("path").GetString())
            .ToList();

        Assert.Contains("manifest.json", paths);
        Assert.Contains("html_ui/InGamePanels/FSOpsPanel/FSOpsPanel.html", paths);
        Assert.DoesNotContain("layout.json", paths);
    }

    [Fact]
    public void InstallOrRepair_HonestlyReportsWhenTheSpbIsMissing_DoesNotClaimTheToolbarWillAppear()
    {
        var result = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        Assert.True(result.Success);
        Assert.False(result.SpbPresent);
        Assert.False(result.ToolbarWillAppearInSim);
    }

    [Fact]
    public void InstallOrRepair_ReportsToolbarWillAppear_WhenTheSpbIsPresentInTheTemplate()
    {
        var templateWithSpb = Path.Combine(_root, "template-with-spb");
        CreateFakeTemplate(templateWithSpb, includeSpb: true);

        var result = PanelPackageInstaller.InstallOrRepair(_communityFolder, templateWithSpb, "5977");

        Assert.True(result.SpbPresent);
        Assert.True(result.ToolbarWillAppearInSim);
        Assert.True(File.Exists(Path.Combine(result.InstalledPath!, "InGamePanels", "FSOpsPanel.spb")));
    }

    [Fact]
    public void InstallOrRepair_RefusesAndWritesNothing_WhenTheChosenFolderIsNotNamedCommunity()
    {
        var wrongFolder = Path.Combine(_root, "Packages");
        var before = Directory.GetFileSystemEntries(wrongFolder, "*", SearchOption.AllDirectories).Length;

        var result = PanelPackageInstaller.InstallOrRepair(wrongFolder, _templateDirectory, "5977");

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        var after = Directory.GetFileSystemEntries(wrongFolder, "*", SearchOption.AllDirectories).Length;
        Assert.Equal(before, after);
    }

    [Fact]
    public void InstallOrRepair_RefusesWhenTheTemplateDirectoryIsMissing()
    {
        var result = PanelPackageInstaller.InstallOrRepair(_communityFolder, Path.Combine(_root, "does-not-exist"), "5977");
        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(_communityFolder, "fsops-panel")));
    }

    // -----------------------------------------------------------------------------------
    // Uninstall
    // -----------------------------------------------------------------------------------

    [Fact]
    public void Uninstall_RemovesOnlyTheFsopsPanelFolder_LeavesSiblingsAlone()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var siblingPath = Path.Combine(_communityFolder, "some-other-addon", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(siblingPath)!);
        File.WriteAllText(siblingPath, "untouched");

        var result = PanelPackageInstaller.Uninstall(_communityFolder);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(Path.Combine(_communityFolder, "fsops-panel")));
        Assert.True(File.Exists(siblingPath));
    }

    [Fact]
    public void Uninstall_WhenNothingWasInstalled_SucceedsAsANoOp()
    {
        var result = PanelPackageInstaller.Uninstall(_communityFolder);
        Assert.True(result.Success);
        Assert.False(result.Installed);
    }

    // -----------------------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------------------

    [Fact]
    public void GetStatus_ReportsNotInstalled_WhenNoPathIsConfigured()
    {
        var status = PanelPackageInstaller.GetStatus(null, "5977");
        Assert.True(status.Success);
        Assert.False(status.Installed);
    }

    [Fact]
    public void GetStatus_ReflectsWhatIsActuallyOnDisk_AfterInstall()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977");

        Assert.True(status.Installed);
        Assert.Equal("1.0.0", status.InstalledVersion);
        Assert.Equal(PanelPackageInstaller.ExpectedPanelVersion, status.ExpectedVersion);
        Assert.False(status.SpbPresent);
    }

    // -----------------------------------------------------------------------------------
    // Detection - must never throw, even on a machine with no MSFS installed
    // -----------------------------------------------------------------------------------

    [Fact]
    public void DetectCommunityFolderCandidates_NeverThrows_ReturnsAListEvenWhenNothingIsFound()
    {
        var candidates = PanelPackageInstaller.DetectCommunityFolderCandidates();
        Assert.NotNull(candidates);
    }
}

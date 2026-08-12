using System.Text.Json;
using FSOps.Server.Services;

namespace FSOps.Server.Tests;

/// <summary>
/// PanelPackageInstaller - see src/fsops-ingame-panel/README.md. The Community
/// folder is captured at onboarding and reused to install the panel. Every test runs against a
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
    // Refusing to delete what FSOps did not create. The Community folder is a path the player
    // typed and "fsops-panel" is a name anyone could have used - a recursive delete of someone
    // else's folder is the one mistake here with no undo.
    // -----------------------------------------------------------------------------------

    [Fact]
    public void Uninstall_RefusesToDeleteAnFsopsPanelFolderThatFsOpsDidNotCreate()
    {
        var impostor = Path.Combine(_communityFolder, "fsops-panel");
        Directory.CreateDirectory(impostor);
        File.WriteAllText(Path.Combine(impostor, "manifest.json"), """{ "title": "Somebody Else's Add-on" }""");
        File.WriteAllText(Path.Combine(impostor, "precious.txt"), "not ours");

        var result = PanelPackageInstaller.Uninstall(_communityFolder);

        Assert.False(result.Success);
        Assert.Contains("didn't create", result.Reason);
        Assert.True(File.Exists(Path.Combine(impostor, "precious.txt")));
    }

    [Fact]
    public void InstallOrRepair_RefusesToOverwriteAnFsopsPanelFolderThatFsOpsDidNotCreate()
    {
        var impostor = Path.Combine(_communityFolder, "fsops-panel");
        Directory.CreateDirectory(impostor);
        File.WriteAllText(Path.Combine(impostor, "precious.txt"), "not ours");

        var result = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        Assert.False(result.Success);
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(impostor, "precious.txt")));
    }

    [Fact]
    public void Uninstall_RemovesAHalfWrittenInstall_RecognisedByTheConfigFileFsOpsGenerates()
    {
        // A previous install that died after writing the config but before the manifest must stay
        // repairable and removable, not become permanently stuck.
        var target = Path.Combine(_communityFolder, "fsops-panel");
        var configDir = Path.Combine(target, "html_ui", "InGamePanels", "FSOpsPanel");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "FSOpsPanel.config.js"), "window.FSOPS_PANEL_PORT = 5977;\n");

        var result = PanelPackageInstaller.Uninstall(_communityFolder);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void InstallOrRepair_RepairsADamagedInstall_RestoringFilesDeletedByHand()
    {
        var first = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var damaged = Path.Combine(first.InstalledPath!, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.html");
        File.Delete(damaged);
        Assert.False(File.Exists(damaged));

        var repaired = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        Assert.True(repaired.Success);
        Assert.True(File.Exists(damaged));
        Assert.Equal(PanelPackageInstaller.ExpectedPanelVersion, repaired.InstalledVersion);
    }

    // -----------------------------------------------------------------------------------
    // Move - changing the Community folder in Settings
    // -----------------------------------------------------------------------------------

    private string CreateSecondCommunityFolder()
    {
        var second = Path.Combine(_root, "OtherInstall", "Packages", "Community");
        Directory.CreateDirectory(second);
        return second;
    }

    [Fact]
    public void MoveInstall_InstallsIntoTheNewFolder_AndRemovesTheOldCopy()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var destination = CreateSecondCommunityFolder();

        var result = PanelPackageInstaller.MoveInstall(_communityFolder, destination, _templateDirectory, "5977");

        Assert.True(result.Success);
        Assert.True(result.Install.Installed);
        Assert.True(File.Exists(Path.Combine(destination, "fsops-panel", "manifest.json")));
        Assert.True(result.OldCopyRemoved);
        Assert.False(Directory.Exists(Path.Combine(_communityFolder, "fsops-panel")));
    }

    [Fact]
    public void MoveInstall_WhenTheNewFolderIsRejected_LeavesTheOldCopyExactlyWhereItWas()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var notACommunityFolder = Path.Combine(_root, "OtherInstall", "Packages");
        Directory.CreateDirectory(notACommunityFolder);

        var result = PanelPackageInstaller.MoveInstall(_communityFolder, notACommunityFolder, _templateDirectory, "5977");

        Assert.False(result.Success);
        Assert.False(result.OldCopyRemoved);
        // The whole point: a failed move never leaves the player with no panel at all.
        Assert.True(File.Exists(Path.Combine(_communityFolder, "fsops-panel", "manifest.json")));
    }

    [Fact]
    public void MoveInstall_ToTheSameFolder_ReinstallsInPlace_AndDoesNotDeleteWhatItJustWrote()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var result = PanelPackageInstaller.MoveInstall(_communityFolder, _communityFolder, _templateDirectory, "6100");

        Assert.True(result.Success);
        Assert.False(result.OldCopyRemoved);
        Assert.True(File.Exists(Path.Combine(_communityFolder, "fsops-panel", "manifest.json")));
        var configPath = Path.Combine(_communityFolder, "fsops-panel", "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.config.js");
        Assert.Contains("6100", File.ReadAllText(configPath));
    }

    [Fact]
    public void MoveInstall_WithNoPreviousFolder_JustInstalls()
    {
        var result = PanelPackageInstaller.MoveInstall(null, _communityFolder, _templateDirectory, "5977");

        Assert.True(result.Success);
        Assert.True(result.Install.Installed);
        Assert.False(result.OldCopyRemoved);
        Assert.Contains("no previous folder", result.OldCopyMessage);
    }

    [Fact]
    public void MoveInstall_WhenTheOldFolderIsAlreadyGone_SucceedsWithoutClaimingItRemovedAnything()
    {
        var destination = CreateSecondCommunityFolder();
        var vanished = Path.Combine(_root, "Deleted", "Packages", "Community");

        var result = PanelPackageInstaller.MoveInstall(vanished, destination, _templateDirectory, "5977");

        Assert.True(result.Success);
        Assert.True(result.Install.Installed);
        Assert.False(result.OldCopyRemoved);
        Assert.Contains("nothing left", result.OldCopyMessage);
    }

    [Fact]
    public void MoveInstall_WhenTheOldFolderHoldsSomethingFsOpsDidNotInstall_KeepsItAndSaysSo()
    {
        var impostor = Path.Combine(_communityFolder, "fsops-panel");
        Directory.CreateDirectory(impostor);
        File.WriteAllText(Path.Combine(impostor, "precious.txt"), "not ours");
        var destination = CreateSecondCommunityFolder();

        var result = PanelPackageInstaller.MoveInstall(_communityFolder, destination, _templateDirectory, "5977");

        Assert.True(result.Success);
        Assert.True(result.Install.Installed);
        Assert.False(result.OldCopyRemoved);
        Assert.True(File.Exists(Path.Combine(impostor, "precious.txt")));
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

    /// <summary>
    /// The port FSOps is running on is knowable before any Community folder has ever been chosen -
    /// it's a fact about this server, not about the install. Regression for the response reporting
    /// ExpectedPort as null in this state even though ResolveConfiguredPort answers it fine.
    /// </summary>
    [Fact]
    public void GetStatus_StillReportsTheExpectedPort_WhenNoPathIsConfigured()
    {
        var status = PanelPackageInstaller.GetStatus(null, "5978");
        Assert.Equal("5978", status.ExpectedPort);
    }

    /// <summary>Same fact, same reasoning, for the two other early-return states in GetStatus - a
    /// path that fails validation, and a previously-configured folder that no longer exists.</summary>
    [Fact]
    public void GetStatus_StillReportsTheExpectedPort_WhenThePathIsInvalid()
    {
        var status = PanelPackageInstaller.GetStatus(Path.Combine(_communityFolder, "..", "NotCommunity"), "5978");
        Assert.False(status.Success);
        Assert.Equal("5978", status.ExpectedPort);
    }

    [Fact]
    public void GetStatus_StillReportsTheExpectedPort_WhenTheConfiguredFolderNoLongerExists()
    {
        var deleted = Path.Combine(_communityFolder, "GoneMissing", "Community");
        Directory.CreateDirectory(deleted);
        // Deleted after being validated once, same as a player who moved or reinstalled MSFS.
        Directory.Delete(deleted);

        var status = PanelPackageInstaller.GetStatus(deleted, "5978");

        Assert.False(status.Success);
        Assert.Equal("5978", status.ExpectedPort);
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
    // Damaged installs
    //
    // The failure these cover shipped once: Installed was decided from manifest.json alone, so
    // deleting the panel's own FSOpsPanel.js left a green "Installed - up to date" status over a
    // package that renders nothing in the sim, and nothing anywhere suggested repairing it.
    // -----------------------------------------------------------------------------------

    [Fact]
    public void GetStatus_ReportsNothingMissing_WhenTheInstallIsIntact()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977", _templateDirectory);

        Assert.True(status.Installed);
        Assert.Empty(status.MissingFiles);
    }

    [Fact]
    public void GetStatus_ReportsADeletedFile_RatherThanClaimingTheInstallIsHealthy()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var installed = Path.Combine(_communityFolder, PanelPackageInstaller.PackageFolderName);
        File.Delete(Path.Combine(installed, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.js"));

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977", _templateDirectory);

        // Still a successful read of a package that IS there - just not a complete one.
        Assert.True(status.Success);
        Assert.False(status.Installed);
        Assert.Equal(["html_ui/InGamePanels/FSOpsPanel/FSOpsPanel.js"], status.MissingFiles);
        Assert.Contains("reinstall", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetStatus_NoticesTheGeneratedLayoutJsonGoingMissing()
    {
        // layout.json is written by the installer rather than copied from the template, so it is the
        // one expected file a template-derived list would miss - and MSFS will not load the package
        // without it.
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var installed = Path.Combine(_communityFolder, PanelPackageInstaller.PackageFolderName);
        File.Delete(Path.Combine(installed, "layout.json"));

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977", _templateDirectory);

        Assert.False(status.Installed);
        Assert.Contains("layout.json", status.MissingFiles);
    }

    [Fact]
    public void GetStatus_CoversAFileAddedToTheTemplateLater_WithoutBeingToldAboutIt()
    {
        // The expected set is derived from the template every time. If it were hardcoded instead,
        // a file added to the package later would go unchecked and quietly reproduce this bug.
        File.WriteAllText(Path.Combine(_templateDirectory, "brand-new-file.txt"), "shipped later");
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var installed = Path.Combine(_communityFolder, PanelPackageInstaller.PackageFolderName);
        File.Delete(Path.Combine(installed, "brand-new-file.txt"));

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977", _templateDirectory);

        Assert.False(status.Installed);
        Assert.Contains("brand-new-file.txt", status.MissingFiles);
    }

    [Fact]
    public void GetStatus_RepairPutsAMissingFileBack_AndClearsTheDamagedState()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var installed = Path.Combine(_communityFolder, PanelPackageInstaller.PackageFolderName);
        var panelJs = Path.Combine(installed, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.js");
        File.Delete(panelJs);

        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977", _templateDirectory);

        Assert.True(File.Exists(panelJs));
        Assert.True(status.Installed);
        Assert.Empty(status.MissingFiles);
    }

    [Fact]
    public void GetStatus_BlamesTheBuild_OnlyWhenTheBuildGenuinelyShipsNoSpb()
    {
        // Telling the player to wait for a future update is right for a build with no .spb at all,
        // and actively harmful when their own copy merely lost one - it sends them away to wait for
        // something that will never arrive, when a repair would fix it now.
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977", _templateDirectory);

        Assert.False(status.SpbPresent);
        Assert.False(status.ToolbarWillAppearInSim);
        Assert.Contains("isn't compiled yet", status.Message);
        // The build has no .spb to lose, so its absence is not damage.
        Assert.Empty(status.MissingFiles);
    }

    [Fact]
    public void GetStatus_TreatsADeletedSpbAsDamage_NotAsAnUncompiledBuild()
    {
        var templateWithSpb = Path.Combine(_root, "template-with-spb");
        CreateFakeTemplate(templateWithSpb, includeSpb: true);
        PanelPackageInstaller.InstallOrRepair(_communityFolder, templateWithSpb, "5977");
        var installed = Path.Combine(_communityFolder, PanelPackageInstaller.PackageFolderName);
        File.Delete(Path.Combine(installed, "InGamePanels", "FSOpsPanel.spb"));

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977", templateWithSpb);

        Assert.False(status.Installed);
        Assert.Contains("InGamePanels/FSOpsPanel.spb", status.MissingFiles);
        Assert.DoesNotContain("isn't compiled yet", status.Message);
        // The honest-reporting property must survive: a missing component still means no button.
        Assert.False(status.ToolbarWillAppearInSim);
    }

    [Fact]
    public void GetStatus_WithNoTemplateToCompareAgainst_DoesNotCondemnAGoodInstall()
    {
        // An unknown answer must not masquerade as "nothing is missing", but it must not invent
        // damage either - the older two-argument callers still have to work.
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977");

        Assert.True(status.Installed);
        Assert.Empty(status.MissingFiles);
    }

    [Fact]
    public void GetStatus_SaysTheFolderIsGone_RatherThanJustNotInstalled_WhenItHasBeenDeleted()
    {
        // "Not installed" would invite the player to press Install and wonder why nothing ever
        // appears in the sim - the folder being gone is the actual thing they need to know.
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        Directory.Delete(_communityFolder, recursive: true);

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977");

        Assert.False(status.Success);
        Assert.False(status.Installed);
        Assert.Contains("no longer exists", status.Reason);
    }

    [Fact]
    public void InstallOrRepair_RefusesAMissingCommunityFolder_RatherThanCreatingAPhantomOne()
    {
        // Creating the tree would report a perfectly successful install of a panel MSFS will never
        // load, because the sim only reads the Community folder it actually has.
        var missing = Path.Combine(_root, "GoneAway", "Packages", "Community");

        var result = PanelPackageInstaller.InstallOrRepair(missing, _templateDirectory, "5977");

        Assert.False(result.Success);
        Assert.Contains("doesn't exist", result.Reason);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void ValidateCommunityFolder_ReportsExistenceSeparatelyFromShape()
    {
        var missing = Path.Combine(_root, "Packages", "NotYet", "Community");

        var present = PanelPackageInstaller.ValidateCommunityFolder(_communityFolder);
        var absent = PanelPackageInstaller.ValidateCommunityFolder(missing);

        Assert.True(present.Valid);
        Assert.True(present.Exists);
        // Still a well-formed Community path - onboarding shouldn't reject it just because the sim
        // isn't installed on this machine yet.
        Assert.True(absent.Valid);
        Assert.False(absent.Exists);
    }

    [Fact]
    public void GetStatus_SpotsVersionDrift_WhenWhatIsOnDiskIsOlderThanExpected()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var manifestPath = Path.Combine(_communityFolder, "fsops-panel", "manifest.json");
        File.WriteAllText(manifestPath, """{ "title": "FSOps In-Game Panel", "package_version": "0.9.0" }""");

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977");

        Assert.True(status.Installed);
        Assert.Equal("0.9.0", status.InstalledVersion);
        Assert.NotEqual(status.InstalledVersion, status.ExpectedVersion);
        Assert.Contains("reinstall", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetStatus_SpotsPortDrift_WhenFsOpsHasMovedPortSinceTheInstall()
    {
        // The panel calls FSOps on a port baked into its config at install time. Move FSOps to a
        // different port and the installed panel goes on calling the old one, showing nothing in
        // the sim with no error anywhere - so status has to be the thing that notices.
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5978");

        Assert.True(status.Installed);
        Assert.Equal("5977", status.InstalledPort);
        Assert.Equal("5978", status.ExpectedPort);
        Assert.Contains("5977", status.Message);
        Assert.Contains("5978", status.Message);
        Assert.Contains("reinstall", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetStatus_ReportsNoPortDrift_WhenThePortStillMatches()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");

        var status = PanelPackageInstaller.GetStatus(_communityFolder, "5977");

        Assert.Equal("5977", status.InstalledPort);
        Assert.Equal("5977", status.ExpectedPort);
        Assert.DoesNotContain("port", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallOrRepair_AfterAPortChange_ClearsTheDriftItJustFixed()
    {
        PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        Assert.NotEqual(
            PanelPackageInstaller.GetStatus(_communityFolder, "5978").InstalledPort,
            PanelPackageInstaller.GetStatus(_communityFolder, "5978").ExpectedPort);

        var repaired = PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5978");

        Assert.Equal("5978", repaired.InstalledPort);
        Assert.Equal("5978", repaired.ExpectedPort);
        var after = PanelPackageInstaller.GetStatus(_communityFolder, "5978");
        Assert.Equal(after.ExpectedPort, after.InstalledPort);
    }

    [Fact]
    public void GetStatus_RefusesAPathThatIsNotACommunityFolder_RatherThanProbingIt()
    {
        var arbitrary = Path.Combine(_root, "Packages");

        var status = PanelPackageInstaller.GetStatus(arbitrary, "5977");

        Assert.False(status.Success);
        Assert.NotNull(status.Reason);
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

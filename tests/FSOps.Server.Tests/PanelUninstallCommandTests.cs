using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// PanelUninstallCommand - what "FSOps.Server.exe --uninstall-panel" actually does when Inno's
/// [UninstallRun] invokes it (see installer/FSOps.iss). Every test runs against a throwaway
/// database file and a throwaway Community folder created and destroyed by the test itself - NEVER
/// against %LOCALAPPDATA%\FSOps or a real Community folder, per the project's data-safety rules.
/// </summary>
public sealed class PanelUninstallCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;
    private readonly string _communityFolder;
    private readonly string _templateDirectory;

    public PanelUninstallCommandTests()
    {
        _root = Directory.CreateTempSubdirectory("fsops-panel-uninstall-test-").FullName;
        _databasePath = Path.Combine(_root, "fsops.db");
        _communityFolder = Path.Combine(_root, "Packages", "Community");
        Directory.CreateDirectory(_communityFolder);
        _templateDirectory = Path.Combine(_root, "template-source");
        CreateFakeTemplate(_templateDirectory);
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

    private static void CreateFakeTemplate(string templateDirectory)
    {
        var panelDir = Path.Combine(templateDirectory, "html_ui", "InGamePanels", "FSOpsPanel");
        Directory.CreateDirectory(panelDir);
        File.WriteAllText(Path.Combine(templateDirectory, "manifest.json"), """
            { "dependencies": [], "content_type": "CORE", "title": "FSOps In-Game Panel", "manufacturer": "", "creator": "", "package_version": "1.0.0", "minimum_game_version": "1.0.0", "release_notes": { "neutral": { "LastUpdate": "", "OlderHistory": "" } }, "total_package_size": "0" }
            """);
        File.WriteAllText(Path.Combine(panelDir, "FSOpsPanel.html"), "<html></html>");
        File.WriteAllText(Path.Combine(panelDir, "FSOpsPanel.js"), "// panel js");
        File.WriteAllText(Path.Combine(panelDir, "FSOpsPanel.config.js"), "window.FSOPS_PANEL_PORT = 5977;\n");
    }

    /// <summary>
    /// Creates a real, migrated database at <see cref="_databasePath"/> - the same schema a genuine
    /// FSOps install would have - and writes a UserSettings row for the fixed local user, exactly
    /// like SettingsEndpoints does. This is deliberately EF/migrations-based even though
    /// PanelUninstallCommand itself never uses EF, so the test proves the command's hand-written SQL
    /// actually matches what the real schema looks like, not just what a hand-rolled fixture assumes.
    /// </summary>
    private async Task SeedDatabaseAsync(string? communityFolderPath)
    {
        // Pooling=False matters here, exactly as in ServiceCollectionExtensions'
        // BackUpBeforeMigrating: Microsoft.Data.Sqlite returns a "closed" connection to its pool
        // rather than releasing the file handle, so a later test that needs the file to genuinely be
        // free (the exclusive-lock test below) would otherwise fail for a reason that has nothing to
        // do with the behaviour it is testing.
        var options = new DbContextOptionsBuilder<FsOpsDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;

        await using var db = new FsOpsDbContext(options);
        await db.Database.MigrateAsync();

        db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = new LocalUser().UserId,
            CommunityFolderPath = communityFolderPath,
        });

        await db.SaveChangesAsync();
    }

    // -----------------------------------------------------------------------------------
    // No database / no configuration - the common "nothing to do" cases
    // -----------------------------------------------------------------------------------

    [Fact]
    public void RunFor_WhenTheDatabaseFileDoesNotExist_DoesNothingAndReturnsZero()
    {
        var missingDb = Path.Combine(_root, "does-not-exist.db");

        var exitCode = PanelUninstallCommand.RunFor(missingDb);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunFor_WhenNoCommunityFolderWasEverConfigured_DoesNothingAndReturnsZero()
    {
        await SeedDatabaseAsync(communityFolderPath: null);

        var exitCode = PanelUninstallCommand.RunFor(_databasePath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void RunFor_AgainstAnEmptyFileThatIsNotARealDatabase_NeverThrows_ReturnsZero()
    {
        File.WriteAllText(_databasePath, "not a sqlite database");

        var exitCode = PanelUninstallCommand.RunFor(_databasePath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void RunFor_AgainstADatabaseFileWithNoTables_NeverThrows_ReturnsZero()
    {
        // A file SQLite genuinely recognises as its own format, but with none of FSOps' schema
        // applied - e.g. migrations never ran. TryReadCommunityFolderPath must treat "the table
        // doesn't exist" the same as "nothing configured", not as a reason to blow up.
        using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
        }

        var exitCode = PanelUninstallCommand.RunFor(_databasePath);

        Assert.Equal(0, exitCode);
        Assert.Null(PanelUninstallCommand.TryReadCommunityFolderPath(_databasePath));
    }

    // -----------------------------------------------------------------------------------
    // The real path: reads the configured folder and removes our own package
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task RunFor_ReadsTheConfiguredCommunityFolder_AndRemovesOurOwnPanel()
    {
        FSOps.Server.Services.PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        Assert.True(Directory.Exists(Path.Combine(_communityFolder, "fsops-panel")));
        await SeedDatabaseAsync(_communityFolder);

        var exitCode = PanelUninstallCommand.RunFor(_databasePath);

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(Path.Combine(_communityFolder, "fsops-panel")));
    }

    [Fact]
    public async Task TryReadCommunityFolderPath_ReturnsExactlyWhatWasSaved()
    {
        await SeedDatabaseAsync(_communityFolder);

        var path = PanelUninstallCommand.TryReadCommunityFolderPath(_databasePath);

        Assert.Equal(_communityFolder, path);
    }

    [Fact]
    public async Task RunFor_LeavesASiblingAddonUntouched()
    {
        FSOps.Server.Services.PanelPackageInstaller.InstallOrRepair(_communityFolder, _templateDirectory, "5977");
        var siblingPath = Path.Combine(_communityFolder, "some-other-addon", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(siblingPath)!);
        File.WriteAllText(siblingPath, "untouched");
        await SeedDatabaseAsync(_communityFolder);

        PanelUninstallCommand.RunFor(_databasePath);

        Assert.True(File.Exists(siblingPath));
    }

    // -----------------------------------------------------------------------------------
    // Never authorise deleting what FSOps did not create, even from this entry point
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task RunFor_RefusesToDeleteAFolderFsOpsDidNotCreate_AndReturnsZeroAnyway()
    {
        var impostor = Path.Combine(_communityFolder, "fsops-panel");
        Directory.CreateDirectory(impostor);
        File.WriteAllText(Path.Combine(impostor, "precious.txt"), "not ours");
        await SeedDatabaseAsync(_communityFolder);

        var exitCode = PanelUninstallCommand.RunFor(_databasePath);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(impostor, "precious.txt")));
    }

    // -----------------------------------------------------------------------------------
    // A moved or deleted Community folder - must never throw or fail the uninstall
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task RunFor_WhenTheConfiguredCommunityFolderNoLongerExists_ReturnsZeroWithoutThrowing()
    {
        var vanished = Path.Combine(_root, "Deleted", "Packages", "Community");
        await SeedDatabaseAsync(vanished);

        var exitCode = PanelUninstallCommand.RunFor(_databasePath);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunFor_WhenTheConfiguredPathIsNoLongerAValidCommunityFolder_ReturnsZeroWithoutThrowing()
    {
        // The path was replaced by something else entirely since it was saved - e.g. the player
        // repointed a symlink, or the drive letter now holds something unrelated named differently.
        var notCommunityAnymore = Path.Combine(_root, "Packages");
        await SeedDatabaseAsync(notCommunityAnymore);

        var exitCode = PanelUninstallCommand.RunFor(_databasePath);

        Assert.Equal(0, exitCode);
    }

    // -----------------------------------------------------------------------------------
    // A locked database file - must never throw or fail the uninstall
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task RunFor_WhenTheDatabaseIsExclusivelyLocked_ReturnsZeroWithoutThrowing()
    {
        await SeedDatabaseAsync(_communityFolder);

        // Holds an exclusive OS-level lock on the file itself, independent of SQLite's own locking,
        // to reproduce "something else has this file open" without needing a second real process.
        using var lockingHandle = new FileStream(
            _databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var exitCode = PanelUninstallCommand.RunFor(_databasePath);

        Assert.Equal(0, exitCode);
        // The panel must survive too - a failed read must never be mistaken for "nothing configured,
        // so also nothing to remove" turning into an accidental removal via some other path.
        Assert.False(Directory.Exists(Path.Combine(_communityFolder, "fsops-panel")));
    }
}

using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// PanelEndpoints - drives the handlers directly against an isolated in-memory RouteTestContext,
/// same convention as MaintenanceEndpointsTests. The heavier file-system behaviour (what actually
/// happens on disk) is covered exhaustively in PanelPackageInstallerTests; these tests only check
/// the endpoints wire requests/responses to PanelPackageInstaller correctly, including reading
/// UserSettings.CommunityFolderPath for the status convenience endpoint.
/// </summary>
public sealed class PanelEndpointsTests
{
    private static int StatusCodeOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static T ValueOf<T>(IResult result) => (T)Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;

    [Fact]
    public void Detect_ReturnsOkWithAList()
    {
        var result = PanelEndpoints.DetectAsync();
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        Assert.NotNull(ValueOf<IReadOnlyList<PanelCandidate>>(result));
    }

    [Fact]
    public void Validate_RefusesANonCommunityFolder_WithAReason()
    {
        using var temp = new TempDirectory();
        var wrongFolder = temp.Path;

        var result = PanelEndpoints.ValidateAsync(new ValidatePanelPathRequest(wrongFolder));

        var validation = ValueOf<PanelPathValidation>(result);
        Assert.False(validation.Valid);
        Assert.NotNull(validation.Reason);
    }

    [Fact]
    public void Validate_AcceptsAFolderNamedCommunity()
    {
        using var temp = new TempDirectory();
        var community = Path.Combine(temp.Path, "Community");
        Directory.CreateDirectory(community);

        var result = PanelEndpoints.ValidateAsync(new ValidatePanelPathRequest(community));

        var validation = ValueOf<PanelPathValidation>(result);
        Assert.True(validation.Valid);
    }

    [Fact]
    public async Task GetStatus_ReportsNotInstalled_WhenTheUserHasNoSettingsRowYet()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var result = await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, path: null, CancellationToken.None);

        var status = ValueOf<PanelOperationResult>(result);
        Assert.False(status.Installed);
    }

    [Fact]
    public async Task GetStatus_ReadsTheSavedCommunityFolderPath_AndReflectsWhatsOnDisk()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        using var temp = new TempDirectory();
        var community = Path.Combine(temp.Path, "Community");
        Directory.CreateDirectory(community);

        ctx.Db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ctx.CurrentUser.UserId,
            CommunityFolderPath = community,
        });
        await ctx.Db.SaveChangesAsync();

        var before = ValueOf<PanelOperationResult>(await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, path: null, CancellationToken.None));
        Assert.False(before.Installed);

        // Installed from the shipped template, because that is what the endpoint measures the result
        // against - see ShippedTemplate. The before/after pair is what gives this test its teeth:
        // the same saved path reads as not-installed and then installed, so an endpoint that
        // consulted some other folder could not produce both answers.
        PanelPackageInstaller.InstallOrRepair(community, ShippedTemplate, "5977");

        var after = ValueOf<PanelOperationResult>(await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, path: null, CancellationToken.None));
        Assert.True(after.Installed);
        Assert.Equal("1.0.0", after.InstalledVersion);
    }

    [Fact]
    public async Task GetStatus_WithAnExplicitPath_ReportsOnThatFolder_NotTheSavedOne()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        using var temp = new TempDirectory();

        var saved = Path.Combine(temp.Path, "saved", "Community");
        var other = Path.Combine(temp.Path, "other", "Community");
        Directory.CreateDirectory(saved);
        Directory.CreateDirectory(other);

        ctx.Db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ctx.CurrentUser.UserId,
            CommunityFolderPath = saved,
        });
        await ctx.Db.SaveChangesAsync();

        PanelPackageInstaller.InstallOrRepair(other, ShippedTemplate, "5977");

        // Saved folder has nothing; the explicitly-named one does. This is exactly the question
        // Settings needs answered before it offers to move an install off an old folder.
        var savedStatus = ValueOf<PanelOperationResult>(await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, path: null, CancellationToken.None));
        var otherStatus = ValueOf<PanelOperationResult>(await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, other, CancellationToken.None));

        // The two folders stay deliberately far apart - one has a complete package, the other has no
        // package at all - so the test still fails if the endpoint ever consults the wrong one.
        Assert.False(savedStatus.Installed);
        Assert.Empty(savedStatus.MissingFiles);
        Assert.True(otherStatus.Installed);
        Assert.Empty(otherStatus.MissingFiles);
    }

    [Fact]
    public async Task GetStatus_WithAPathThatIsNotACommunityFolder_RefusesInsteadOfProbingIt()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        using var temp = new TempDirectory();

        var result = await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, temp.Path, CancellationToken.None);

        // Still a 200 - "that isn't a Community folder" is an answer to show the player, not a
        // transport failure - but Success is false and it never looked at the folder's contents.
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var status = ValueOf<PanelOperationResult>(result);
        Assert.False(status.Success);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public void Move_ReturnsBadRequest_AndKeepsTheOldInstall_WhenTheNewPathIsInvalid()
    {
        using var temp = new TempDirectory();
        var community = Path.Combine(temp.Path, "Community");
        Directory.CreateDirectory(community);
        PanelPackageInstaller.InstallOrRepair(community, CreateTemplate(temp.Path), "5977");

        var result = PanelEndpoints.MoveAsync(new MovePanelRequest(community, temp.Path));

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var value = ValueOf<PanelMoveResult>(result);
        Assert.False(value.OldCopyRemoved);
        Assert.True(File.Exists(Path.Combine(community, "fsops-panel", "manifest.json")));
    }

    /// <summary>Minimal stand-in for the shipped PanelTemplate folder, inside a throwaway temp dir.</summary>
    private static string CreateTemplate(string root)
    {
        var templateDirectory = Path.Combine(root, "template");
        var panelDir = Path.Combine(templateDirectory, "html_ui", "InGamePanels", "FSOpsPanel");
        Directory.CreateDirectory(panelDir);
        File.WriteAllText(Path.Combine(templateDirectory, "manifest.json"), "{\"title\":\"FSOps In-Game Panel\",\"package_version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(panelDir, "FSOpsPanel.config.js"), "window.FSOPS_PANEL_PORT = 5977;");
        return templateDirectory;
    }

    /// <summary>
    /// The very template the endpoints themselves install from and measure against - the same
    /// PanelTemplate folder that ships beside the server and is copied into the test output.
    ///
    /// <para>
    /// A test that wants the endpoint to see a COMPLETE install has to use this rather than
    /// CreateTemplate's stub. GetStatus reports an install as complete only when every file the
    /// shipped template would write is present, so installing a two-file stub and then asking the
    /// endpoint about it produces a genuinely incomplete package - a disagreement that cannot arise
    /// in the real app, where both sides read this one directory.
    /// </para>
    /// </summary>
    private static string ShippedTemplate => Path.Combine(AppContext.BaseDirectory, "PanelTemplate");

    [Fact]
    public void Install_ReturnsBadRequest_WhenThePathIsInvalid()
    {
        using var temp = new TempDirectory();
        var result = PanelEndpoints.InstallAsync(new InstallPanelRequest(temp.Path));
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
    }

    [Fact]
    public void Uninstall_OnAnUnwrittenFolder_SucceedsAsANoOp()
    {
        using var temp = new TempDirectory();
        var community = Path.Combine(temp.Path, "Community");
        Directory.CreateDirectory(community);

        var result = PanelEndpoints.UninstallAsync(community);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var value = ValueOf<PanelOperationResult>(result);
        Assert.False(value.Installed);
    }

    /// <summary>Throwaway temp directory, deleted on dispose - never a real Community folder.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("fsops-panel-endpoint-test-").FullName;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

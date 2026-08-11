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

        var result = await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None);

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

        var before = ValueOf<PanelOperationResult>(await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None));
        Assert.False(before.Installed);

        var templateDirectory = Path.Combine(temp.Path, "template");
        Directory.CreateDirectory(Path.Combine(templateDirectory, "html_ui", "InGamePanels", "FSOpsPanel"));
        File.WriteAllText(Path.Combine(templateDirectory, "manifest.json"), "{\"package_version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(templateDirectory, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.config.js"), "window.FSOPS_PANEL_PORT = 5977;");

        PanelPackageInstaller.InstallOrRepair(community, templateDirectory, "5977");

        var after = ValueOf<PanelOperationResult>(await PanelEndpoints.GetStatusAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None));
        Assert.True(after.Installed);
        Assert.Equal("1.0.0", after.InstalledVersion);
    }

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

using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Detect / validate / install / status / uninstall for the FSOps in-game panel package (see
/// src/fsops-ingame-panel/README.md and PanelPackageInstaller). Deliberately does not write
/// UserSettings.CommunityFolderPath itself - that field is owned by SettingsEndpoints' PUT
/// /settings, and the frontend calls that separately after a successful install so there is one
/// place, not two, that persists the chosen path. GetStatusAsync is the one read-only exception:
/// it reads the already-saved setting purely as a convenience for "what's currently installed".
/// </summary>
public static class PanelEndpoints
{
    public static void MapPanelEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/panel/detect", DetectAsync);
        group.MapPost("/panel/validate", ValidateAsync);
        group.MapGet("/panel/status", GetStatusAsync);
        group.MapPost("/panel/install", InstallAsync);
        group.MapDelete("/panel/uninstall", UninstallAsync);
    }

    public static IResult DetectAsync() =>
        Results.Ok(PanelPackageInstaller.DetectCommunityFolderCandidates());

    public static IResult ValidateAsync(ValidatePanelPathRequest request) =>
        Results.Ok(PanelPackageInstaller.ValidateCommunityFolder(request.Path));

    public static async Task<IResult> GetStatusAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == currentUser.UserId, ct);
        var port = PanelPackageInstaller.ResolveConfiguredPort();
        var status = PanelPackageInstaller.GetStatus(settings?.CommunityFolderPath, port);
        return Results.Ok(status);
    }

    public static IResult InstallAsync(InstallPanelRequest request)
    {
        var templateDirectory = Path.Combine(AppContext.BaseDirectory, "PanelTemplate");
        var port = PanelPackageInstaller.ResolveConfiguredPort();
        var result = PanelPackageInstaller.InstallOrRepair(request.Path, templateDirectory, port);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }

    public static IResult UninstallAsync(string? path)
    {
        var result = PanelPackageInstaller.Uninstall(path);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }
}

public record ValidatePanelPathRequest(string? Path);

public record InstallPanelRequest(string Path);

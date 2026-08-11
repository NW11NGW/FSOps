using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Detect / validate / install / status / move / uninstall for the FSOps in-game panel package (see
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
        group.MapPost("/panel/move", MoveAsync);
        group.MapDelete("/panel/uninstall", UninstallAsync);
    }

    private static string TemplateDirectory => Path.Combine(AppContext.BaseDirectory, "PanelTemplate");

    public static IResult DetectAsync() =>
        Results.Ok(PanelPackageInstaller.DetectCommunityFolderCandidates());

    public static IResult ValidateAsync(ValidatePanelPathRequest request) =>
        Results.Ok(PanelPackageInstaller.ValidateCommunityFolder(request.Path));

    /// <summary>
    /// With no <paramref name="path"/>, reports on the folder currently saved in settings - the
    /// original behaviour, unchanged. With one, reports on that folder instead, which is what lets
    /// Settings answer "is there still a copy in the folder I'm about to move away from?" before
    /// the player commits to a change.
    ///
    /// <para>
    /// The supplied path is caller-controlled, so it goes through exactly the same
    /// ValidateCommunityFolder gate the install path uses (inside GetStatus): anything that isn't a
    /// folder named "Community" is refused before a single directory is looked at, which keeps this
    /// from becoming a way to probe arbitrary locations on disk. Refusals come back as 200 with
    /// Success=false and a reason, because "that path is not a Community folder" is an answer worth
    /// showing the player, not a transport-level failure.
    /// </para>
    /// </summary>
    public static async Task<IResult> GetStatusAsync(FsOpsDbContext db, ICurrentUser currentUser, string? path, CancellationToken ct)
    {
        var port = PanelPackageInstaller.ResolveConfiguredPort();

        if (!string.IsNullOrWhiteSpace(path))
        {
            return Results.Ok(PanelPackageInstaller.GetStatus(path, port));
        }

        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == currentUser.UserId, ct);
        var status = PanelPackageInstaller.GetStatus(settings?.CommunityFolderPath, port);
        return Results.Ok(status);
    }

    public static IResult InstallAsync(InstallPanelRequest request)
    {
        var port = PanelPackageInstaller.ResolveConfiguredPort();
        var result = PanelPackageInstaller.InstallOrRepair(request.Path, TemplateDirectory, port);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }

    /// <summary>
    /// Reinstalls into <c>ToPath</c> and then clears out <c>FromPath</c>. Kept as one server-side
    /// operation rather than two calls from the SPA so the ordering is guaranteed: the new copy is
    /// written first, and the old one is only removed once that worked.
    /// </summary>
    public static IResult MoveAsync(MovePanelRequest request)
    {
        var port = PanelPackageInstaller.ResolveConfiguredPort();
        var result = PanelPackageInstaller.MoveInstall(request.FromPath, request.ToPath, TemplateDirectory, port);
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

public record MovePanelRequest(string? FromPath, string ToPath);

using FSOps.Core.SimAircraft;
using FSOps.Server.Auth;
using FSOps.Server.Services;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Which aircraft the player can actually load in their simulator.
///
/// <para>Nothing here is player-visible on its own - it feeds contract flying, where a job arrives
/// with an aircraft attached. The whole point is that a contract is never written for something the
/// player does not have, so every answer this returns is framed as evidence ("found in your
/// Community folder") rather than as a verdict, and the player can always overrule it.</para>
/// </summary>
public static class SimAircraftEndpoints
{
    public static void MapSimAircraftEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/sim-aircraft", GetAsync);
        group.MapPost("/sim-aircraft/scan", ScanAsync);
        group.MapPut("/sim-aircraft", UpdateAsync);
        group.MapPut("/sim-aircraft/{typeDesignator}", SetOverrideAsync);
        group.MapGet("/sim-aircraft/community-folders", FindFoldersAsync);
    }

    private static async Task<IResult> GetAsync(SimAircraftService service, ICurrentUser currentUser, CancellationToken ct) =>
        Results.Ok(Present(await service.GetAsync(currentUser.UserId, ct)));

    private static async Task<IResult> ScanAsync(SimAircraftService service, ICurrentUser currentUser, CancellationToken ct) =>
        Results.Ok(Present(await service.ScanAsync(currentUser.UserId, ct)));

    private static async Task<IResult> UpdateAsync(
        UpdateSimAircraftRequest request,
        SimAircraftService service,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!Enum.TryParse<SimEdition>(request.Edition, ignoreCase: true, out var edition))
        {
            return Results.BadRequest(new { error = "edition must be Standard, Deluxe or PremiumDeluxe." });
        }

        var state = await service.UpdateAsync(
            currentUser.UserId,
            edition,
            request.CommunityFolderPath,
            request.ClearCommunityFolderPath,
            ct);

        return Results.Ok(Present(state));
    }

    private static async Task<IResult> SetOverrideAsync(
        string typeDesignator,
        SetAircraftOverrideRequest request,
        SimAircraftService service,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        // Checked here rather than caught from the service, so the message the SPA shows is written
        // for a person rather than being an ArgumentException's parameter-name suffix.
        if (ContractAircraftCatalogue.Find(typeDesignator) is null)
        {
            return Results.BadRequest(new { error = $"'{typeDesignator}' is not an aircraft FSOps knows about." });
        }

        var state = await service.SetOverrideAsync(currentUser.UserId, typeDesignator, request.Available, ct);
        return Results.Ok(Present(state));
    }

    /// <summary>
    /// Every Community folder FSOps can find on this machine. Offered so somebody who has both
    /// simulators, or who moved their packages to another drive, can pick rather than type a path.
    /// An empty list means "could not find one", never "you have none".
    /// </summary>
    private static Task<IResult> FindFoldersAsync() =>
        Task.FromResult(Results.Ok(new { folders = SimInstallLocator.FindCommunityFolders() }));

    /// <summary>
    /// Flattens the domain shape into the field names the SPA and the docs agree on, so the API
    /// boundary stays stable even when the internal records change.
    /// </summary>
    private static SimAircraftResponse Present(SimAircraftState state) =>
        new(
            state.Edition.ToString(),
            state.ConfiguredCommunityFolderPath,
            state.EffectiveCommunityFolderPath,
            state.LastScan is null
                ? null
                : new SimAircraftScanResponse(
                    state.LastScan.Outcome.ToString(),
                    state.LastScan.CommunityFolderPath,
                    state.LastScan.ScannedUtc,
                    state.LastScan.PackagesInspected,
                    state.LastScan.AircraftPackages
                        .Select(p => new SimAircraftPackageResponse(p.PackageFolder, p.PackageTitle, p.RawDesignator, p.TypeDesignator))
                        .ToList(),
                    state.LastScan.BasePackageTypeDesignators),
            state.Aircraft
                .Select(r => new SimAircraftEntryResponse(
                    r.Aircraft.TypeDesignator,
                    r.Aircraft.Name,
                    r.Aircraft.Manufacturer,
                    r.Aircraft.Category.ToString(),
                    r.Aircraft.Seats,
                    r.Aircraft.PayloadKg,
                    r.Aircraft.RangeNm,
                    r.Aircraft.CruiseTasKts,
                    r.Aircraft.ShipsWith.ToString(),
                    r.Available,
                    r.Evidence.ToString()))
                .ToList());
}

/// <param name="Edition">Standard, Deluxe or PremiumDeluxe.</param>
/// <param name="CommunityFolderPath">
/// An explicit path to store, or null to leave whatever is already stored alone. Use
/// <paramref name="ClearCommunityFolderPath"/> to go back to letting FSOps find it - a distinction
/// that matters, because "do not change this" and "forget this" are different requests and a null
/// cannot mean both.
/// </param>
/// <param name="ClearCommunityFolderPath">Forget the stored path and auto-detect again.</param>
public record UpdateSimAircraftRequest(
    string? Edition,
    string? CommunityFolderPath,
    bool ClearCommunityFolderPath);

/// <param name="Available">
/// True to tick the aircraft on, false to tick it off, null to clear the tick and go back to what
/// FSOps worked out on its own.
/// </param>
public record SetAircraftOverrideRequest(bool? Available);

public record SimAircraftResponse(
    string Edition,
    string? ConfiguredCommunityFolderPath,
    string? EffectiveCommunityFolderPath,
    SimAircraftScanResponse? LastScan,
    IReadOnlyList<SimAircraftEntryResponse> Aircraft);

public record SimAircraftScanResponse(
    string Outcome,
    string? CommunityFolderPath,
    DateTimeOffset ScannedUtc,
    int PackagesInspected,
    IReadOnlyList<SimAircraftPackageResponse> AircraftPackages,
    IReadOnlyList<string> BasePackageTypeDesignators);

public record SimAircraftPackageResponse(
    string PackageFolder,
    string PackageTitle,
    string? RawDesignator,
    string? TypeDesignator);

public record SimAircraftEntryResponse(
    string TypeDesignator,
    string Name,
    string Manufacturer,
    string Category,
    int Seats,
    int PayloadKg,
    int RangeNm,
    int CruiseTasKts,
    string ShipsWith,
    bool Available,
    string Evidence);

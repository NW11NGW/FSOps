using FSOps.Server.Services;

namespace FSOps.Server.Endpoints;

/// <summary>
/// The SPA's view of the updater. Every route here answers with the same
/// <see cref="UpdateStatusResponse"/> shape so the client has exactly one thing to render and one
/// thing to poll.
///
/// <para>Nothing on this surface can fail loudly. A check that could not reach GitHub returns 200
/// with <c>lastCheckFailed: true</c> and no update, which is what "you are up to date" also returns -
/// the UI is built so those two are indistinguishable, on purpose. There is no error status here for
/// the network being down, because the user having no internet is not an error condition for a flight
/// simulator companion app.</para>
///
/// <para><c>GET /update/status</c> never waits on the network. It reads the cached result and, if a
/// check is due and the feature is on, starts one in the background. When the feature is off it does
/// not even do that - see <see cref="UpdateChecker.BeginBackgroundCheck"/>.</para>
/// </summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/update/status", GetStatusAsync);
        group.MapPost("/update/check", CheckAsync);
        group.MapPut("/update/preferences", SetPreferencesAsync);
        group.MapPut("/update/channel", SetChannelAsync);
        group.MapPost("/update/dismiss", DismissAsync);
        group.MapPost("/update/download", DownloadAsync);
        group.MapPost("/update/reveal", RevealAsync);
    }

    private static async Task<IResult> GetStatusAsync(UpdateChecker checker, CancellationToken ct)
    {
        var status = await checker.GetStatusAsync(ct);
        checker.BeginBackgroundCheck();

        // Deliberately the status from BEFORE the background check is started: this response must
        // not wait for it. The client picks the result up on its next poll.
        return Results.Ok(status);
    }

    /// <summary>An explicit "check now". Awaited, because the user asked and is watching - but it
    /// still cannot fail: an unreachable GitHub comes back as a normal 200 with no update.</summary>
    private static async Task<IResult> CheckAsync(UpdateChecker checker, CancellationToken ct)
    {
        var status = await checker.CheckAsync(force: true, ct);
        return Results.Ok(status);
    }

    private static async Task<IResult> SetPreferencesAsync(UpdatePreferencesRequest request, UpdateChecker checker, CancellationToken ct) =>
        Results.Ok(await checker.SetEnabledAsync(request.Enabled, ct));

    /// <summary>
    /// Chooses which releases may be offered, and re-checks against the new choice before answering -
    /// so the status the user sees on the next render is about the channel they just picked, not the
    /// one they left.
    /// <para>
    /// The one route on this surface that can return a 400, and only for a channel name that does not
    /// exist. That is a caller bug rather than a user-facing failure, and the alternative - silently
    /// choosing a channel on the user's behalf because their request did not parse - is precisely the
    /// kind of quiet substitution this setting must never make.
    /// </para>
    /// </summary>
    private static async Task<IResult> SetChannelAsync(UpdateChannelRequest request, UpdateChecker checker, CancellationToken ct)
    {
        if (!UpdateChecker.TryParseChannel(request.Channel, out var channel))
        {
            return Results.BadRequest(new { error = "channel must be 'stable' or 'development'." });
        }

        return Results.Ok(await checker.SetChannelAsync(channel, ct));
    }

    private static async Task<IResult> DismissAsync(UpdateChecker checker, CancellationToken ct) =>
        Results.Ok(await checker.DismissAsync(ct));

    /// <summary>
    /// Starts the download the user asked for. Returns immediately with
    /// <c>downloadState: "downloading"</c>; the client polls <c>/update/status</c> for the outcome,
    /// because an installer can be large enough that holding a request open for it would simply time
    /// out.
    /// </summary>
    private static async Task<IResult> DownloadAsync(UpdateChecker checker, CancellationToken ct) =>
        Results.Ok(await checker.BeginDownloadAsync(ct));

    /// <summary>
    /// Shows the verified installer's folder. Re-verifies the file's SHA-256 first - see
    /// <see cref="UpdateChecker.RevealAsync"/>. FSOps never runs the installer itself.
    /// </summary>
    private static async Task<IResult> RevealAsync(UpdateChecker checker, CancellationToken ct)
    {
        var result = await checker.RevealAsync(ct);
        return result.Success
            ? Results.Ok(new { opened = true, folder = checker.UpdatesDirectory })
            : Results.BadRequest(new { error = result.Message });
    }
}

public sealed record UpdatePreferencesRequest(bool Enabled);

/// <summary>
/// The channel, by name: "stable" or "development". A separate request type from
/// <see cref="UpdatePreferencesRequest"/> rather than a field added to it, because a client that
/// posted preferences without a channel would otherwise reset the channel as a side effect of
/// changing something unrelated.
/// </summary>
public sealed record UpdateChannelRequest(string? Channel);

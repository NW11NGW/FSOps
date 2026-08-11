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
        group.MapGet("/update/status", GetStatus);
        group.MapPost("/update/check", CheckAsync);
        group.MapPut("/update/preferences", SetPreferences);
        group.MapPost("/update/dismiss", Dismiss);
        group.MapPost("/update/download", Download);
        group.MapPost("/update/reveal", RevealAsync);
    }

    private static IResult GetStatus(UpdateChecker checker)
    {
        var status = checker.GetStatus();
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

    private static IResult SetPreferences(UpdatePreferencesRequest request, UpdateChecker checker) =>
        Results.Ok(checker.SetEnabled(request.Enabled));

    private static IResult Dismiss(UpdateChecker checker) => Results.Ok(checker.Dismiss());

    /// <summary>
    /// Starts the download the user asked for. Returns immediately with
    /// <c>downloadState: "downloading"</c>; the client polls <c>/update/status</c> for the outcome,
    /// because an installer can be large enough that holding a request open for it would simply time
    /// out.
    /// </summary>
    private static IResult Download(UpdateChecker checker) => Results.Ok(checker.BeginDownload());

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

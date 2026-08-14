using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSOps.Server.Services;

/// <summary>One downloadable file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long SizeBytes);

/// <summary>
/// A GitHub release, trimmed to what the updater needs. <see cref="IsDraft"/> and
/// <see cref="IsPrerelease"/> are carried through deliberately rather than filtered inside the
/// transport, so the refusal to offer them is an explicit, testable decision in
/// <see cref="UpdateChecker"/> rather than an accident of which endpoint was called.
/// </summary>
public sealed record GitHubRelease(
    string TagName,
    bool IsDraft,
    bool IsPrerelease,
    string HtmlUrl,
    string? Body,
    DateTimeOffset? PublishedAtUtc,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Outcome of a release lookup. A failed lookup is an ordinary, expected result - not an exception -
/// because "no internet", "GitHub is down", "rate limited", "corporate proxy ate it" and "offline
/// sim rig" all have to be indistinguishable from "no update available" as far as the user is
/// concerned. <see cref="FailureReason"/> is for the log only and is never shown in the UI.
/// </summary>
public sealed record ReleaseLookup(bool Success, GitHubRelease? Release, string? FailureReason)
{
    public static ReleaseLookup Failed(string reason) => new(false, null, reason);

    public static ReleaseLookup Found(GitHubRelease release) => new(true, release, null);
}

/// <summary>
/// Outcome of listing recent releases - what the development channel reads, because
/// <c>/releases/latest</c> excludes pre-releases by definition and so can never return one.
/// <para>
/// The list arrives unfiltered on purpose, exactly as <see cref="GitHubRelease.IsPrerelease"/> does:
/// deciding which of these releases may be offered is <see cref="UpdateChecker"/>'s job and is
/// asserted there. A transport that quietly pre-filtered would make the channel's behaviour an
/// accident of which URL was called.
/// </para>
/// </summary>
public sealed record ReleaseListLookup(bool Success, IReadOnlyList<GitHubRelease> Releases, string? FailureReason)
{
    public static ReleaseListLookup Failed(string reason) => new(false, Array.Empty<GitHubRelease>(), reason);

    public static ReleaseListLookup Found(IReadOnlyList<GitHubRelease> releases) => new(true, releases, null);
}

/// <summary>Outcome of streaming a release asset to disk. Never throws for expected failures.</summary>
public sealed record AssetDownload(bool Success, long BytesWritten, string? FailureReason)
{
    public static AssetDownload Failed(string reason) => new(false, 0, reason);
}

/// <summary>Outcome of fetching a small text asset (the checksum sidecar).</summary>
public sealed record TextDownload(bool Success, string? Text, string? FailureReason)
{
    public static TextDownload Failed(string reason) => new(false, null, reason);
}

public interface IGitHubReleaseClient
{
    Task<ReleaseLookup> GetLatestReleaseAsync(CancellationToken ct);

    /// <summary>Recent releases, newest-published first, as GitHub returns them. Includes
    /// pre-releases; excludes drafts, which the API does not disclose to an unauthenticated
    /// caller.</summary>
    Task<ReleaseListLookup> GetRecentReleasesAsync(int count, CancellationToken ct);

    Task<TextDownload> DownloadTextAsync(Uri url, int maxBytes, CancellationToken ct);

    Task<AssetDownload> DownloadToFileAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct);
}

/// <summary>
/// Reads the FSOps repository's latest published release from GitHub's public REST API, and
/// downloads release assets on demand.
/// <para>
/// This is the app's update-check transport and the only place it talks to GitHub. Everything here
/// is written to fail quietly: every expected failure - DNS, TLS, timeout, 403 rate limit, 404, a
/// body that does not parse, a redirect somewhere unexpected - returns a failed result rather than
/// throwing, so the caller can treat it identically to "you are up to date". Nothing here ever
/// reaches the user's screen as an error.
/// </para>
/// <para>
/// Download URLs are checked against an allowlist of GitHub hosts before a single byte is
/// requested. The release JSON already arrives over TLS from api.github.com, so this is
/// defence in depth rather than the primary control - but an updater that will fetch an executable
/// from whatever host a JSON field happens to name is exactly the kind of thing that turns one
/// compromised response into a compromised machine, and refusing off-allowlist hosts costs nothing.
/// </para>
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    public const string HttpClientName = "github-releases";

    /// <summary>The FSOps repository the update check reads. Public, no token, no authentication.</summary>
    private const string RepositoryOwner = "NW11NGW";
    private const string RepositoryName = "FSOps";

    private const string ReleasesUrl =
        $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases";

    /// <summary>
    /// The stable channel's feed. GitHub documents this as the newest release excluding both drafts
    /// and pre-releases, which is exactly the guarantee the stable channel rests on: a pre-release
    /// cannot appear here however it is tagged, so publishing one can never reach somebody who did
    /// not ask for it.
    /// </summary>
    private const string LatestReleaseUrl = ReleasesUrl + "/latest";

    /// <summary>GitHub rejects API requests that arrive without a User-Agent, with a 403.</summary>
    private const string UserAgent = "FSOps-UpdateCheck";

    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Hosts a release asset may be fetched from. GitHub serves the release JSON from api.github.com
    /// and redirects asset downloads to its object storage, so all four are needed; nothing else is.
    /// </summary>
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GitHubReleaseClient> _logger;

    public GitHubReleaseClient(IHttpClientFactory httpClientFactory, ILogger<GitHubReleaseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// True when <paramref name="url"/> is an https URL on a GitHub host. Public so the rule can be
    /// tested directly and reused by callers that want to reject an asset before queuing it.
    /// </summary>
    public static bool IsAllowedDownloadUrl(Uri? url) =>
        url is not null &&
        url.IsAbsoluteUri &&
        string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        AllowedHosts.Contains(url.Host);

    public async Task<ReleaseLookup> GetLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MetadataTimeout);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                // 403 with a rate-limit header, 404 before the first release exists, 5xx during an
                // outage - all the same outcome to the user, all worth distinguishing in the log.
                var rateLimited = response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
                    response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) &&
                    remaining.FirstOrDefault() == "0";
                return ReleaseLookup.Failed(rateLimited
                    ? "GitHub API rate limit reached"
                    : $"GitHub API returned {(int)response.StatusCode}");
            }

            var payload = await response.Content.ReadFromJsonAsync<GitHubReleasePayload>(JsonOptions, timeoutCts.Token);
            if (payload is null)
            {
                return ReleaseLookup.Failed("release payload did not parse");
            }

            var release = MapRelease(payload);
            return release is null
                ? ReleaseLookup.Failed("release payload had no tag name")
                : ReleaseLookup.Found(release);
        }
        catch (Exception ex) when (IsExpectedTransportFailure(ex, ct))
        {
            _logger.LogDebug(ex, "Update check could not reach GitHub - treating as no update available");
            return ReleaseLookup.Failed(ex.GetType().Name);
        }
    }

    /// <summary>
    /// Lists recent releases for the development channel. Same host, same headers, same timeout and
    /// the same silent-failure contract as <see cref="GetLatestReleaseAsync"/> - the only difference
    /// between the two channels at this layer is which URL is asked and how many results come back.
    /// <para>
    /// A release whose payload carries no tag name is dropped rather than failing the whole lookup:
    /// one unusable entry in a list of ten is not a reason to tell the user nothing.
    /// </para>
    /// </summary>
    public async Task<ReleaseListLookup> GetRecentReleasesAsync(int count, CancellationToken ct)
    {
        var perPage = Math.Clamp(count, 1, 100);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ReleasesUrl}?per_page={perPage}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MetadataTimeout);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var rateLimited = response.StatusCode == System.Net.HttpStatusCode.Forbidden &&
                    response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) &&
                    remaining.FirstOrDefault() == "0";
                return ReleaseListLookup.Failed(rateLimited
                    ? "GitHub API rate limit reached"
                    : $"GitHub API returned {(int)response.StatusCode}");
            }

            var payloads = await response.Content.ReadFromJsonAsync<List<GitHubReleasePayload>>(JsonOptions, timeoutCts.Token);
            if (payloads is null)
            {
                return ReleaseListLookup.Failed("release list payload did not parse");
            }

            var releases = payloads
                .Select(MapRelease)
                .Where(r => r is not null)
                .Select(r => r!)
                .ToList();

            return ReleaseListLookup.Found(releases);
        }
        catch (Exception ex) when (IsExpectedTransportFailure(ex, ct))
        {
            _logger.LogDebug(ex, "Update check could not list releases - treating as no update available");
            return ReleaseListLookup.Failed(ex.GetType().Name);
        }
    }

    /// <summary>
    /// Turns one release payload into the shape the checker reasons about, or null when it carries no
    /// tag name and therefore nothing that could be compared to a version. Assets whose URL does not
    /// parse are dropped here; assets whose URL is not on an allowed host are dropped later, by the
    /// selection rules, so that refusal stays visible where it is asserted.
    /// </summary>
    private static GitHubRelease? MapRelease(GitHubReleasePayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.TagName))
        {
            return null;
        }

        var assets = (payload.Assets ?? new List<GitHubAssetPayload>())
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .Select(a => Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out var uri)
                ? new ReleaseAsset(a.Name!, uri, a.Size)
                : null)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        return new GitHubRelease(
            payload.TagName!,
            payload.Draft,
            payload.Prerelease,
            payload.HtmlUrl ?? $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases",
            payload.Body,
            payload.PublishedAt,
            assets);
    }

    public async Task<TextDownload> DownloadTextAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        if (!IsAllowedDownloadUrl(url))
        {
            return TextDownload.Failed($"refused download from disallowed host '{url?.Host}'");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(MetadataTimeout);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return TextDownload.Failed($"asset request returned {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            var buffer = new byte[maxBytes];
            var total = 0;
            while (total < maxBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), timeoutCts.Token);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return new TextDownload(true, Encoding.UTF8.GetString(buffer, 0, total), null);
        }
        catch (Exception ex) when (IsExpectedTransportFailure(ex, ct))
        {
            _logger.LogDebug(ex, "Update asset text download failed");
            return TextDownload.Failed(ex.GetType().Name);
        }
    }

    public async Task<AssetDownload> DownloadToFileAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct)
    {
        if (!IsAllowedDownloadUrl(url))
        {
            return AssetDownload.Failed($"refused download from disallowed host '{url?.Host}'");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DownloadTimeout);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return AssetDownload.Failed($"asset request returned {(int)response.StatusCode}");
            }

            // Declared length is only a hint - the size cap below is enforced against bytes actually
            // written, so a lying Content-Length cannot be used to fill the user's disk.
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > 0 && declaredLength > maxBytes)
            {
                return AssetDownload.Failed($"asset is larger than the {maxBytes} byte limit");
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            await using var destination = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

            var buffer = new byte[81920];
            long written = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, timeoutCts.Token);
                if (read == 0)
                {
                    break;
                }

                written += read;
                if (written > maxBytes)
                {
                    return AssetDownload.Failed($"asset exceeded the {maxBytes} byte limit mid-download");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
            }

            await destination.FlushAsync(timeoutCts.Token);
            return new AssetDownload(true, written, null);
        }
        catch (Exception ex) when (IsExpectedTransportFailure(ex, ct) || ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Update asset download failed");
            return AssetDownload.Failed(ex.GetType().Name);
        }
    }

    /// <summary>
    /// Failures the updater is expected to absorb silently. A cancellation the caller asked for is
    /// deliberately excluded so a shutdown or an abandoned request still propagates as a
    /// cancellation, exactly as VatsimNetworkClient does.
    /// </summary>
    private static bool IsExpectedTransportFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or NotSupportedException
            or TaskCanceledException or OperationCanceledException or InvalidOperationException or UriFormatException
        && !ct.IsCancellationRequested;

    private sealed record GitHubReleasePayload(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("assets")] List<GitHubAssetPayload>? Assets);

    private sealed record GitHubAssetPayload(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size);
}

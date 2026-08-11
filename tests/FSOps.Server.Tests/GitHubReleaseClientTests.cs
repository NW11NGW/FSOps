using System.Net;
using System.Text;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The update check's transport, against a stub handler. GitHub is never contacted from a test -
/// these all pass with no internet, which is the same state most of them are simulating.
/// </summary>
public class GitHubReleaseClientTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "fsops-release-client-tests", Guid.NewGuid().ToString("N"));

    public GitHubReleaseClientTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static GitHubReleaseClient Create(FakeReleaseHttpHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<GitHubReleaseClient>.Instance);

    [Fact]
    public async Task GetLatestRelease_ReadsTheFieldsTheUpdaterActuallyDependsOn()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson("api.github.com", """
        {
          "tag_name": "v1.4.0",
          "draft": true,
          "prerelease": true,
          "html_url": "https://github.com/NW11NGW/FSOps/releases/tag/v1.4.0",
          "body": "Release notes here.",
          "published_at": "2026-08-10T09:00:00Z",
          "assets": [
            { "name": "FSOps-Setup-1.4.0.exe", "browser_download_url": "https://github.com/NW11NGW/FSOps/releases/download/v1.4.0/FSOps-Setup-1.4.0.exe", "size": 62914560 }
          ]
        }
        """);

        var lookup = await Create(handler).GetLatestReleaseAsync(CancellationToken.None);

        Assert.True(lookup.Success);
        var release = lookup.Release!;
        Assert.Equal("v1.4.0", release.TagName);
        Assert.True(release.IsDraft);
        Assert.True(release.IsPrerelease);
        Assert.Equal("Release notes here.", release.Body);
        Assert.Equal(new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero), release.PublishedAtUtc);
        Assert.Single(release.Assets);
        Assert.Equal(62914560, release.Assets[0].SizeBytes);
    }

    [Fact]
    public async Task TheRequestCarriesAUserAgent_BecauseGitHubRejectsOnesThatDoNot()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeReleaseHttpHandler();
        handler.When("api.github.com", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "tag_name": "v1.0.0", "assets": [] }""", Encoding.UTF8, "application/json"),
        });

        // Assert indirectly through a handler that inspects the request it is given.
        var inspecting = new InspectingHandler(request => seen = request);
        var client = new GitHubReleaseClient(new FakeHttpClientFactory(inspecting), NullLogger<GitHubReleaseClient>.Instance);
        await client.GetLatestReleaseAsync(CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Contains("FSOps", seen!.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", seen.Headers.Accept.ToString());
    }

    [Fact]
    public async Task AssetsWithNoNameOrNoUrl_AreDroppedRatherThanCarriedAsNulls()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson("api.github.com", """
        {
          "tag_name": "v1.0.0",
          "assets": [
            { "name": null, "browser_download_url": "https://github.com/a.exe", "size": 1 },
            { "name": "b.exe", "browser_download_url": null, "size": 1 },
            { "name": "c.exe", "browser_download_url": "not a url at all", "size": 1 },
            { "name": "d.exe", "browser_download_url": "https://github.com/d.exe", "size": 1 }
          ]
        }
        """);

        var lookup = await Create(handler).GetLatestReleaseAsync(CancellationToken.None);

        Assert.True(lookup.Success);
        Assert.Single(lookup.Release!.Assets);
        Assert.Equal("d.exe", lookup.Release.Assets[0].Name);
    }

    [Fact]
    public async Task AReleaseWithNoTag_IsAFailedLookup()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson("api.github.com", """{ "assets": [] }""");

        var lookup = await Create(handler).GetLatestReleaseAsync(CancellationToken.None);

        Assert.False(lookup.Success);
        Assert.Null(lookup.Release);
    }

    [Fact]
    public async Task RateLimiting_IsRecognisedForTheLog_ButIsStillJustAFailedLookup()
    {
        var handler = new FakeReleaseHttpHandler().WhenRateLimited("api.github.com");

        var lookup = await Create(handler).GetLatestReleaseAsync(CancellationToken.None);

        Assert.False(lookup.Success);
        Assert.Contains("rate limit", lookup.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesRatherThanBeingSwallowedAsAFailedLookup()
    {
        // Shutdown and an abandoned request are not the same event as GitHub being unreachable, and
        // must not be reported as one - the same rule VatsimNetworkClient follows.
        var handler = new FakeReleaseHttpHandler().WhenJson("api.github.com", """{ "tag_name": "v1.0.0" }""");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(handler).GetLatestReleaseAsync(cts.Token));
    }

    // ---------------------------------------------------------------------------------------
    // Download host allowlist
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.github.com/x")]
    [InlineData("https://github.com/NW11NGW/FSOps/releases/download/v1/x.exe")]
    [InlineData("https://objects.githubusercontent.com/x")]
    [InlineData("https://release-assets.githubusercontent.com/x")]
    public void GitHubsOwnHostsOverHttps_AreAllowed(string url)
    {
        Assert.True(GitHubReleaseClient.IsAllowedDownloadUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("http://github.com/x.exe")]
    [InlineData("https://github.com.evil.example/x.exe")]
    [InlineData("https://evil.example/x.exe")]
    [InlineData("file:///C:/Windows/System32/x.exe")]
    [InlineData("ftp://github.com/x.exe")]
    public void AnythingElse_IsRefused(string url)
    {
        Assert.False(GitHubReleaseClient.IsAllowedDownloadUrl(new Uri(url)));
    }

    [Fact]
    public async Task ADownloadFromADisallowedHost_IsRefusedWithoutARequestBeingMade()
    {
        var handler = new FakeReleaseHttpHandler().WhenBytes("evil", new byte[] { 1 });
        var destination = Path.Combine(_directory, "x.exe");

        var result = await Create(handler).DownloadToFileAsync(
            new Uri("https://evil.example/x.exe"), destination, 1024, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(handler.RequestedUrls);
        Assert.False(File.Exists(destination));
    }

    // ---------------------------------------------------------------------------------------
    // Size cap
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAssetLargerThanTheCap_IsAbandonedMidDownload()
    {
        var bytes = new byte[200_000];
        var handler = new FakeReleaseHttpHandler().WhenBytes("github.com", bytes);
        var destination = Path.Combine(_directory, "big.exe");

        var result = await Create(handler).DownloadToFileAsync(
            new Uri("https://github.com/big.exe"), destination, maxBytes: 1024, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("limit", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASuccessfulDownload_WritesExactlyTheBytesServed()
    {
        var bytes = Encoding.UTF8.GetBytes("installer contents");
        var handler = new FakeReleaseHttpHandler().WhenBytes("github.com", bytes);
        var destination = Path.Combine(_directory, "ok.exe");

        var result = await Create(handler).DownloadToFileAsync(
            new Uri("https://github.com/ok.exe"), destination, 1024 * 1024, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(bytes.Length, result.BytesWritten);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadText_StopsAtTheByteLimit_SoAnOversizedChecksumFileCannotBeUsedAsAPayload()
    {
        var handler = new FakeReleaseHttpHandler().WhenText("github.com", new string('a', 100_000));

        var result = await Create(handler).DownloadTextAsync(
            new Uri("https://github.com/x.sha256"), maxBytes: 64, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(64, result.Text!.Length);
    }

    private sealed class InspectingHandler : HttpMessageHandler
    {
        private readonly Action<HttpRequestMessage> _inspect;

        public InspectingHandler(Action<HttpRequestMessage> inspect) => _inspect = inspect;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _inspect(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "tag_name": "v1.0.0", "assets": [] }""", Encoding.UTF8, "application/json"),
            });
        }
    }
}

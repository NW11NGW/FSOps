using System.Net;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The updater's decision rules, exercised against a stub transport. Every interesting path here is
/// a failure path - the happy case is one line - so these tests deliberately spend almost all their
/// effort on what happens when the network, the release, or the release's assets are wrong.
/// <para>
/// No test in this file touches the network or the real data directory. They pass with the machine
/// completely offline, which is the same condition most of them are simulating.
/// </para>
/// </summary>
public class UpdateCheckerTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private const string ReleaseApiUrl = "api.github.com";
    private const string InstallerUrl = "https://github.com/NW11NGW/FSOps/releases/download/v0.2.0/FSOps-Setup-0.2.0.exe";
    private const string ChecksumUrl = InstallerUrl + ".sha256";

    private readonly TempUpdateStorage _storage = new();

    public void Dispose() => _storage.Dispose();

    private UpdateChecker CreateChecker(
        FakeReleaseHttpHandler handler,
        FakeClock clock,
        string currentVersion = "0.1.0",
        IUpdateChannelStore? channels = null)
    {
        var client = new GitHubReleaseClient(new FakeHttpClientFactory(handler), NullLogger<GitHubReleaseClient>.Instance);
        return new UpdateChecker(
            client,
            _storage,
            channels ?? new FakeUpdateChannelStore(),
            clock,
            NullLogger<UpdateChecker>.Instance)
        {
            CurrentVersion = currentVersion,
        };
    }

    /// <summary>Builds a release payload in the shape GitHub's /releases/latest actually returns.</summary>
    private static string ReleaseJson(
        string tag = "v0.2.0",
        bool draft = false,
        bool prerelease = false,
        bool withInstaller = true,
        bool withChecksum = true,
        string installerName = "FSOps-Setup-0.2.0.exe")
    {
        var assets = new List<string>();
        if (withInstaller)
        {
            assets.Add($$"""{ "name": "{{installerName}}", "browser_download_url": "https://github.com/NW11NGW/FSOps/releases/download/{{tag}}/{{installerName}}", "size": 1024 }""");
        }

        if (withChecksum)
        {
            assets.Add($$"""{ "name": "{{installerName}}.sha256", "browser_download_url": "https://github.com/NW11NGW/FSOps/releases/download/{{tag}}/{{installerName}}.sha256", "size": 80 }""");
        }

        return $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/NW11NGW/FSOps/releases/tag/{{tag}}",
          "body": "Fixed the thing.",
          "published_at": "2026-08-10T09:00:00Z",
          "assets": [ {{string.Join(",", assets)}} ]
        }
        """;
    }

    // -----------------------------------------------------------------------------------
    // The happy case, so the failure cases below mean something
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task NewerStableRelease_IsOffered_WithItsAssetsAndReleasePage()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.2.0", status.LatestVersion);
        Assert.Equal("0.1.0", status.CurrentVersion);
        Assert.False(status.Dismissed);
        Assert.False(status.LastCheckFailed);
        Assert.Equal(Base, status.LastCheckedUtc);
        Assert.Equal("https://github.com/NW11NGW/FSOps/releases/tag/v0.2.0", status.ReleaseUrl);
        Assert.Equal("Fixed the thing.", status.ReleaseNotes);
        Assert.True(status.DownloadAvailable);

        // The result is persisted, not just returned - a restart must not re-check immediately.
        Assert.Equal("0.2.0", _storage.Load().LatestVersion);
    }

    // -----------------------------------------------------------------------------------
    // The kill switch: OFF must mean no packet leaves the machine
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_MakesNoOutboundRequestAtAll_EvenWhenAskedToCheckExplicitly()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var checker = CreateChecker(handler, new FakeClock(Base));

        await checker.SetEnabledAsync(false, CancellationToken.None);
        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.Empty(handler.RequestedUrls);
        Assert.False(status.Enabled);
        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LastCheckedUtc);
    }

    [Fact]
    public async Task Disabled_BackgroundCheckDoesNothing_AndOpensNoConnection()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var checker = CreateChecker(handler, new FakeClock(Base));

        await checker.SetEnabledAsync(false, CancellationToken.None);
        checker.BeginBackgroundCheck();

        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task Disabled_RefusesToDownload_EvenWithAVerifiedUpdateAlreadyPending()
    {
        var installer = new byte[] { 1, 2, 3, 4 };
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson())
            .WhenText(".sha256", Sha256Hex(installer))
            .WhenBytes("FSOps-Setup-0.2.0.exe", installer);
        var checker = CreateChecker(handler, new FakeClock(Base));

        await checker.CheckAsync(force: true, CancellationToken.None);
        var requestsAfterCheck = handler.CallCount;

        await checker.SetEnabledAsync(false, CancellationToken.None);
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(requestsAfterCheck, handler.CallCount);
        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Null(status.DownloadFileName);
    }

    [Fact]
    public async Task ReEnabling_DoesNotResurrectAPreviouslyDeletedDownload()
    {
        var installer = new byte[] { 9, 9, 9 };
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson())
            .WhenText(".sha256", Sha256Hex(installer))
            .WhenBytes("FSOps-Setup-0.2.0.exe", installer);
        var checker = CreateChecker(handler, new FakeClock(Base));

        await checker.CheckAsync(force: true, CancellationToken.None);
        var downloaded = await checker.DownloadAsync(CancellationToken.None);
        Assert.Equal(UpdateDownloadStates.Ready, downloaded.DownloadState);
        var filePath = Path.Combine(_storage.UpdatesDirectory, "FSOps-Setup-0.2.0.exe");
        Assert.True(File.Exists(filePath));

        await checker.SetEnabledAsync(false, CancellationToken.None);
        Assert.False(File.Exists(filePath));

        var status = await checker.SetEnabledAsync(true, CancellationToken.None);
        Assert.NotEqual(UpdateDownloadStates.Ready, status.DownloadState);
        Assert.Null(status.DownloadFileName);
    }

    // -----------------------------------------------------------------------------------
    // Every way the network can let us down: all of them must look like "no update"
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task NoNetworkAtAll_LooksExactlyLikeNoUpdateAvailable()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenThrows(ReleaseApiUrl, new HttpRequestException("No such host is known."));
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
        Assert.True(status.LastCheckFailed);
        Assert.Equal(Base, status.LastCheckedUtc);
    }

    [Fact]
    public async Task RequestThatHangs_IsResolvedByTheClientsOwnTimeout_NotByWaitingForever()
    {
        // The handler never answers. The client's request timeout has to be what ends this; if it
        // did not exist, this test would hang rather than fail, which is exactly the failure mode a
        // user on a captive-portal wifi would see.
        var handler = new FakeReleaseHttpHandler().WhenHangs(ReleaseApiUrl);
        var checker = CreateChecker(handler, new FakeClock(Base));

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var status = await checker.CheckAsync(force: true, guard.Token);

        Assert.False(status.UpdateAvailable);
        Assert.True(status.LastCheckFailed);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task NonSuccessStatus_IsNotAnError_JustNoUpdate(HttpStatusCode status)
    {
        var handler = new FakeReleaseHttpHandler().WhenStatus(ReleaseApiUrl, status);
        var checker = CreateChecker(handler, new FakeClock(Base));

        var result = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(result.UpdateAvailable);
        Assert.True(result.LastCheckFailed);
    }

    [Fact]
    public async Task RateLimited_IsAbsorbedSilently()
    {
        var handler = new FakeReleaseHttpHandler().WhenRateLimited(ReleaseApiUrl);
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.True(status.LastCheckFailed);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "tag_name": """)]
    [InlineData("""{ "unexpected": "shape" }""")]
    public async Task MalformedOrTruncatedPayload_IsAbsorbedSilently(string body)
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, body);
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
    }

    [Fact]
    public async Task AFailedCheck_KeepsWhateverWeAlreadyKnew_RatherThanForgettingTheUpdate()
    {
        var goodHandler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var clock = new FakeClock(Base);
        var checker = CreateChecker(goodHandler, clock);
        await checker.CheckAsync(force: true, CancellationToken.None);

        var badHandler = new FakeReleaseHttpHandler().WhenThrows(ReleaseApiUrl, new HttpRequestException("offline"));
        var offlineChecker = CreateChecker(badHandler, clock);
        var status = await offlineChecker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.2.0", status.LatestVersion);
        Assert.True(status.LastCheckFailed);
    }

    // -----------------------------------------------------------------------------------
    // Releases that must never be offered
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task DraftRelease_IsNeverOffered()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson(draft: true));
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
        Assert.False(status.DownloadAvailable);
    }

    [Fact]
    public async Task PrereleaseFlaggedByGitHub_IsNeverOffered()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson(prerelease: true));
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task PrereleaseByTagAlone_IsNeverOffered_EvenWithTheFlagUnticked()
    {
        // A release published from a "-rc.1" tag without ticking GitHub's pre-release box is a very
        // easy mistake to make; the tag itself has to be enough to refuse it.
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson(tag: "v0.3.0-rc.1", prerelease: false));
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
    }

    [Theory]
    [InlineData("v0.0.9")]
    [InlineData("v0.1.0")]
    public async Task OlderOrEqualRelease_IsNotAnUpdate(string tag)
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson(tag: tag));
        var checker = CreateChecker(handler, new FakeClock(Base), currentVersion: "0.1.0");

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
    }

    [Fact]
    public async Task DoubleDigitMinorVersion_IsRecognisedAsNewer_NotOlder()
    {
        // The bug this whole type exists to prevent: ordinal string comparison puts "0.10.0" before
        // "0.9.0", which would silently switch the updater off forever at exactly this release.
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson(tag: "v0.10.0"));
        var checker = CreateChecker(handler, new FakeClock(Base), currentVersion: "0.9.0");

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.10.0", status.LatestVersion);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("latest")]
    [InlineData("release-2026-08")]
    public async Task ATagThatIsNotAVersion_IsNeverOffered(string tag)
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson(tag: tag));
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
    }

    // -----------------------------------------------------------------------------------
    // Releases whose ASSETS are wrong: notify, but never offer an unverifiable download
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task ReleaseWithNoInstallerAsset_StillTellsTheUser_ButOffersNoDownload()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson(withInstaller: false, withChecksum: false));
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.2.0", status.LatestVersion);
        Assert.NotNull(status.ReleaseUrl);
        Assert.False(status.DownloadAvailable);
    }

    [Fact]
    public async Task ReleaseWithAnInstallerButNoChecksum_OffersNoDownload_AndRequestsNothingIfAsked()
    {
        // This is the supply-chain rule: an unsigned installer with nothing to verify it against is
        // exactly what the checksum requirement exists to refuse. Telling the user a version exists
        // is safe; fetching an unverifiable executable for them is not.
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson(withChecksum: false))
            .WhenBytes("FSOps-Setup-0.2.0.exe", new byte[] { 1, 2, 3 });
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);
        Assert.True(status.UpdateAvailable);
        Assert.False(status.DownloadAvailable);

        var requestsAfterCheck = handler.CallCount;
        var afterDownload = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(requestsAfterCheck, handler.CallCount);
        Assert.Equal(UpdateDownloadStates.Failed, afterDownload.DownloadState);
        Assert.Null(afterDownload.DownloadFileName);
        Assert.False(Directory.Exists(_storage.UpdatesDirectory) &&
            Directory.EnumerateFileSystemEntries(_storage.UpdatesDirectory).Any());
    }

    [Fact]
    public async Task AnAssetNameThatIsNotASafeFileName_IsIgnoredEntirely()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson(installerName: "..%5C..%5CFSOps.exe"));
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.False(status.DownloadAvailable);
    }

    [Fact]
    public void SafeAssetFileName_RefusesAnythingThatCouldEscapeTheUpdatesFolder()
    {
        Assert.Null(UpdateChecker.SafeAssetFileName(@"..\..\evil.exe"));
        Assert.Null(UpdateChecker.SafeAssetFileName("../../evil.exe"));
        Assert.Null(UpdateChecker.SafeAssetFileName(@"C:\Windows\System32\evil.exe"));
        Assert.Null(UpdateChecker.SafeAssetFileName(".hidden"));
        Assert.Null(UpdateChecker.SafeAssetFileName("name with spaces.exe"));
        Assert.Null(UpdateChecker.SafeAssetFileName(new string('a', 200) + ".exe"));
        Assert.Null(UpdateChecker.SafeAssetFileName(null));
        Assert.Null(UpdateChecker.SafeAssetFileName("   "));

        Assert.Equal("FSOps-Setup-0.2.0.exe", UpdateChecker.SafeAssetFileName("FSOps-Setup-0.2.0.exe"));
        Assert.Equal("FSOps_Setup.exe", UpdateChecker.SafeAssetFileName("FSOps_Setup.exe"));

        // The name a beta build produces. Checked rather than assumed: the development channel is
        // pointless if the installer it offers is one this filter throws away, and it would fail
        // silently - the release would be announced and the download simply never offered.
        Assert.Equal(
            "FSOps-Setup-1.1.0-beta.1.exe",
            UpdateChecker.SafeAssetFileName("FSOps-Setup-1.1.0-beta.1.exe"));
        Assert.Equal(
            "FSOps-Setup-1.1.0-beta.1.exe.sha256",
            UpdateChecker.SafeAssetFileName("FSOps-Setup-1.1.0-beta.1.exe.sha256"));
    }

    /// <summary>
    /// And the assets of a beta release are actually selected, not merely survivable as filenames.
    /// The installer/checksum pairing is what <c>CanDownload</c> rests on.
    /// </summary>
    [Fact]
    public void ABetaReleasesInstallerAndChecksum_AreBothSelected()
    {
        const string name = "FSOps-Setup-1.1.0-beta.1.exe";
        var assets = new List<ReleaseAsset>
        {
            new(name, new Uri($"https://github.com/NW11NGW/FSOps/releases/download/v1.1.0-beta.1/{name}"), 1024),
            new($"{name}.sha256", new Uri($"https://github.com/NW11NGW/FSOps/releases/download/v1.1.0-beta.1/{name}.sha256"), 80),
        };

        var installer = UpdateChecker.SelectInstallerAsset(assets);
        Assert.NotNull(installer);
        Assert.Equal(name, installer.Name);
        Assert.NotNull(UpdateChecker.SelectChecksumAsset(assets, installer));
    }

    [Fact]
    public void AnAssetHostedSomewhereOtherThanGitHub_IsRefused()
    {
        var assets = new List<ReleaseAsset>
        {
            new("FSOps-Setup-0.2.0.exe", new Uri("https://not-github.example.com/FSOps-Setup-0.2.0.exe"), 10),
            new("FSOps-Setup-0.2.0.exe.sha256", new Uri("https://not-github.example.com/FSOps-Setup-0.2.0.exe.sha256"), 10),
        };

        Assert.Null(UpdateChecker.SelectInstallerAsset(assets));
    }

    [Fact]
    public void AnAssetOfferedOverPlainHttp_IsRefused()
    {
        var assets = new List<ReleaseAsset>
        {
            new("FSOps-Setup-0.2.0.exe", new Uri("http://github.com/x/FSOps-Setup-0.2.0.exe"), 10),
        };

        Assert.Null(UpdateChecker.SelectInstallerAsset(assets));
    }

    // -----------------------------------------------------------------------------------
    // Caching: cheap and rare, and never a retry storm
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task WithinTheCacheWindow_NoSecondRequestIsMade()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var clock = new FakeClock(Base);
        var checker = CreateChecker(handler, clock);

        await checker.CheckAsync(force: true, CancellationToken.None);
        clock.UtcNow = Base.AddHours(23);
        await checker.CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AfterTheCacheWindow_ANewRequestIsMade()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var clock = new FakeClock(Base);
        var checker = CreateChecker(handler, clock);

        await checker.CheckAsync(force: true, CancellationToken.None);
        clock.UtcNow = Base.Add(UpdateChecker.SuccessfulCheckInterval);
        await checker.CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task AFailedCheck_RetriesSoonerThanASuccessfulOne_ButStillNotImmediately()
    {
        var handler = new FakeReleaseHttpHandler().WhenThrows(ReleaseApiUrl, new HttpRequestException("offline"));
        var clock = new FakeClock(Base);
        var checker = CreateChecker(handler, clock);

        await checker.CheckAsync(force: true, CancellationToken.None);

        clock.UtcNow = Base.AddMinutes(5);
        await checker.CheckAsync(force: false, CancellationToken.None);
        Assert.Equal(1, handler.CallCount);

        clock.UtcNow = Base.Add(UpdateChecker.FailedCheckInterval);
        await checker.CheckAsync(force: false, CancellationToken.None);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task AClockThatJumpsBackwards_DoesNotWedgeTheCheckOffForever()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var clock = new FakeClock(Base);
        var checker = CreateChecker(handler, clock);

        await checker.CheckAsync(force: true, CancellationToken.None);
        clock.UtcNow = Base.AddDays(-3);
        await checker.CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task ACompletedCheck_DoesNotStillClaimToBeChecking()
    {
        // Caught by watching the live endpoint, not by the unit tests that existed at the time: the
        // response was being built while the "checking" flag was still set, so a finished check told
        // the client a check was in progress and the UI sat polling for something already over.
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.Checking);
        Assert.False((await checker.GetStatusAsync(CancellationToken.None)).Checking);
    }

    [Fact]
    public async Task ACheckThatFailed_AlsoStopsClaimingToBeChecking()
    {
        var handler = new FakeReleaseHttpHandler().WhenThrows(ReleaseApiUrl, new HttpRequestException("offline"));
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.Checking);
        Assert.True(status.LastCheckFailed);
    }

    [Fact]
    public async Task StatusEndpointsView_NeverWaitsOnTheNetwork()
    {
        // GetStatusAsync does no network I/O at all - proven here by there being no route registered
        // for the API at all: an implementation that fetched would 404 and mark the check failed.
        var handler = new FakeReleaseHttpHandler();
        var checker = CreateChecker(handler, new FakeClock(Base));

        var status = await checker.GetStatusAsync(CancellationToken.None);

        Assert.Empty(handler.RequestedUrls);
        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LastCheckedUtc);
    }

    // -----------------------------------------------------------------------------------
    // Respecting "no"
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task DismissingAVersion_SilencesThatVersionOnly()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var clock = new FakeClock(Base);
        var checker = CreateChecker(handler, clock);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var dismissed = await checker.DismissAsync(CancellationToken.None);

        Assert.True(dismissed.UpdateAvailable);
        Assert.True(dismissed.Dismissed);

        // A later release starts talking again.
        var newerHandler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson(tag: "v0.3.0", installerName: "FSOps-Setup-0.3.0.exe"));
        var newerChecker = CreateChecker(newerHandler, clock);
        var status = await newerChecker.CheckAsync(force: true, CancellationToken.None);

        Assert.Equal("0.3.0", status.LatestVersion);
        Assert.False(status.Dismissed);
    }

    [Fact]
    public async Task DismissalSurvivesARepeatedCheckOfTheSameVersion()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson());
        var clock = new FakeClock(Base);
        var checker = CreateChecker(handler, clock);

        await checker.CheckAsync(force: true, CancellationToken.None);
        await checker.DismissAsync(CancellationToken.None);

        clock.UtcNow = Base.AddDays(2);
        var status = await checker.CheckAsync(force: false, CancellationToken.None);

        Assert.True(status.Dismissed);
    }

    // -----------------------------------------------------------------------------------
    // Checksum parsing
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  FSOps-Setup-0.2.0.exe")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 *FSOps-Setup-0.2.0.exe")]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    [InlineData("\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n")]
    public void ParseSha256_AcceptsTheFormatsRealToolingProduces(string text)
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            UpdateChecker.ParseSha256(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a hash")]
    [InlineData("abc123")]
    [InlineData("<html><body>404 Not Found</body></html>")]
    [InlineData("zzz0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void ParseSha256_RefusesAnythingItCannotReadAsAHash(string? text)
    {
        Assert.Null(UpdateChecker.ParseSha256(text));
    }

    internal static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}

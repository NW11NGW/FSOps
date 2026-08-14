using System.Net;
using System.Security.Cryptography;
using System.Text;
using FSOps.Core.Entities;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The checksum path, exercised against real bytes on a real (temporary) disk.
///
/// <para>FSOps ships unsigned. The SHA-256 published beside a release is therefore the ONLY thing
/// distinguishing "the installer the author built" from "whatever arrived over the network", and a
/// downloader that skipped or fudged that check would be building the exact supply-chain hole the
/// checksum exists to close. So these tests do not assert on a flag saying verification happened -
/// they feed the downloader bytes that genuinely do not match the published hash and then assert
/// against the filesystem that nothing was kept and nothing was named to the user.</para>
/// </summary>
public class UpdateDownloadVerificationTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private const string ReleaseApiUrl = "api.github.com";
    private const string InstallerName = "FSOps-Setup-0.2.0.exe";

    private readonly TempUpdateStorage _storage = new();

    public void Dispose() => _storage.Dispose();

    private static readonly byte[] RealInstallerBytes = Encoding.UTF8.GetBytes(
        "MZ this stands in for a 60MB Inno Setup installer, and its hash is what the release publishes.");

    private static readonly byte[] TamperedBytes = Encoding.UTF8.GetBytes(
        "MZ this is NOT the file the author built - one byte of difference is enough.");

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private const string ReleaseJson = $$"""
    {
      "tag_name": "v0.2.0",
      "draft": false,
      "prerelease": false,
      "html_url": "https://github.com/NW11NGW/FSOps/releases/tag/v0.2.0",
      "body": "Notes.",
      "published_at": "2026-08-10T09:00:00Z",
      "assets": [
        { "name": "{{InstallerName}}", "browser_download_url": "https://github.com/NW11NGW/FSOps/releases/download/v0.2.0/{{InstallerName}}", "size": 96 },
        { "name": "{{InstallerName}}.sha256", "browser_download_url": "https://github.com/NW11NGW/FSOps/releases/download/v0.2.0/{{InstallerName}}.sha256", "size": 80 }
      ]
    }
    """;

    private UpdateChecker CreateChecker(
        FakeReleaseHttpHandler handler,
        UpdateChannel channel = UpdateChannel.Stable)
    {
        var client = new GitHubReleaseClient(new FakeHttpClientFactory(handler), NullLogger<GitHubReleaseClient>.Instance);
        return new UpdateChecker(
            client,
            _storage,
            new FakeUpdateChannelStore(channel),
            new FakeClock(Base),
            NullLogger<UpdateChecker>.Instance)
        {
            CurrentVersion = "0.1.0",
        };
    }

    private string InstallerPath => Path.Combine(_storage.UpdatesDirectory, InstallerName);

    private string PartialPath => InstallerPath + ".part";

    private IReadOnlyList<string> UpdatesFolderContents() =>
        Directory.Exists(_storage.UpdatesDirectory)
            ? Directory.GetFileSystemEntries(_storage.UpdatesDirectory).Select(Path.GetFileName).ToList()!
            : Array.Empty<string>();

    // -----------------------------------------------------------------------------------
    // The channel decides WHICH build is offered. It has no say in whether it is checked.
    //
    // These two run the whole download path twice, once per channel, against the same bytes and the
    // same published hash. If a future change ever let the channel reach the verification code -
    // "pre-releases are ours anyway", "the beta feed is trusted" - one of these fails. That is the
    // entire reason they are theories over the channel rather than one test on the default.
    // -----------------------------------------------------------------------------------

    /// <summary>The pre-release equivalent of <see cref="ReleaseJson"/>, served from the list feed
    /// the development channel reads. Same installer, same sidecar name - so the only thing that
    /// differs between a stable run and a development one is which feed answered.</summary>
    private static string PrereleaseListJson(bool withChecksum = true)
    {
        const string downloadBase = "https://github.com/NW11NGW/FSOps/releases/download/v0.2.0-beta.1";
        var assets = new List<string>
        {
            $$"""{ "name": "{{InstallerName}}", "browser_download_url": "{{downloadBase}}/{{InstallerName}}", "size": 96 }""",
        };

        if (withChecksum)
        {
            assets.Add($$"""{ "name": "{{InstallerName}}.sha256", "browser_download_url": "{{downloadBase}}/{{InstallerName}}.sha256", "size": 80 }""");
        }

        return $$"""
        [{
          "tag_name": "v0.2.0-beta.1",
          "draft": false,
          "prerelease": true,
          "html_url": "https://github.com/NW11NGW/FSOps/releases/tag/v0.2.0-beta.1",
          "body": "Beta notes.",
          "published_at": "2026-08-10T09:00:00Z",
          "assets": [ {{string.Join(",", assets)}} ]
        }]
        """;
    }

    /// <summary>Stubs whichever feed the given channel reads, with a release that offers the same
    /// installer either way - so the only difference between the two runs is the channel.</summary>
    private static FakeReleaseHttpHandler HandlerForChannel(UpdateChannel channel) =>
        channel == UpdateChannel.Development
            ? new FakeReleaseHttpHandler().WhenJson("releases?per_page", PrereleaseListJson())
            : new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson);

    [Theory]
    [InlineData(UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Development)]
    public async Task AChecksumMismatch_IsRefusedIdentically_OnEveryChannel(UpdateChannel channel)
    {
        var handler = HandlerForChannel(channel)
            .WhenText(".sha256", $"{Sha256Hex(RealInstallerBytes)}  {InstallerName}")
            .WhenBytes(InstallerName, TamperedBytes);
        var checker = CreateChecker(handler, channel);

        var checkStatus = await checker.CheckAsync(force: true, CancellationToken.None);
        Assert.True(checkStatus.UpdateAvailable, "the release should have been offered on this channel");

        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Null(status.DownloadFileName);
        Assert.Null(status.DownloadSha256);
        Assert.False(File.Exists(InstallerPath));
        Assert.False(File.Exists(PartialPath));
        Assert.Empty(UpdatesFolderContents());
        Assert.Null(_storage.Load().VerifiedFilePath);
    }

    [Theory]
    [InlineData(UpdateChannel.Stable)]
    [InlineData(UpdateChannel.Development)]
    public async Task AMatchingChecksum_IsRequiredAndVerifiedIdentically_OnEveryChannel(UpdateChannel channel)
    {
        var handler = HandlerForChannel(channel)
            .WhenText(".sha256", $"{Sha256Hex(RealInstallerBytes)}  {InstallerName}")
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler, channel);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Ready, status.DownloadState);
        Assert.Equal(Sha256Hex(RealInstallerBytes), status.DownloadSha256);
        Assert.Equal(RealInstallerBytes, await File.ReadAllBytesAsync(InstallerPath));

        // The sidecar is fetched on both channels. A pre-release is not exempt from having one.
        Assert.Contains(handler.RequestedUrls, url => url.EndsWith(".sha256", StringComparison.Ordinal));
    }

    /// <summary>
    /// A pre-release that ships an installer with no checksum is refused exactly as a stable one is:
    /// announced, linked, and never fetched. "It is only a beta" is not a reason to download an
    /// unsigned executable there is nothing to verify against - if anything it is the opposite.
    /// </summary>
    [Fact]
    public async Task APrereleaseWithNoChecksum_IsAnnouncedButNeverDownloaded()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson("releases?per_page", PrereleaseListJson(withChecksum: false))
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler, UpdateChannel.Development);

        var checkStatus = await checker.CheckAsync(force: true, CancellationToken.None);
        Assert.True(checkStatus.UpdateAvailable);
        Assert.False(checkStatus.DownloadAvailable);

        var requestsBefore = handler.CallCount;
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Equal(requestsBefore, handler.CallCount);
        Assert.Empty(UpdatesFolderContents());
    }

    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task MatchingChecksum_KeepsTheFile_UnderItsRealNameWithNoPartLeftBehind()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", $"{Sha256Hex(RealInstallerBytes)}  {InstallerName}")
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Ready, status.DownloadState);
        Assert.Equal(InstallerName, status.DownloadFileName);
        Assert.Equal(Sha256Hex(RealInstallerBytes), status.DownloadSha256);
        Assert.Null(status.DownloadMessage);

        Assert.True(File.Exists(InstallerPath));
        Assert.False(File.Exists(PartialPath));
        Assert.Equal(RealInstallerBytes, await File.ReadAllBytesAsync(InstallerPath));
        Assert.Equal(new[] { InstallerName }, UpdatesFolderContents());
    }

    [Fact]
    public async Task ChecksumMismatch_DeletesTheBytes_AndOffersTheUserNothing()
    {
        // The published hash describes the real installer; the bytes served are a different file.
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", $"{Sha256Hex(RealInstallerBytes)}  {InstallerName}")
            .WhenBytes(InstallerName, TamperedBytes);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var status = await checker.DownloadAsync(CancellationToken.None);

        // Nothing is offered.
        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Null(status.DownloadFileName);
        Assert.Null(status.DownloadSha256);

        // Nothing survives on disk, under either name.
        Assert.False(File.Exists(InstallerPath));
        Assert.False(File.Exists(PartialPath));
        Assert.Empty(UpdatesFolderContents());

        // And nothing was recorded that a later call could resurrect.
        var state = _storage.Load();
        Assert.Null(state.VerifiedFilePath);
        Assert.Null(state.VerifiedSha256);
        Assert.Null(state.VerifiedVersion);
    }

    [Fact]
    public async Task ASingleFlippedByte_IsEnoughToRejectTheDownload()
    {
        var almost = RealInstallerBytes.ToArray();
        almost[^1] ^= 0x01;

        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenBytes(InstallerName, almost);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Empty(UpdatesFolderContents());
    }

    [Fact]
    public async Task ATruncatedDownload_FailsVerification_RatherThanBeingKept()
    {
        // A connection that dies half way through produces a real, shorter file. Hashing what
        // actually landed on disk is what catches it.
        var truncated = RealInstallerBytes.Take(RealInstallerBytes.Length / 2).ToArray();

        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenBytes(InstallerName, truncated);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Empty(UpdatesFolderContents());
    }

    [Fact]
    public async Task AChecksumFileThatIsAnErrorPage_StopsTheDownloadBeforeItStarts()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", "<!DOCTYPE html><title>Not Found</title>")
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var requestsBefore = handler.CallCount;
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Empty(UpdatesFolderContents());

        // The checksum is fetched first precisely so the installer is never pulled down when there
        // is nothing to check it against: exactly one extra request was made, and it was not the exe.
        Assert.Equal(requestsBefore + 1, handler.CallCount);
        Assert.DoesNotContain(handler.RequestedUrls.Skip(requestsBefore), url => url.EndsWith(InstallerName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AChecksumRequestThatFails_StopsTheDownloadBeforeItStarts()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenStatus(".sha256", HttpStatusCode.NotFound)
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var requestsBefore = handler.CallCount;
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Empty(UpdatesFolderContents());
        Assert.Equal(requestsBefore + 1, handler.CallCount);
    }

    [Fact]
    public async Task AnInstallerDownloadThatFails_LeavesNoPartialFileBehind()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenStatus(InstallerName, HttpStatusCode.ServiceUnavailable);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        var status = await checker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.Empty(UpdatesFolderContents());
    }

    [Fact]
    public async Task AFailedDownloadAfterASuccessfulOne_DoesNotLeaveTheOldFileLookingLikeTheNewOne()
    {
        // First, a good download.
        var goodHandler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(goodHandler);
        await checker.CheckAsync(force: true, CancellationToken.None);
        Assert.Equal(UpdateDownloadStates.Ready, (await checker.DownloadAsync(CancellationToken.None)).DownloadState);

        // Then a re-download that gets tampered bytes. The already-verified file must not be
        // silently replaced by, or confused with, the bad one.
        var badHandler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenBytes(InstallerName, TamperedBytes);
        var badChecker = CreateChecker(badHandler);
        var status = await badChecker.DownloadAsync(CancellationToken.None);

        Assert.Equal(UpdateDownloadStates.Failed, status.DownloadState);
        Assert.False(File.Exists(PartialPath));

        // Whatever is left must still be the verified bytes, never the tampered ones.
        if (File.Exists(InstallerPath))
        {
            Assert.Equal(RealInstallerBytes, await File.ReadAllBytesAsync(InstallerPath));
        }
    }

    [Fact]
    public async Task RevealingAFileThatWasSwappedAfterVerification_DeletesItInsteadOfShowingIt()
    {
        // The window between "verified" and "the user clicks" is real: the file sits on disk for as
        // long as the user takes. "Verified an hour ago" is not the same claim as "these bytes are
        // correct now", so the hash is checked again at the moment it matters.
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        await checker.DownloadAsync(CancellationToken.None);
        Assert.True(File.Exists(InstallerPath));

        await File.WriteAllBytesAsync(InstallerPath, TamperedBytes);

        var reveal = await checker.RevealAsync(CancellationToken.None);

        Assert.False(reveal.Success);
        Assert.False(File.Exists(InstallerPath));
        Assert.Null(_storage.Load().VerifiedFilePath);
    }

    [Fact]
    public async Task RevealingWhenTheFileHasSimplyGone_ClearsTheRecordRatherThanFailingOpaquely()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        await checker.DownloadAsync(CancellationToken.None);
        File.Delete(InstallerPath);

        var reveal = await checker.RevealAsync(CancellationToken.None);

        Assert.False(reveal.Success);
        Assert.NotNull(reveal.Message);
        Assert.Null(_storage.Load().VerifiedFilePath);
        Assert.NotEqual(UpdateDownloadStates.Ready, (await checker.GetStatusAsync(CancellationToken.None)).DownloadState);
    }

    [Fact]
    public async Task AVerifiedDownloadForAnOlderVersion_IsDiscardedWhenANewerReleaseArrives()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        await checker.DownloadAsync(CancellationToken.None);
        Assert.True(File.Exists(InstallerPath));

        var newerJson = ReleaseJson
            .Replace("v0.2.0", "v0.3.0")
            .Replace("FSOps-Setup-0.2.0", "FSOps-Setup-0.3.0");
        var newerHandler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, newerJson);
        var newerChecker = CreateChecker(newerHandler);

        var status = await newerChecker.CheckAsync(force: true, CancellationToken.None);

        Assert.Equal("0.3.0", status.LatestVersion);
        Assert.NotEqual(UpdateDownloadStates.Ready, status.DownloadState);
        Assert.Null(status.DownloadFileName);
        Assert.False(File.Exists(InstallerPath));
    }

    [Fact]
    public async Task TheDownloadNeverWritesOutsideTheUpdatesDirectory()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson)
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenBytes(InstallerName, RealInstallerBytes);
        var checker = CreateChecker(handler);

        await checker.CheckAsync(force: true, CancellationToken.None);
        await checker.DownloadAsync(CancellationToken.None);

        var verifiedPath = _storage.Load().VerifiedFilePath;
        Assert.NotNull(verifiedPath);
        Assert.StartsWith(
            Path.GetFullPath(_storage.UpdatesDirectory),
            Path.GetFullPath(verifiedPath!),
            StringComparison.OrdinalIgnoreCase);
    }
}

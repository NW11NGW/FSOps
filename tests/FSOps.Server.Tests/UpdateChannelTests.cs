using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Which builds each channel will and will not offer.
///
/// <para>Two properties here are worth more than the rest put together. The first is that
/// <b>stable is the default with nothing stored</b> - not "development until something writes the
/// key", which is the way a default like this usually fails, silently and only for people who never
/// touched the setting. The second is that <b>a build newer than the channel is reported as such</b>
/// rather than answered with an older release, because presenting a downgrade as an update is how an
/// updater talks somebody into overwriting a newer build with an older one.</para>
///
/// <para>No test here touches the network. GitHub is a stub, as everywhere else in this suite.</para>
/// </summary>
public class UpdateChannelTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private const string ReleaseApiUrl = "api.github.com";

    /// <summary>Matches the list feed only. Registered BEFORE the single-release route wherever both
    /// are stubbed, because the handler takes the first route whose fragment matches and
    /// "api.github.com" would otherwise swallow this one.</summary>
    private const string ReleaseListUrl = "releases?per_page";

    private readonly TempUpdateStorage _storage = new();

    public void Dispose() => _storage.Dispose();

    private UpdateChecker CreateChecker(
        FakeReleaseHttpHandler handler,
        UpdateChannel channel,
        string currentVersion = "0.1.0",
        FakeClock? clock = null)
    {
        var client = new GitHubReleaseClient(new FakeHttpClientFactory(handler), NullLogger<GitHubReleaseClient>.Instance);
        return new UpdateChecker(
            client,
            _storage,
            new FakeUpdateChannelStore(channel),
            clock ?? new FakeClock(Base),
            NullLogger<UpdateChecker>.Instance)
        {
            CurrentVersion = currentVersion,
        };
    }

    private static string ReleaseJson(
        string tag,
        bool prerelease = false,
        bool draft = false,
        bool withInstaller = true,
        bool withChecksum = true)
    {
        var installerName = $"FSOps-Setup-{tag.TrimStart('v')}.exe";
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
          "body": "Notes for {{tag}}.",
          "published_at": "2026-08-10T09:00:00Z",
          "assets": [ {{string.Join(",", assets)}} ]
        }
        """;
    }

    private static string ReleaseListJson(params string[] releases) => "[" + string.Join(",", releases) + "]";

    // -----------------------------------------------------------------------------------
    // The default. The one most likely to regress without anyone noticing.
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// A brand-new install: the database has been migrated and nothing else has happened. No settings
    /// row exists, because one is only created when something writes a setting. That has to read as
    /// Stable - and it has to read as Stable from the REAL store against a REAL database, not from a
    /// fake whose default a test author chose.
    /// </summary>
    [Fact]
    public async Task WithNothingStoredAtAll_TheChannelIsStable()
    {
        await using var harness = await DatabaseChannelHarness.CreateAsync();

        Assert.Empty(await harness.Db.UserSettings.ToListAsync());
        Assert.Equal(UpdateChannel.Stable, await harness.Store.GetAsync(CancellationToken.None));
    }

    /// <summary>
    /// Asking must not answer by writing. If reading the channel created a settings row, the default
    /// would stop being a default the moment anything looked at it - and "the user chose stable" and
    /// "the user chose nothing" would become indistinguishable in the data.
    /// </summary>
    [Fact]
    public async Task ReadingTheChannel_DoesNotCreateASettingsRow()
    {
        await using var harness = await DatabaseChannelHarness.CreateAsync();

        await harness.Store.GetAsync(CancellationToken.None);
        await harness.Store.GetAsync(CancellationToken.None);

        Assert.Empty(await harness.Db.UserSettings.ToListAsync());
    }

    /// <summary>
    /// A settings row that exists for other reasons - somebody set their currency - is still on
    /// stable. This is the case a "defaults to development until the key is written" bug would sail
    /// straight through, because a row exists and only the one column is untouched.
    /// </summary>
    [Fact]
    public async Task ASettingsRowWrittenForSomeOtherReason_IsStillOnStable()
    {
        await using var harness = await DatabaseChannelHarness.CreateAsync();

        harness.Db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            OwnerUserId = LocalUserId,
            CurrencyCode = "JPY",
        });
        await harness.Db.SaveChangesAsync();

        Assert.Equal(UpdateChannel.Stable, await harness.Store.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AChosenChannel_IsStoredAndReadBack()
    {
        await using var harness = await DatabaseChannelHarness.CreateAsync();

        await harness.Store.SetAsync(UpdateChannel.Development, CancellationToken.None);
        Assert.Equal(UpdateChannel.Development, await harness.Store.GetAsync(CancellationToken.None));

        await harness.Store.SetAsync(UpdateChannel.Stable, CancellationToken.None);
        Assert.Equal(UpdateChannel.Stable, await harness.Store.GetAsync(CancellationToken.None));
    }

    /// <summary>
    /// A database that cannot be opened must mean Stable, not "whatever the enum's zero value happens
    /// to be by luck" and certainly not a failure. Being wrong towards stable costs an update nobody
    /// gets offered; being wrong the other way hands somebody an untested build on the strength of a
    /// failed read.
    /// </summary>
    [Fact]
    public async Task AnUnreadableDatabase_ResolvesToStable()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentUser, LocalUser>();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite("Data Source=/this/path/does/not/exist/nope.db"));
        await using var provider = services.BuildServiceProvider();

        var store = new DatabaseUpdateChannelStore(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseUpdateChannelStore>.Instance);

        Assert.Equal(UpdateChannel.Stable, await store.GetAsync(CancellationToken.None));
    }

    // -----------------------------------------------------------------------------------
    // Stable: a pre-release must never reach it
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task StableChannel_ReadsTheLatestFeed_AndNeverTheReleaseList()
    {
        // The stable channel's guarantee is enforced by GitHub before this code sees anything:
        // /releases/latest cannot return a pre-release. Asking the list feed instead would move that
        // guarantee into FSOps' own filtering, which is a weaker place for it to live.
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson("v0.2.0"));
        var checker = CreateChecker(handler, UpdateChannel.Stable);

        await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.Contains(handler.RequestedUrls, url => url.EndsWith("/releases/latest", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestedUrls, url => url.Contains("per_page", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("v0.2.0", true)]
    [InlineData("v0.2.0-beta.1", false)]
    [InlineData("v0.2.0-beta.1", true)]
    public async Task StableChannel_NeverOffersAPrerelease_ByFlagOrByTag(string tag, bool prereleaseFlag)
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseApiUrl, ReleaseJson(tag, prerelease: prereleaseFlag));
        var checker = CreateChecker(handler, UpdateChannel.Stable);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
        Assert.False(status.DownloadAvailable);
        Assert.Equal("stable", status.Channel);
    }

    [Fact]
    public async Task StableChannel_StillOffersAnOrdinaryRelease()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson("v0.2.0"));
        var checker = CreateChecker(handler, UpdateChannel.Stable);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.2.0", status.LatestVersion);
        Assert.True(status.DownloadAvailable);
        Assert.False(status.AheadOfChannel);
    }

    // -----------------------------------------------------------------------------------
    // Development: pre-releases, and the newest of everything
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task DevelopmentChannel_OffersAPrerelease_WithItsInstallerAndChecksum()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseListUrl, ReleaseListJson(ReleaseJson("v0.2.0-beta.1", prerelease: true)));
        var checker = CreateChecker(handler, UpdateChannel.Development);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.2.0-beta.1", status.LatestVersion);
        Assert.True(status.DownloadAvailable);
        Assert.Equal("development", status.Channel);
    }

    [Fact]
    public async Task DevelopmentChannel_PicksTheHighestVersion_NotWhicheverWasPublishedLast()
    {
        // GitHub returns releases newest-published first, and publication order is not version order:
        // a patch to an older line published after a beta would win on position alone.
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseListUrl, ReleaseListJson(
            ReleaseJson("v0.1.5"),
            ReleaseJson("v0.3.0-beta.2", prerelease: true),
            ReleaseJson("v0.3.0-beta.1", prerelease: true),
            ReleaseJson("v0.2.0")));
        var checker = CreateChecker(handler, UpdateChannel.Development);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.Equal("0.3.0-beta.2", status.LatestVersion);
    }

    [Fact]
    public async Task DevelopmentChannel_TakesANewerStableReleaseOverAnOlderBeta()
    {
        // Opting in to development means seeing more, never being pinned to something older. A user
        // on the development channel when the stable release finally lands must be moved onto it.
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseListUrl, ReleaseListJson(
            ReleaseJson("v0.3.0"),
            ReleaseJson("v0.3.0-beta.4", prerelease: true)));
        var checker = CreateChecker(handler, UpdateChannel.Development);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.Equal("0.3.0", status.LatestVersion);
    }

    [Fact]
    public async Task DevelopmentChannel_StillRefusesADraft()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseListUrl, ReleaseListJson(
            ReleaseJson("v0.9.0", draft: true),
            ReleaseJson("v0.2.0-beta.1", prerelease: true)));
        var checker = CreateChecker(handler, UpdateChannel.Development);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.Equal("0.2.0-beta.1", status.LatestVersion);
    }

    [Fact]
    public async Task DevelopmentChannel_IgnoresATagThatIsNotAVersion()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseListUrl, ReleaseListJson(
            ReleaseJson("nightly"),
            ReleaseJson("v0.2.0-beta.1", prerelease: true)));
        var checker = CreateChecker(handler, UpdateChannel.Development);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.Equal("0.2.0-beta.1", status.LatestVersion);
    }

    [Fact]
    public async Task DevelopmentChannel_WithAnEmptyReleaseList_OffersNothing_AndIsNotAFailure()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseListUrl, "[]");
        var checker = CreateChecker(handler, UpdateChannel.Development);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.False(status.LastCheckFailed);
        Assert.False(status.AheadOfChannel);
    }

    [Fact]
    public async Task DevelopmentChannel_WhenTheFeedIsUnreachable_LooksLikeNoUpdate()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenThrows(ReleaseListUrl, new HttpRequestException("No such host is known."));
        var checker = CreateChecker(handler, UpdateChannel.Development);

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.True(status.LastCheckFailed);
    }

    // -----------------------------------------------------------------------------------
    // Ahead of the channel: never a downgrade, never a lie
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The case requirement 3 exists for. Running 0.3.0-beta.2, switched back to stable, where the
    /// newest stable release is 0.2.0. There is no update - and there must be no offer of 0.2.0
    /// either, which would be an older build presented as a newer one.
    /// </summary>
    [Fact]
    public async Task RunningNewerThanTheChannel_IsReportedAsAhead_AndOffersNoDowngrade()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson("v0.2.0"));
        var checker = CreateChecker(handler, UpdateChannel.Stable, currentVersion: "0.3.0-beta.2");

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.AheadOfChannel);
        Assert.Equal("0.2.0", status.ChannelNewestVersion);

        // Nothing offered, nothing downloadable, and above all nothing named as an update.
        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
        Assert.False(status.DownloadAvailable);
    }

    [Fact]
    public async Task RunningExactlyTheChannelsNewestVersion_IsUpToDate_NotAhead()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson("v0.2.0"));
        var checker = CreateChecker(handler, UpdateChannel.Stable, currentVersion: "0.2.0");

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.False(status.UpdateAvailable);
        Assert.False(status.AheadOfChannel);
        Assert.Equal("0.2.0", status.ChannelNewestVersion);
    }

    /// <summary>
    /// And the other side of it: once stable overtakes the beta the user is running, the update is
    /// offered normally and "ahead" goes away on its own. Nobody has to do anything to get unstuck.
    /// </summary>
    [Fact]
    public async Task OnceStableOvertakesTheBeta_TheUpdateIsOfferedAndAheadClearsItself()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson("v0.3.0"));
        var checker = CreateChecker(handler, UpdateChannel.Stable, currentVersion: "0.3.0-beta.2");

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.UpdateAvailable);
        Assert.Equal("0.3.0", status.LatestVersion);
        Assert.False(status.AheadOfChannel);
    }

    [Fact]
    public async Task ADevelopmentUserRunningAheadOfEveryPublishedBeta_IsAlsoToldSo()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseListUrl, ReleaseListJson(ReleaseJson("v0.3.0-beta.1", prerelease: true)));
        var checker = CreateChecker(handler, UpdateChannel.Development, currentVersion: "0.3.0-beta.5");

        var status = await checker.CheckAsync(force: true, CancellationToken.None);

        Assert.True(status.AheadOfChannel);
        Assert.Equal("0.3.0-beta.1", status.ChannelNewestVersion);
        Assert.False(status.UpdateAvailable);
    }

    // -----------------------------------------------------------------------------------
    // Switching channels
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// Switching away from development must not leave the pre-release still on screen. The cached
    /// answer belonged to a question the user has stopped asking, and a verified installer downloaded
    /// for it is not a download they still want sitting in their data directory.
    /// </summary>
    [Fact]
    public async Task SwitchingToStable_DiscardsTheDevelopmentOfferAndItsDownload()
    {
        var installer = new byte[] { 4, 8, 15, 16 };
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseListUrl, ReleaseListJson(ReleaseJson("v0.3.0-beta.1", prerelease: true)))
            .WhenText(".sha256", Sha256Hex(installer))
            .WhenBytes("FSOps-Setup-0.3.0-beta.1.exe", installer)
            .WhenJson(ReleaseApiUrl, ReleaseJson("v0.2.0"));

        var channels = new FakeUpdateChannelStore(UpdateChannel.Development);
        var client = new GitHubReleaseClient(new FakeHttpClientFactory(handler), NullLogger<GitHubReleaseClient>.Instance);
        var checker = new UpdateChecker(client, _storage, channels, new FakeClock(Base), NullLogger<UpdateChecker>.Instance)
        {
            CurrentVersion = "0.1.0",
        };

        await checker.CheckAsync(force: true, CancellationToken.None);
        var downloaded = await checker.DownloadAsync(CancellationToken.None);
        Assert.Equal(UpdateDownloadStates.Ready, downloaded.DownloadState);

        var installerPath = Path.Combine(_storage.UpdatesDirectory, "FSOps-Setup-0.3.0-beta.1.exe");
        Assert.True(File.Exists(installerPath));

        var status = await checker.SetChannelAsync(UpdateChannel.Stable, CancellationToken.None);

        Assert.Equal("stable", status.Channel);
        Assert.Equal("0.2.0", status.LatestVersion);
        Assert.Equal(UpdateChannel.Stable, channels.Channel);

        // The beta's installer is gone, and nothing claims to be ready.
        Assert.False(File.Exists(installerPath));
        Assert.NotEqual(UpdateDownloadStates.Ready, status.DownloadState);
        Assert.Null(status.DownloadFileName);
    }

    [Fact]
    public async Task SwitchingChannels_ReChecksImmediatelyRatherThanServingTheCachedAnswer()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseListUrl, ReleaseListJson(ReleaseJson("v0.4.0-beta.1", prerelease: true)))
            .WhenJson(ReleaseApiUrl, ReleaseJson("v0.2.0"));

        var channels = new FakeUpdateChannelStore(UpdateChannel.Stable);
        var client = new GitHubReleaseClient(new FakeHttpClientFactory(handler), NullLogger<GitHubReleaseClient>.Instance);
        var checker = new UpdateChecker(client, _storage, channels, new FakeClock(Base), NullLogger<UpdateChecker>.Instance)
        {
            CurrentVersion = "0.1.0",
        };

        var stable = await checker.CheckAsync(force: true, CancellationToken.None);
        Assert.Equal("0.2.0", stable.LatestVersion);

        // Inside the 24-hour cache window, so only the switch itself can be what causes a new lookup.
        var development = await checker.SetChannelAsync(UpdateChannel.Development, CancellationToken.None);

        Assert.Equal("0.4.0-beta.1", development.LatestVersion);
        Assert.Equal("development", development.Channel);
    }

    /// <summary>
    /// Choosing a channel is not consent to start making requests. With checks switched off, the
    /// switch stores the preference and stops there.
    /// </summary>
    [Fact]
    public async Task SwitchingChannelsWithChecksTurnedOff_StoresThePreferenceAndSendsNothing()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseListUrl, ReleaseListJson(ReleaseJson("v0.4.0-beta.1", prerelease: true)));
        var channels = new FakeUpdateChannelStore(UpdateChannel.Stable);
        var client = new GitHubReleaseClient(new FakeHttpClientFactory(handler), NullLogger<GitHubReleaseClient>.Instance);
        var checker = new UpdateChecker(client, _storage, channels, new FakeClock(Base), NullLogger<UpdateChecker>.Instance)
        {
            CurrentVersion = "0.1.0",
        };

        await checker.SetEnabledAsync(false, CancellationToken.None);
        var status = await checker.SetChannelAsync(UpdateChannel.Development, CancellationToken.None);

        Assert.Empty(handler.RequestedUrls);
        Assert.Equal(UpdateChannel.Development, channels.Channel);
        Assert.Equal("development", status.Channel);
        Assert.False(status.UpdateAvailable);
    }

    /// <summary>
    /// A cached result from another channel is not an answer to this channel's question. Reaching
    /// this means a state file written before channels existed, or one edited underneath the app -
    /// either way the cache window must not be allowed to keep a stale channel's answer alive.
    /// </summary>
    [Fact]
    public async Task ACachedResultFromTheOtherChannel_ForcesAFreshCheckEvenInsideTheCacheWindow()
    {
        var clock = new FakeClock(Base);
        var stableHandler = new FakeReleaseHttpHandler().WhenJson(ReleaseApiUrl, ReleaseJson("v0.2.0"));
        var stableChecker = CreateChecker(stableHandler, UpdateChannel.Stable, clock: clock);
        await stableChecker.CheckAsync(force: true, CancellationToken.None);
        Assert.Equal("0.2.0", _storage.Load().LatestVersion);

        // Same storage, same clock, different channel - and NOT forced.
        var devHandler = new FakeReleaseHttpHandler()
            .WhenJson(ReleaseListUrl, ReleaseListJson(ReleaseJson("v0.4.0-beta.1", prerelease: true)));
        var devChecker = CreateChecker(devHandler, UpdateChannel.Development, clock: clock);

        var status = await devChecker.CheckAsync(force: false, CancellationToken.None);

        Assert.Equal(1, devHandler.CallCount);
        Assert.Equal("0.4.0-beta.1", status.LatestVersion);
    }

    // -----------------------------------------------------------------------------------
    // The channel's name on the wire
    // -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("stable", UpdateChannel.Stable)]
    [InlineData("Stable", UpdateChannel.Stable)]
    [InlineData("development", UpdateChannel.Development)]
    [InlineData("DEVELOPMENT", UpdateChannel.Development)]
    [InlineData("  development  ", UpdateChannel.Development)]
    public void TryParseChannel_AcceptsTheNamesTheApiDocuments(string value, UpdateChannel expected)
    {
        Assert.True(UpdateChecker.TryParseChannel(value, out var channel));
        Assert.Equal(expected, channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("beta")]
    [InlineData("nightly")]
    [InlineData("1")]
    public void TryParseChannel_RefusesAnythingElseRatherThanGuessing(string? value)
    {
        // "1" matters: Enum.TryParse happily accepts numeric strings, so a client sending an index
        // would otherwise silently select Development.
        Assert.False(UpdateChecker.TryParseChannel(value, out _));
    }

    [Fact]
    public void ChannelName_IsTheLowerCaseNameTheApiPromises()
    {
        Assert.Equal("stable", UpdateChecker.ChannelName(UpdateChannel.Stable));
        Assert.Equal("development", UpdateChecker.ChannelName(UpdateChannel.Development));
    }

    // -----------------------------------------------------------------------------------

    private static readonly Guid LocalUserId = new LocalUser().UserId;

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A migrated, file-backed database and the real store over it - so the default is
    /// proven where it actually has to hold rather than against a fake.</summary>
    private sealed class DatabaseChannelHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        private readonly string _directory;

        private DatabaseChannelHarness(ServiceProvider provider, IServiceScope scope, string directory)
        {
            _provider = provider;
            _scope = scope;
            _directory = directory;
            Db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
            Store = new DatabaseUpdateChannelStore(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<DatabaseUpdateChannelStore>.Instance);
        }

        public FsOpsDbContext Db { get; }

        public DatabaseUpdateChannelStore Store { get; }

        public static async Task<DatabaseChannelHarness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "fsops-channel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "fsops.db"),
            }.ToString();

            var services = new ServiceCollection();
            services.AddScoped<ICurrentUser, LocalUser>();
            services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(connectionString));
            var provider = services.BuildServiceProvider();

            var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<FsOpsDbContext>().Database.MigrateAsync();

            return new DatabaseChannelHarness(provider, scope, directory);
        }

        public async ValueTask DisposeAsync()
        {
            _scope.Dispose();
            await _provider.DisposeAsync();

            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a passing test over.
            }
        }
    }
}

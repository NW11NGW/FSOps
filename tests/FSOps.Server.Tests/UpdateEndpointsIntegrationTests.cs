using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSOps.Core.Time;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FSOps.Server.Tests;

/// <summary>
/// The update endpoints over real HTTP, against a real Kestrel, with a fake GitHub behind them.
///
/// <para>These exist because everything interesting about this feature is a failure path, and a
/// failure path that has only ever been unit-tested is a failure path nobody has actually watched
/// happen. So each one is driven end to end - request in, JSON out - and asserted on the response
/// body rather than the status code, which for this feature is 200 almost regardless of what went
/// wrong (that is the design: a broken network must be indistinguishable from "up to date").</para>
///
/// <para>The host registers exactly what Program.cs registers, so a divergence between the two
/// shows up here rather than in the shipped app. GitHub is never contacted; the transport is a
/// stub, which is also the only way to drive a checksum mismatch on demand.</para>
/// </summary>
public class UpdateEndpointsIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private const string InstallerName = "FSOps-Setup-0.2.0.exe";

    private static readonly byte[] RealInstallerBytes =
        Encoding.UTF8.GetBytes("MZ the real installer, whose hash is what the release publishes.");

    private static readonly byte[] TamperedBytes =
        Encoding.UTF8.GetBytes("MZ something else entirely.");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TempUpdateStorage _storage = new();
    private WebApplication? _app;
    private HttpClient? _client;
    private FakeReleaseHttpHandler _handler = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _storage.Dispose();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReleaseJson(
        string tag = "v0.2.0",
        bool draft = false,
        bool prerelease = false,
        bool withChecksum = true) => $$"""
    {
      "tag_name": "{{tag}}",
      "draft": {{(draft ? "true" : "false")}},
      "prerelease": {{(prerelease ? "true" : "false")}},
      "html_url": "https://github.com/NW11NGW/FSOps/releases/tag/{{tag}}",
      "body": "What changed.",
      "published_at": "2026-08-10T09:00:00Z",
      "assets": [
        { "name": "{{InstallerName}}", "browser_download_url": "https://github.com/NW11NGW/FSOps/releases/download/{{tag}}/{{InstallerName}}", "size": 64 }
        {{(withChecksum ? $$""", { "name": "{{InstallerName}}.sha256", "browser_download_url": "https://github.com/NW11NGW/FSOps/releases/download/{{tag}}/{{InstallerName}}.sha256", "size": 80 }""" : "")}}
      ]
    }
    """;

    /// <summary>
    /// Boots a real server on an OS-assigned port with the same registrations Program.cs uses. Port
    /// 0 rather than a fixed number on purpose: another copy of FSOps is usually running on this
    /// machine while these tests do, and a test has no business competing for a port it did not
    /// open.
    /// </summary>
    private async Task<HttpClient> StartAsync(FakeReleaseHttpHandler handler, string currentVersion = "0.1.0")
    {
        _handler = handler;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IClock>(new FakeClock(Base));
        builder.Services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));
        builder.Services.AddSingleton<IUpdateStorage>(_storage);
        builder.Services.AddSingleton<IGitHubReleaseClient, GitHubReleaseClient>();
        builder.Services.AddSingleton(sp => new UpdateChecker(
            sp.GetRequiredService<IGitHubReleaseClient>(),
            sp.GetRequiredService<IUpdateStorage>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<UpdateChecker>>())
        {
            CurrentVersion = currentVersion,
        });

        _app = builder.Build();
        _app.MapGroup("/api/v1").MapUpdateEndpoints();
        await _app.StartAsync();

        var address = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        _client = new HttpClient { BaseAddress = new Uri(address) };
        return _client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
    }

    private async Task<JsonElement> GetStatusAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/update/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    /// <summary>Polls the status until the background download settles, so tests assert on the
    /// finished state rather than on a race.</summary>
    private async Task<JsonElement> WaitForDownloadToSettleAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var status = await GetStatusAsync(client);
            var state = status.GetProperty("downloadState").GetString();
            if (state != "downloading")
            {
                return status;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The download never left the 'downloading' state.");
    }

    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task Status_OnAFreshInstall_Answers200WithNoUpdateAndTheRunningVersion()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler().WhenStatus("api.github.com", HttpStatusCode.NotFound));

        var status = await GetStatusAsync(client);

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("0.1.0", status.GetProperty("currentVersion").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("latestVersion").ValueKind);
        Assert.Equal("none", status.GetProperty("downloadState").GetString());
    }

    [Fact]
    public async Task Status_ReturnsImmediately_EvenWhenTheFeedNeverAnswers()
    {
        // The handler hangs forever. If /update/status waited on the check, this request would too,
        // and the SPA would hang on every page load for anyone behind a captive portal.
        var client = await StartAsync(new FakeReleaseHttpHandler().WhenHangs("api.github.com"));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var status = await GetStatusAsync(client);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"status took {stopwatch.Elapsed}");
        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Check_WithAnUnreachableFeed_Is200WithNoUpdate_NotAnError()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler()
            .WhenThrows("api.github.com", new HttpRequestException("No such host is known.")));

        var response = await client.PostAsync("/api/v1/update/check", null);
        var status = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
        Assert.True(status.GetProperty("lastCheckFailed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("downloadMessage").ValueKind);

        // A finished check must not still report itself as running - otherwise the client polls for
        // something that has already happened.
        Assert.False(status.GetProperty("checking").GetBoolean());
    }

    [Fact]
    public async Task Check_WithARateLimitedFeed_Is200WithNoUpdate()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler().WhenRateLimited("api.github.com"));

        var response = await client.PostAsync("/api/v1/update/check", null);
        var status = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
        Assert.True(status.GetProperty("lastCheckFailed").GetBoolean());
    }

    [Fact]
    public async Task Check_WithANewerRelease_ReportsItWithItsNotesAndReleasePage()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler().WhenJson("api.github.com", ReleaseJson()));

        var response = await client.PostAsync("/api/v1/update/check", null);
        var status = await ReadJsonAsync(response);

        Assert.True(status.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("0.2.0", status.GetProperty("latestVersion").GetString());
        Assert.Equal("What changed.", status.GetProperty("releaseNotes").GetString());
        Assert.Equal("https://github.com/NW11NGW/FSOps/releases/tag/v0.2.0", status.GetProperty("releaseUrl").GetString());
        Assert.True(status.GetProperty("downloadAvailable").GetBoolean());
        Assert.False(status.GetProperty("checking").GetBoolean());
    }

    [Fact]
    public async Task Check_WithADraftRelease_OffersNothing()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler().WhenJson("api.github.com", ReleaseJson(draft: true)));

        var status = await ReadJsonAsync(await client.PostAsync("/api/v1/update/check", null));

        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
        Assert.False(status.GetProperty("downloadAvailable").GetBoolean());
    }

    [Fact]
    public async Task Check_WithAPrerelease_OffersNothing()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler().WhenJson("api.github.com", ReleaseJson(prerelease: true)));

        var status = await ReadJsonAsync(await client.PostAsync("/api/v1/update/check", null));

        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Check_WhenTheReleaseIsTheVersionAlreadyRunning_OffersNothing()
    {
        var client = await StartAsync(
            new FakeReleaseHttpHandler().WhenJson("api.github.com", ReleaseJson(tag: "v0.2.0")),
            currentVersion: "0.2.0");

        var status = await ReadJsonAsync(await client.PostAsync("/api/v1/update/check", null));

        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Check_WhenTheReleaseIsOlderThanTheVersionRunning_OffersNothing()
    {
        var client = await StartAsync(
            new FakeReleaseHttpHandler().WhenJson("api.github.com", ReleaseJson(tag: "v0.2.0")),
            currentVersion: "0.10.0");

        var status = await ReadJsonAsync(await client.PostAsync("/api/v1/update/check", null));

        Assert.False(status.GetProperty("updateAvailable").GetBoolean());
    }

    [Fact]
    public async Task Preferences_TurnedOff_MakesNoOutboundRequestForAnythingAfterwards()
    {
        var handler = new FakeReleaseHttpHandler().WhenJson("api.github.com", ReleaseJson());
        var client = await StartAsync(handler);

        var off = await client.PutAsJsonAsync("/api/v1/update/preferences", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await ReadJsonAsync(off)).GetProperty("enabled").GetBoolean());

        await client.GetAsync("/api/v1/update/status");
        await client.PostAsync("/api/v1/update/check", null);
        await client.PostAsync("/api/v1/update/download", null);

        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task Preferences_SurviveARestartOfTheServer()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler());
        await client.PutAsJsonAsync("/api/v1/update/preferences", new { enabled = false });

        // Same storage, new host - which is what a restart looks like from the state file's side.
        await _app!.StopAsync();
        await _app.DisposeAsync();
        _client!.Dispose();
        _app = null;

        var restarted = await StartAsync(new FakeReleaseHttpHandler());
        var status = await GetStatusAsync(restarted);

        Assert.False(status.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Dismiss_SilencesTheNoticeWithoutHidingTheUpdateFromSettings()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler().WhenJson("api.github.com", ReleaseJson()));
        await client.PostAsync("/api/v1/update/check", null);

        var status = await ReadJsonAsync(await client.PostAsync("/api/v1/update/dismiss", null));

        Assert.True(status.GetProperty("dismissed").GetBoolean());
        Assert.True(status.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("0.2.0", status.GetProperty("latestVersion").GetString());
    }

    // -----------------------------------------------------------------------------------
    // Download, over the wire
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task Download_WithAMatchingChecksum_EndsReadyAndNamesTheVerifiedFile()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler()
            .WhenJson("api.github.com", ReleaseJson())
            .WhenText(".sha256", $"{Sha256Hex(RealInstallerBytes)}  {InstallerName}")
            .WhenBytes(InstallerName, RealInstallerBytes));

        await client.PostAsync("/api/v1/update/check", null);
        await client.PostAsync("/api/v1/update/download", null);
        var status = await WaitForDownloadToSettleAsync(client);

        Assert.Equal("ready", status.GetProperty("downloadState").GetString());
        Assert.Equal(InstallerName, status.GetProperty("downloadFileName").GetString());
        Assert.Equal(Sha256Hex(RealInstallerBytes), status.GetProperty("downloadSha256").GetString());
        Assert.True(File.Exists(Path.Combine(_storage.UpdatesDirectory, InstallerName)));
    }

    [Fact]
    public async Task Download_WithAChecksumThatDoesNotMatch_EndsFailedAndLeavesNothingOnDisk()
    {
        // The one that matters. The published hash describes the real installer; the bytes served
        // are a different file. Nothing may survive this, and nothing may be named to the user.
        var client = await StartAsync(new FakeReleaseHttpHandler()
            .WhenJson("api.github.com", ReleaseJson())
            .WhenText(".sha256", $"{Sha256Hex(RealInstallerBytes)}  {InstallerName}")
            .WhenBytes(InstallerName, TamperedBytes));

        await client.PostAsync("/api/v1/update/check", null);
        await client.PostAsync("/api/v1/update/download", null);
        var status = await WaitForDownloadToSettleAsync(client);

        Assert.Equal("failed", status.GetProperty("downloadState").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("downloadFileName").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("downloadSha256").ValueKind);
        Assert.Contains("checksum", status.GetProperty("downloadMessage").GetString(), StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(Path.Combine(_storage.UpdatesDirectory, InstallerName)));
        Assert.False(File.Exists(Path.Combine(_storage.UpdatesDirectory, InstallerName + ".part")));

        // And reveal must refuse, since there is nothing verified to reveal.
        var reveal = await client.PostAsync("/api/v1/update/reveal", null);
        Assert.Equal(HttpStatusCode.BadRequest, reveal.StatusCode);
    }

    [Fact]
    public async Task Download_WhenTheReleaseHasNoChecksum_IsNotOffered_AndDoesNothingIfPostedAnyway()
    {
        var handler = new FakeReleaseHttpHandler()
            .WhenJson("api.github.com", ReleaseJson(withChecksum: false))
            .WhenBytes(InstallerName, RealInstallerBytes);
        var client = await StartAsync(handler);

        var checkStatus = await ReadJsonAsync(await client.PostAsync("/api/v1/update/check", null));
        Assert.True(checkStatus.GetProperty("updateAvailable").GetBoolean());
        Assert.False(checkStatus.GetProperty("downloadAvailable").GetBoolean());

        var requestsBefore = handler.CallCount;
        await client.PostAsync("/api/v1/update/download", null);
        var status = await WaitForDownloadToSettleAsync(client);

        Assert.Equal("failed", status.GetProperty("downloadState").GetString());
        Assert.Equal(requestsBefore, handler.CallCount);
        Assert.False(File.Exists(Path.Combine(_storage.UpdatesDirectory, InstallerName)));
    }

    [Fact]
    public async Task Download_WhenTheInstallerRequestFails_EndsFailedWithNoPartialFile()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler()
            .WhenJson("api.github.com", ReleaseJson())
            .WhenText(".sha256", Sha256Hex(RealInstallerBytes))
            .WhenStatus(InstallerName, HttpStatusCode.ServiceUnavailable));

        await client.PostAsync("/api/v1/update/check", null);
        await client.PostAsync("/api/v1/update/download", null);
        var status = await WaitForDownloadToSettleAsync(client);

        Assert.Equal("failed", status.GetProperty("downloadState").GetString());
        Assert.False(File.Exists(Path.Combine(_storage.UpdatesDirectory, InstallerName + ".part")));
    }

    [Fact]
    public async Task Reveal_WithNothingDownloaded_IsRefusedWithAMessageRatherThanA500()
    {
        var client = await StartAsync(new FakeReleaseHttpHandler());

        var response = await client.PostAsync("/api/v1/update/reveal", null);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }
}

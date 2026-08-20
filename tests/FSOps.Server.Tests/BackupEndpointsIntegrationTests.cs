using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Endpoints;
using FSOps.Server.Services.Backup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FSOps.Server.Tests;

/// <summary>
/// The backup endpoints over real HTTP, against a real Kestrel and a real database file.
///
/// <para>Everything interesting about this feature is a refusal, and a refusal that has only been
/// unit-tested is one nobody has watched happen. So each of them is driven end to end - bytes in,
/// JSON out - and asserted on the response body rather than only its status code.</para>
///
/// <para>The round trip in the middle is the point of the whole feature: what the download endpoint
/// hands over must be a file the restore endpoint will accept. Nothing else here matters if that
/// one does not hold.</para>
/// </summary>
public class BackupEndpointsIntegrationTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fsops-backup-api-{Guid.NewGuid():N}");
    private string _dataDirectory = string.Empty;
    private string _databasePath = string.Empty;

    private WebApplication? _app;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(_root, "data");
        _databasePath = Path.Combine(_dataDirectory, "fsops.db");
        Directory.CreateDirectory(_dataDirectory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// A real server on an OS-assigned port. Port 0 rather than a fixed number on purpose: another
    /// copy of FSOps is usually running on this machine while these tests do, and a test has no
    /// business competing for a port it did not open.
    /// </summary>
    private async Task<HttpClient> StartAsync(string? airlineName = "Skyline Air")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddDbContext<FsOpsDbContext>(options => options
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new WalModeConnectionInterceptor()));
        builder.Services.AddSingleton(_ => new BackupService(_dataDirectory, _databasePath) { CurrentAppVersion = "1.2.0" });

        _app = builder.Build();

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
            await db.Database.MigrateAsync();

            if (airlineName is not null)
            {
                db.Airlines.Add(new Airline
                {
                    Id = Guid.NewGuid(),
                    Name = airlineName,
                    IcaoCode = "SKY",
                    HomeAirportIcao = "EGGD",
                    StrategyProfile = AirlineStrategyProfile.Domestic,
                    Playstyle = AirlinePlaystyle.Casual,
                    OwnerUserId = Guid.NewGuid(),
                    CreatedUtc = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }

        _app.MapGroup("/api/v1").MapBackupEndpoints();
        await _app.StartAsync();

        var address = _app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        _client = new HttpClient { BaseAddress = new Uri(address) };
        return _client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), JsonOptions);

    private static async Task<HttpResponseMessage> RestoreAsync(HttpClient client, byte[] bytes, string fileName)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await client.PostAsync($"/api/v1/backup/restore?fileName={Uri.EscapeDataString(fileName)}", content);
    }

    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task Status_NamesTheFileTheBackupWouldGetAndSaysHowBigTheDatabaseIs()
    {
        var client = await StartAsync();

        var status = await ReadJsonAsync(await client.GetAsync("/api/v1/backup/status"));

        var suggested = status.GetProperty("suggestedFileName").GetString()!;
        Assert.StartsWith("Skyline Air backup ", suggested);
        Assert.EndsWith(".fsopsbak", suggested);
        Assert.True(status.GetProperty("databaseSizeBytes").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pendingRestore").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastRestore").ValueKind);
    }

    [Fact]
    public async Task Download_ThenRestore_IsAcceptedAndStaged()
    {
        // The round trip. If this does not hold, nothing else about the feature matters.
        var client = await StartAsync();

        var download = await client.GetAsync("/api/v1/backup/file");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        var offeredName = download.Content.Headers.ContentDisposition?.FileNameStar
            ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(offeredName);
        Assert.StartsWith("Skyline Air backup ", offeredName);
        Assert.EndsWith(".fsopsbak", offeredName);

        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);

        var restore = await RestoreAsync(client, bytes, offeredName!);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

        var status = await ReadJsonAsync(restore);
        var pending = status.GetProperty("pendingRestore");
        Assert.Equal(JsonValueKind.Object, pending.ValueKind);
        Assert.Equal(offeredName, pending.GetProperty("sourceFileName").GetString());

        // The safety copy has to exist by the time the response says the restore is staged, not
        // afterwards - that ordering is the whole protection.
        var safetyCopy = pending.GetProperty("safetyCopyPath").GetString()!;
        Assert.True(File.Exists(safetyCopy), "the current airline must already be saved when staging is reported");

        // And the staged file is really sitting there waiting for the next start.
        Assert.True(File.Exists(PendingRestore.StagedDatabasePath(_dataDirectory)));
    }

    [Fact]
    public async Task Download_LeavesNoTemporaryFileBehind()
    {
        var client = await StartAsync();

        await (await client.GetAsync("/api/v1/backup/file")).Content.ReadAsByteArrayAsync();

        // The server disposes the response stream - and with it the temporary copy - once the
        // response has finished, which is necessarily a moment after the client has the last byte.
        // So some waiting is unavoidable here; what matters is that the wait is a generous ceiling
        // rather than a guess at how long it takes. A healthy run leaves this loop on the first or
        // second pass and the ceiling costs nothing; only a genuine failure ever waits it out.
        // Budgeted at thirty seconds because a shorter one is a test that fails on a busy machine
        // while the code is perfectly correct, which is worse than a slow failure.
        var scratchDirectory = PendingRestore.BackupsDirectory(_dataDirectory);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Directory.GetFiles(scratchDirectory, "working-*").Length > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Empty(Directory.GetFiles(scratchDirectory, "working-*"));
    }

    [Fact]
    public async Task Restore_WithATruncatedBackup_Is400AndStagesNothing()
    {
        var client = await StartAsync();
        var whole = await (await client.GetAsync("/api/v1/backup/file")).Content.ReadAsByteArrayAsync();

        var response = await RestoreAsync(client, whole[..(whole.Length / 2)], "half a backup.fsopsbak");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("incomplete", body.GetProperty("error").GetString());
        Assert.False(File.Exists(PendingRestore.StagedDatabasePath(_dataDirectory)));

        // A refused restore must not have saved anything "just in case" either - the safety copy is
        // taken only once there is a verified replacement, so its absence here is the proof that
        // nothing of the player's was ever at risk.
        Assert.Empty(Directory.GetFiles(PendingRestore.BackupsDirectory(_dataDirectory), "*.fsopsbak"));
    }

    [Fact]
    public async Task Restore_WithAFileThatIsNotABackup_Is400AndSaysWhatABackupLooksLike()
    {
        var client = await StartAsync();

        var response = await RestoreAsync(client, Encoding.ASCII.GetBytes(new string('n', 9_000)), "notes.txt");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(".fsopsbak", body.GetProperty("error").GetString());
        Assert.False(File.Exists(PendingRestore.StagedDatabasePath(_dataDirectory)));
    }

    [Fact]
    public async Task Restore_WithNothingAtAll_Is400RatherThanAnEmptyAirline()
    {
        var client = await StartAsync();

        var response = await RestoreAsync(client, Array.Empty<byte>(), "nothing.fsopsbak");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("nothing to restore", (await ReadJsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Restore_ThenCancel_ClearsThePendingRestoreButKeepsTheSafetyCopy()
    {
        var client = await StartAsync();
        var bytes = await (await client.GetAsync("/api/v1/backup/file")).Content.ReadAsByteArrayAsync();

        var staged = await ReadJsonAsync(await RestoreAsync(client, bytes, "a backup.fsopsbak"));
        var safetyCopy = staged.GetProperty("pendingRestore").GetProperty("safetyCopyPath").GetString()!;

        var cancelled = await ReadJsonAsync(await client.PostAsync("/api/v1/backup/restore/cancel", null));

        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("pendingRestore").ValueKind);
        Assert.False(File.Exists(PendingRestore.StagedDatabasePath(_dataDirectory)));
        Assert.True(File.Exists(safetyCopy), "cancelling a restore must never delete a backup");
    }

    [Fact]
    public async Task BeforeAnAirlineExists_ABackupIsStillOfferedWithAUsableName()
    {
        var client = await StartAsync(airlineName: null);

        var download = await client.GetAsync("/api/v1/backup/file");
        var offeredName = download.Content.Headers.ContentDisposition?.FileNameStar
            ?? download.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.StartsWith("FSOps backup ", offeredName);
    }
}

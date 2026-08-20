using System.Diagnostics;
using System.Text.Json.Serialization;
using FSOps.Core;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
using FSOps.Core.Time;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Endpoints;
using FSOps.Server.Hubs;
using FSOps.Server.Services;
using FSOps.Server.Services.Backup;
using FSOps.Sim;
using FSOps.Sim.Fake;
using FSOps.Sim.SimConnect;
using Serilog;

// Pin the content root to the folder the assembly actually lives in, rather than letting it
// default to the process working directory. The SPA is served from <contentRoot>/wwwroot, so a
// launch whose working directory is anywhere else - a shortcut with a "Start in" folder, a
// terminal in another directory, a scheduled task - would find no wwwroot and serve the "UI not
// built yet" fallback instead of the app. That is a blank product for an installed user, and it
// has now bitten twice, so resolve it from the binary's own location and stop depending on how
// the process happened to be started.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Localhost only - this app tracks flights and money through SimConnect and a local
// SQLite ledger, and none of that should ever be reachable from another machine. The port
// defaults to 5977 but can be overridden with FSOPS_PORT - useful when that port is already
// taken by another copy of the app, and for tests/tooling that must never collide with the
// owner's own running instance.
var port = Environment.GetEnvironmentVariable("FSOPS_PORT") ?? "5977";
builder.WebHost.UseUrls($"http://localhost:{port}");

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(AppPaths.LogsDirectory, "fsops-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7);
});

const string DevClientCorsPolicy = "DevClient";

builder.Services.AddCors(options =>
{
    options.AddPolicy(DevClientCorsPolicy, policy =>
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

builder.Services.AddSignalR();
builder.Services.AddScoped<ICurrentUser, LocalUser>();
builder.Services.AddFsOpsData();

// The single place economy-config.json is ever loaded from - every tuning constant the economy
// engine uses (fares, demand, fuel, costs), plus the "casual"/"trueLife" playstyle overrides (see
// EconomyConfigCatalog for how those are merged). AppContext.BaseDirectory (not ContentRootPath)
// so this resolves the same way under "dotnet run" and a published exe, matching the world-data
// seed path above. Falls back to EconomyConfigCatalog.Default() if the file is missing (e.g. a
// stripped deployment); either way, both playstyles' Validate() has already run by the time this
// returns, so a bad config fails fast at startup rather than producing silently wrong numbers
// mid-flight.
builder.Services.AddSingleton(_ =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "economy-config.json");
    return File.Exists(path) ? EconomyConfigCatalog.FromJson(File.ReadAllText(path)) : EconomyConfigCatalog.Default();
});
// Deliberately NOT registered: a flat EconomyConfig singleton. One existed briefly as a
// compatibility shim while the playstyle work landed, and it always resolved to Casual - which
// meant anything injecting it silently billed a True-life airline at Casual rates. Every economy
// figure must be resolved per-airline via EconomyConfigCatalog.Get(airline.Playstyle); a resolved
// EconomyConfig is then passed down as a plain argument. If a constructor ever fails to resolve
// EconomyConfig, that is the correct failure - inject the catalog instead, do not re-add this.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// "--sim=fake" (or Sim:Source in config) plays back a scripted flight with no simulator
// installed - the project's main development and test strategy. Anything else, including no
// setting at all, talks to a real, running copy of MSFS.
var simSource = builder.Configuration["sim"] ?? builder.Configuration["Sim:Source"] ?? "SimConnect";

builder.Services.AddSingleton<ISimSource>(sp =>
{
    if (string.Equals(simSource, "fake", StringComparison.OrdinalIgnoreCase))
    {
        var replayPath = builder.Configuration["Sim:ReplayFile"]
            ?? Path.Combine(AppContext.BaseDirectory, "Fake", "Replays", "egkk-lebl.json");
        var timeCompression = builder.Configuration.GetValue<double?>("Sim:TimeCompression") ?? 1.0;

        return new FakeSimSource(new FakeSimSourceOptions
        {
            ReplayFilePath = replayPath,
            TimeCompressionFactor = timeCompression,
            Loop = true,
        });
    }

    return new SimConnectSource(new SimConnectSourceOptions(), sp.GetRequiredService<ILogger<SimConnectSource>>());
});
builder.Services.AddSingleton<SimTelemetryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SimTelemetryService>());
builder.Services.AddSingleton<FlightLifecycleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FlightLifecycleService>());
builder.Services.AddHostedService<HeartbeatService>();

// Posts monthly lease/salary/insurance charges on the real-world clock, with startup catch-up for
// however long the app was closed - see EconomyClockService's class doc. Without this the balance
// is fiction: the fixed costs the economy is tuned around would never actually be charged.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHostedService<EconomyClockService>();

// Resolves every virtual pilot's scheduled occurrence whose block time has elapsed on the real
// wall clock, with the same startup catch-up shape as EconomyClockService - see
// VirtualFlightResolverService's class doc for how it reuses that service's idempotency/
// monotonicity/capped-catch-up pattern rather than inventing a second one.
builder.Services.AddHostedService<VirtualFlightResolverService>();

// Populated during startup, below - nothing can be added to the service collection after Build().
builder.Services.AddSingleton<StartupReconciliationState>();

// Which aircraft the player can actually load in the simulator. The scanner is stateless and holds
// no handles, so a singleton is enough; the service around it is scoped because it reads and writes
// the settings row through the request's DbContext.
builder.Services.AddSingleton<InstalledAircraftScanner>();
builder.Services.AddScoped<SimAircraftService>();

// Contract flying. Scoped for the same reason SimAircraftService is: it reads and writes through the
// request's DbContext. It holds no state of its own - the board is a pure function of the world seed,
// the airline and the time bucket, so there is nothing to cache and nothing to keep warm.
builder.Services.AddScoped<ContractBoardService>();

// VATSIM's public data feed, fetched server-side only - the SPA never calls it directly, because
// the UI has no business making third-party requests of its own. A singleton so one cached fetch
// is shared by every client rather than one
// request per poll; the timeout is deliberately generous but finite, since a slow feed must never
// hold up a dashboard request.
builder.Services.AddHttpClient("vatsim", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<IVatsimNetworkClient, VatsimNetworkClient>();

// Bundled VAT-Spy FIR/UIR geometry - the only reason en-route (CTR/FSS) controllers can be shown
// at all. Singleton because it parses two data files once, lazily, on the first request that
// needs a boundary; nothing is fetched, so this stays true offline.
builder.Services.AddSingleton<IAtcBoundarySource, VatSpyBoundarySource>();

// Recognises the player's own flights on the public feed while a sector is being tracked.
// Deliberately no HttpClient of its own: it reuses the IVatsimNetworkClient singleton above, so
// online detection shares the one cached fetch rather than adding a second poll of the same feed.
builder.Services.AddSingleton<VatsimFlightCorroborationService>();

// The update check's transport - see UpdateChecker for why this exists at all and how far it is
// allowed to go. Second and last outbound call the server makes, after VATSIM. Nothing is
// registered as a hosted service on purpose: the check is lazy and cached on disk, so there is
// deliberately nothing about updates on the startup path.
//
// The ten-minute handler timeout is a backstop for a large installer download, not the real
// limit - the timeouts that matter (seconds for metadata, minutes for the asset) are enforced
// inside GitHubReleaseClient, so a slow feed can never hold up a request that is merely asking
// whether an update exists.
builder.Services.AddHttpClient(GitHubReleaseClient.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddSingleton<IUpdateStorage, FileUpdateStorage>();
builder.Services.AddSingleton<IGitHubReleaseClient, GitHubReleaseClient>();

// The release channel, the one piece of updater state that IS a database column - it is a user
// setting and belongs with the user's settings. Registered as a singleton holding a scope factory
// rather than a DbContext, and written so that an unreadable database resolves to the stable
// channel; see DatabaseUpdateChannelStore for why that direction is the safe one. Nothing here runs
// at startup: the first read happens when something asks for the update status.
builder.Services.AddSingleton<IUpdateChannelStore, DatabaseUpdateChannelStore>();
builder.Services.AddSingleton<UpdateChecker>();

// Saving the airline to a file and putting one back. A singleton holding the two paths it is
// allowed to touch, so no request handler builds a path of its own - the same reasoning as
// IUpdateStorage above, and for a feature where writing to the wrong place is the whole risk.
builder.Services.AddSingleton(sp => new BackupService(
    AppPaths.DataDirectory,
    AppPaths.DatabasePath,
    sp.GetRequiredService<ILogger<BackupService>>()));

var app = builder.Build();

// MUST run here: before the migration below, and therefore before anything has opened the
// database. A restore replaces fsops.db wholesale, which cannot be done while the server holds it
// open - so the request that accepted the file only staged it, and this is where it actually
// happens. Migrations run immediately afterwards, which is exactly what makes restoring an older
// backup into a newer build work: the restored schema is simply brought forward. See PendingRestore.
{
    var restoreLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseRestore");
    PendingRestore.ApplyIfStaged(AppPaths.DataDirectory, AppPaths.DatabasePath, restoreLogger);
}

// Migrations are fast (schema-only), so this runs synchronously before Kestrel starts
// listening. The world data import can take longer, so it's kicked off in the background
// below and its progress is polled through /api/v1/worlddata/status instead of blocking
// startup on it.
// The database is the whole application - every screen is a query - so a file that cannot be
// opened is a fail-fast case, deliberately the opposite of the wall-clock catch-up services'
// log-and-carry-on. What was wrong before was never that the app exited; it was that it exited
// saying nothing anyone could act on. FsOpsDatabaseUnavailableException carries text written for
// the person in front of the app, StartupFailureReport puts it where the shell can show it, and
// exit code 3 tells the shell to go and look. See FsOpsDatabaseUnavailableException for why
// FSOps must never repair, rename or delete a damaged database by itself.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
try
{
    app.Services.MigrateFsOpsDatabase();
    StartupFailureReport.Clear();
}
catch (FsOpsDatabaseUnavailableException ex)
{
    startupLogger.LogCritical(ex, "The database at {Path} could not be opened.", ex.DatabasePath);
    Log.CloseAndFlush();
    return StartupFailureReport.Write(ex.UserMessage);
}
catch (FsOpsBackupFailedException ex)
{
    startupLogger.LogCritical(ex, "The pre-migration database backup could not be taken; nothing was migrated.");
    Log.CloseAndFlush();
    return StartupFailureReport.Write(ex.Message);
}

// MUST run here: after the schema migration, and strictly before any hosted service starts.
// Reservation only became a hard two-way invariant in E3, so a database written earlier can hold
// an aircraft that is both ReservedForPlayer and carrying virtual-pilot legs - reserving one never
// checked for a schedule before. VirtualFlightResolverService does not consult ReservedForPlayer at
// all and runs its first resolution pass synchronously the moment it starts (at app.Run(), not at
// registration above), so if it went first it would fly a contradictory aircraft's legs while the
// Fly screen simultaneously offered the same airframe to the player - exactly the two-actor
// collision the invariant exists to prevent. See ReservationReconciler for the resolution rule.
using (var reservationScope = app.Services.CreateScope())
{
    var reservationDb = reservationScope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
    var reservationLogger = reservationScope.ServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("ReservationReconciliation");

    app.Services.GetRequiredService<StartupReconciliationState>().Result =
        await ReservationReconciler.ReconcileAsync(reservationDb, reservationLogger);
}

// AppContext.BaseDirectory (not ContentRootPath) so this resolves the same way whether
// running via "dotnet run" (project bin output) or from a published single-file exe -
// both ship the data/ folder next to the assembly via the csproj's CopyToOutputDirectory.
var seedDataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
_ = app.Services.SeedWorldDataAsync(seedDataDirectory);

if (app.Environment.IsDevelopment())
{
    // Only the Vite dev server needs this, since it runs on a different port than
    // Kestrel. The built SPA is served same-origin everywhere else, so no CORS there.
    app.UseCors(DevClientCorsPolicy);
}

app.UseDefaultFiles();
app.UseStaticFiles();

var apiV1 = app.MapGroup("/api/v1");

apiV1.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    version = AppVersion.Current,
    serverTimeUtc = DateTime.UtcNow.ToString("o")
}));

apiV1.MapAirportEndpoints();
apiV1.MapWorldDataEndpoints();
apiV1.MapAirlineEndpoints();
apiV1.MapRouteEndpoints();
apiV1.MapPlanningEndpoints();
apiV1.MapSettingsEndpoints();
apiV1.MapSimEndpoints();
apiV1.MapSimAircraftEndpoints();
apiV1.MapContractEndpoints();
apiV1.MapFlightEndpoints();
apiV1.MapFleetEndpoints();
apiV1.MapFleetDisposalEndpoints();
apiV1.MapFleetRepositionEndpoints();
apiV1.MapFinanceEndpoints();
apiV1.MapPilotEndpoints();
apiV1.MapMaintenanceEndpoints();
apiV1.MapStatsEndpoints();
apiV1.MapVatsimEndpoints();
apiV1.MapUpdateEndpoints();
apiV1.MapOperationsEndpoints();
apiV1.MapBackupEndpoints();

app.MapHub<LiveHub>("/hubs/live");

// wwwroot is empty until the frontend is built into it, so this has to degrade
// gracefully instead of throwing when index.html isn't there yet.
app.MapFallback(async context =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var webRootPath = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var indexPath = Path.Combine(webRootPath, "index.html");

    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(indexPath);
    }
    else
    {
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("FSOps UI not built yet - run npm run build in src/fsops-web");
    }
});

if (!app.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("FSOPS_NO_BROWSER") is null)
{
    try
    {
        // The resolved port, not a hardcoded 5977. These had drifted apart: a second instance
        // started on another port (because 5977 was taken, or FSOPS_PORT was set) opened a browser
        // pointed at the FIRST instance - so the user was looking at a different copy of the app
        // than the one they had just launched, with no indication anything was wrong.
        Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true });
    }
    catch
    {
        // Best-effort only - a missing default browser shouldn't stop the server.
    }
}

// Last-chance diagnostics. This exists because of a real, repeated failure: nine identical
// process terminations were found in the Windows Application event log with NOTHING in FSOps' own
// log file. The throw happened inside Host.DisposeAsync() - after logging had already been torn
// down - and it aborted the disposal loop part-way, so Serilog was never flushed. From the user's
// side the app simply vanished, which is close to undiagnosable.
//
// The underlying disposal bug is fixed where it belongs, but the blind spot was the worse problem:
// a crash that leaves no trace turns a five-minute fix into an open question that sits in the plan
// for weeks. Anything that kills this process should now leave something behind.
// Deliberately the logger resolved from the container, not the static Log.Fatal. UseSerilog's
// service-provider overload configures the HOST's logger, and relying on the static one here would
// risk writing these particular messages - the ones that explain a fatal exit - into a logger that
// may never have been assigned. Losing this message specifically would be a bitter way to
// reintroduce the very blind spot these handlers exist to close.
var fatalLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FSOps");

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    fatalLogger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; the process is terminating");
    Log.CloseAndFlush();
};

// Faults on a task nobody awaited are silent by default. Several of this app's moving parts are
// background services, so this is exactly where an error would otherwise disappear.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    fatalLogger.LogError(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

try
{
    app.Run();
}
catch (Exception ex)
{
    // Covers startup failures and anything thrown out of shutdown, including host disposal - the
    // exact path that produced those nine silent deaths.
    fatalLogger.LogCritical(ex, "FSOps terminated unexpectedly");
    throw;
}
finally
{
    // Serilog's file sink buffers. Without this, the last writes before an abnormal exit - the
    // ones that actually explain what happened - are lost.
    Log.CloseAndFlush();
}

return 0;

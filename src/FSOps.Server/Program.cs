using System.Diagnostics;
using System.Text.Json.Serialization;
using FSOps.Core;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Time;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Endpoints;
using FSOps.Server.Hubs;
using FSOps.Server.Services;
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
// docs/PLAN.md "Playstyle - Casual vs True-life"). AppContext.BaseDirectory (not ContentRootPath)
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
// however long the app was closed - see EconomyClockService's class doc and
// docs/PLAN.md "Status after the progression-loop rebalance".
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHostedService<EconomyClockService>();

// Resolves every virtual pilot's scheduled occurrence whose block time has elapsed on the real
// wall clock, with the same startup catch-up shape as EconomyClockService - see
// VirtualFlightResolverService's class doc for how it reuses that service's idempotency/
// monotonicity/capped-catch-up pattern rather than inventing a second one.
builder.Services.AddHostedService<VirtualFlightResolverService>();

// Populated during startup, below - nothing can be added to the service collection after Build().
builder.Services.AddSingleton<StartupReconciliationState>();

var app = builder.Build();

// Migrations are fast (schema-only), so this runs synchronously before Kestrel starts
// listening. The world data import can take longer, so it's kicked off in the background
// below and its progress is polled through /api/v1/worlddata/status instead of blocking
// startup on it.
app.Services.MigrateFsOpsDatabase();

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
    version = "0.1.0",
    serverTimeUtc = DateTime.UtcNow.ToString("o")
}));

apiV1.MapAirportEndpoints();
apiV1.MapWorldDataEndpoints();
apiV1.MapAirlineEndpoints();
apiV1.MapRouteEndpoints();
apiV1.MapSettingsEndpoints();
apiV1.MapSimEndpoints();
apiV1.MapFlightEndpoints();
apiV1.MapFleetEndpoints();
apiV1.MapFleetDisposalEndpoints();
apiV1.MapFinanceEndpoints();
apiV1.MapPilotEndpoints();
apiV1.MapMaintenanceEndpoints();
apiV1.MapOperationsEndpoints();

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
        Process.Start(new ProcessStartInfo("http://localhost:5977") { UseShellExecute = true });
    }
    catch
    {
        // Best-effort only - a missing default browser shouldn't stop the server.
    }
}

app.Run();

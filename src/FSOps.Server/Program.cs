using System.Diagnostics;
using System.Text.Json.Serialization;
using FSOps.Core;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Endpoints;
using FSOps.Server.Hubs;
using FSOps.Server.Services;
using FSOps.Sim;
using FSOps.Sim.Fake;
using FSOps.Sim.SimConnect;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

// Migrations are fast (schema-only), so this runs synchronously before Kestrel starts
// listening. The world data import can take longer, so it's kicked off in the background
// below and its progress is polled through /api/v1/worlddata/status instead of blocking
// startup on it.
app.Services.MigrateFsOpsDatabase();

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

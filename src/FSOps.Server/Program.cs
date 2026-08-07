using System.Diagnostics;
using System.Text.Json.Serialization;
using FSOps.Core;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Endpoints;
using FSOps.Server.Hubs;
using FSOps.Server.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Localhost only - this app tracks flights and money through SimConnect and a local
// SQLite ledger, and none of that should ever be reachable from another machine.
builder.WebHost.UseUrls("http://localhost:5977");

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
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddFsOpsData();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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

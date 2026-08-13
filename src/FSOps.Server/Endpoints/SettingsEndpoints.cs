using FSOps.Core.Entities;
using FSOps.Core.Money;
using FSOps.Data;
using FSOps.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/settings", GetAsync);
        group.MapPut("/settings", UpdateAsync);
        group.MapGet("/settings/currencies", GetCurrenciesAsync);
    }

    private static async Task<IResult> GetAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var settings = await GetOrCreateAsync(db, currentUser.UserId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(settings);
    }

    private static async Task<IResult> UpdateAsync(UpdateSettingsRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var currency = CurrencyCatalogue.TryGet(request.CurrencyCode);
        if (currency is null)
        {
            return Results.BadRequest(new { error = $"Unsupported currency code '{request.CurrencyCode}'." });
        }

        if (!Enum.TryParse<DistanceUnit>(request.DistanceUnit, ignoreCase: true, out var distanceUnit))
        {
            return Results.BadRequest(new { error = "distanceUnit must be Nm or Km." });
        }

        if (!Enum.TryParse<AltitudeUnit>(request.AltitudeUnit, ignoreCase: true, out var altitudeUnit))
        {
            return Results.BadRequest(new { error = "altitudeUnit must be Feet or Metres." });
        }

        if (!Enum.TryParse<WeightUnit>(request.WeightUnit, ignoreCase: true, out var weightUnit))
        {
            return Results.BadRequest(new { error = "weightUnit must be Kg or Lb." });
        }

        if (!Enum.TryParse<TimeDisplay>(request.TimeDisplay, ignoreCase: true, out var timeDisplay))
        {
            return Results.BadRequest(new { error = "timeDisplay must be Utc or Local." });
        }

        var settings = await GetOrCreateAsync(db, currentUser.UserId, ct);
        settings.CurrencyCode = currency.Code;
        settings.DistanceUnit = distanceUnit;
        settings.AltitudeUnit = altitudeUnit;
        settings.WeightUnit = weightUnit;
        settings.TimeDisplay = timeDisplay;
        settings.Use24HourClock = request.Use24HourClock;
        settings.Theme = string.IsNullOrWhiteSpace(request.Theme) ? settings.Theme : request.Theme.Trim();
        settings.SimBriefPilotId = request.SimBriefPilotId;
        // Stored and validated exactly like SimBriefPilotId above - a non-numeric or blank value
        // just means "not set" (trimmed to null), never a 400. VATSIM CIDs are plain digits; a
        // stricter check isn't worth it since an invalid value simply never matches anyone on the
        // feed and G8/G9 quietly find nothing, the same fail-soft posture as every other VATSIM path.
        var trimmedCid = request.VatsimCid?.Trim();
        settings.VatsimCid = string.IsNullOrEmpty(trimmedCid) ? null : trimmedCid;

        await db.SaveChangesAsync(ct);
        return Results.Ok(settings);
    }

    // Maps the internal Currency domain type onto the field names the frontend and docs agree
    // on, so the API boundary is stable even if the internal type's shape changes later.
    private static IResult GetCurrenciesAsync()
    {
        var currencies = CurrencyCatalogue.All.Select(c => new CurrencyResponse(
            c.Code,
            c.Symbol,
            c.EnglishName,
            c.Placement == CurrencySymbolPlacement.Before,
            c.DecimalPlaces,
            c.DisplayRate));

        return Results.Ok(currencies);
    }

    private static async Task<UserSettings> GetOrCreateAsync(FsOpsDbContext db, Guid ownerUserId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId, ct);
        if (settings is not null)
        {
            return settings;
        }

        settings = new UserSettings { Id = Guid.NewGuid(), OwnerUserId = ownerUserId };
        db.UserSettings.Add(settings);
        return settings;
    }
}

public record UpdateSettingsRequest(
    string? CurrencyCode,
    string? DistanceUnit,
    string? AltitudeUnit,
    string? WeightUnit,
    string? TimeDisplay,
    bool Use24HourClock,
    string? Theme,
    string? SimBriefPilotId,
    string? VatsimCid);

public record CurrencyResponse(
    string Code,
    string Symbol,
    string Name,
    bool SymbolBefore,
    int DecimalPlaces,
    decimal Rate);

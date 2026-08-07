namespace FSOps.Core.Entities;

/// <summary>
/// One row per user (see ICurrentUser). Created lazily with sensible sim-standard defaults the
/// first time it's needed rather than requiring an explicit setup step - see GetOrCreateAsync
/// in SettingsEndpoints.
/// </summary>
public class UserSettings
{
    public Guid Id { get; set; }

    public Guid OwnerUserId { get; set; }

    public string CurrencyCode { get; set; } = "GBP";

    public DistanceUnit DistanceUnit { get; set; } = DistanceUnit.Nm;

    public AltitudeUnit AltitudeUnit { get; set; } = AltitudeUnit.Feet;

    public WeightUnit WeightUnit { get; set; } = WeightUnit.Kg;

    public TimeDisplay TimeDisplay { get; set; } = TimeDisplay.Utc;

    public bool Use24HourClock { get; set; } = true;

    public string Theme { get; set; } = "dark";

    public string? CommunityFolderPath { get; set; }

    public string? SimBriefPilotId { get; set; }
}

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

    /// <summary>
    /// Vestigial. This held the MSFS Community folder path for the in-game toolbar panel, which has
    /// been removed from the product; nothing reads or writes it any more. It is kept only so the
    /// column can be dropped in its own migration later, with the non-destructive proof a table
    /// rebuild deserves - deleting the property here would desync the model snapshot and hand the
    /// next unrelated migration a silent DropColumn. Do not wire anything new to it.
    /// </summary>
    public string? CommunityFolderPath { get; set; }

    public string? SimBriefPilotId { get; set; }

    /// <summary>
    /// The player's VATSIM certificate ID, used only to recognise their own flights on VATSIM's
    /// public feed. Stored locally like every other setting and <b>never sent anywhere</b> - FSOps
    /// reads the public feed and looks for this value in it, rather than telling VATSIM anything.
    /// Null disables online detection entirely, and no request is made on its behalf.
    /// </summary>
    public string? VatsimCid { get; set; }
}

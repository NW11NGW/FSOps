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

    public string? SimBriefPilotId { get; set; }

    /// <summary>
    /// Which releases the in-app updater may offer - see <see cref="Entities.UpdateChannel"/>. Kept
    /// here rather than in the updater's own state file because it is a user setting like every
    /// other one on this row, and because a setting that lives with the settings is a setting people
    /// can find. The updater reads it through IUpdateChannelStore, which answers
    /// <see cref="UpdateChannel.Stable"/> whenever the database cannot supply an answer - so this
    /// column being absent, empty or unreadable can only ever make FSOps more cautious, never less.
    /// </summary>
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>
    /// The player's VATSIM certificate ID, used only to recognise their own flights on VATSIM's
    /// public feed. Stored locally like every other setting and <b>never sent anywhere</b> - FSOps
    /// reads the public feed and looks for this value in it, rather than telling VATSIM anything.
    /// Null disables online detection entirely, and no request is made on its behalf.
    /// </summary>
    public string? VatsimCid { get; set; }
}

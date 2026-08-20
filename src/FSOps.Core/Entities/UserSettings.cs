using FSOps.Core.SimAircraft;

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

    /// <summary>
    /// Where the player's MSFS Community folder is, when they have told FSOps or FSOps found it.
    /// Null means "look for it" - <c>SimInstallLocator</c> asks the simulator's own UserCfg.opt
    /// rather than guessing, and a null here is never a claim that there is no folder.
    ///
    /// <para>This column existed once before, for installing the in-game toolbar panel, and was
    /// dropped when the panel was cut from the product. It is back for an unrelated reason: FSOps
    /// reads the folder to find out which aircraft the player can actually load, so a contract is
    /// never written for something that is not in their hangar. FSOps only ever READS this folder.</para>
    /// </summary>
    public string? CommunityFolderPath { get; set; }

    /// <summary>
    /// Which edition of MSFS 2024 the player has, used to work out which base aircraft they can
    /// load. Defaults to <see cref="SimAircraft.SimEdition.Standard"/> - the smallest set - because
    /// guessing low costs somebody a tick box and guessing high costs them a contract they cannot fly.
    /// </summary>
    public SimEdition SimEdition { get; set; } = SimEdition.Standard;

    /// <summary>
    /// The last scan of the player's simulator folders, as JSON (see <c>AircraftScanResult</c>).
    /// Null means no scan has been run, which is different from a scan that found nothing. Cached
    /// rather than re-run because walking somebody's whole package folder is not something to do on
    /// every request, and because the answer only changes when they install something.
    /// </summary>
    public string? SimAircraftScanJson { get; set; }

    /// <summary>
    /// The aircraft the player ticked or unticked by hand, as JSON: <c>{"on":[...],"off":[...]}</c>.
    /// Null means they have not overridden anything. These beat both the scan and the edition,
    /// because the player is the only one here who actually knows what is in their simulator.
    /// </summary>
    public string? SimAircraftOverridesJson { get; set; }
}

namespace FSOps.Core.SimAircraft;

/// <summary>
/// Which edition of MSFS 2024 the player bought. The editions are cumulative - Deluxe is Standard
/// plus ten aircraft, Premium Deluxe is Deluxe plus fifteen more - so this is an ordered scale and
/// <see cref="SimEditionExtensions.Includes"/> relies on that ordering.
///
/// <para><b>Standard is the default for anybody who has not said otherwise</b>, because it is the
/// smallest set. Guessing low means somebody occasionally has to tick a box for an aircraft they
/// own; guessing high means offering somebody a job in an aircraft they cannot load, which is the
/// exact failure this whole feature exists to prevent.</para>
/// </summary>
public enum SimEdition
{
    Standard = 0,
    Deluxe = 1,
    PremiumDeluxe = 2,
}

/// <summary>
/// Which edition an aircraft ships with, or <see cref="AddOn"/> for anything that is not base
/// content at all - a marketplace or freeware package the player installed themselves.
///
/// <para>Deliberately a separate enum from <see cref="SimEdition"/> rather than a nullable one:
/// "ships with Deluxe" and "you own Deluxe" are different facts and conflating them is how an
/// add-on ends up silently treated as base content.</para>
/// </summary>
public enum SimAircraftAvailability
{
    Standard = 0,
    Deluxe = 1,
    PremiumDeluxe = 2,

    /// <summary>
    /// Not base content in any edition. FSOps never assumes the player has one of these: it is
    /// offered only when a scan actually found it on disk, or the player ticked it themselves.
    /// This is also where anything goes when the edition it ships with cannot be established with
    /// confidence - see <c>ContractAircraftCatalogue</c>.
    /// </summary>
    AddOn = 99,
}

public static class SimEditionExtensions
{
    /// <summary>
    /// True when an edition includes an aircraft as base content. Add-ons are never included by an
    /// edition, however expensive the edition is.
    /// </summary>
    public static bool Includes(this SimEdition edition, SimAircraftAvailability availability) =>
        availability switch
        {
            SimAircraftAvailability.Standard => true,
            SimAircraftAvailability.Deluxe => edition >= SimEdition.Deluxe,
            SimAircraftAvailability.PremiumDeluxe => edition >= SimEdition.PremiumDeluxe,
            _ => false,
        };
}

namespace FSOps.Core.SimAircraft;

/// <summary>
/// Roughly what an aircraft is for. Contract generation needs this to decide what kind of job is
/// plausible in it - a bank courier run is not a Boeing 747 job and a hundred passengers is not a
/// Cessna 172 job - so it is a property of the aircraft rather than something inferred from seat
/// count at the point of use.
/// </summary>
public enum ContractAircraftCategory
{
    LightSingle,
    LightTwin,
    UtilityTurboprop,
    BusinessJet,
    RegionalAirliner,
    Narrowbody,
    Widebody,
}

/// <summary>
/// One aircraft the player might be able to load in the simulator, and everything contract
/// generation needs in order to write a job for it.
///
/// <para><b>This is NOT the fleet catalogue and must never be merged with it.</b> The fleet
/// catalogue (<c>AircraftTypeSeeder</c>) is the list of aircraft an airline may buy or lease, and
/// it is airliners only on purpose: the demand model, the seat-based fare model and the
/// weight-based airport fees all assume an airliner, and a fleet full of light singles would
/// quietly corrupt every one of them. This list exists for the opposite job - a contract supplies
/// the aircraft, the player just flies it - so it reaches down to a Cessna 152. The two lists
/// overlap (an ATR ferry flight is a perfectly reasonable contract) but adding something here can
/// never make it purchasable, because nothing reads this list to decide what an airline may own.</para>
/// </summary>
/// <param name="TypeDesignator">
/// The ICAO type designator, upper-case - "C172", "A320", "B38M". This is the primary key of this
/// catalogue and also the value a scan reads out of a package's <c>aircraft.cfg</c>
/// (<c>icao_type_designator</c>), which is what lets a scan identify a package exactly rather than
/// by guessing at its title.
/// </param>
/// <param name="Name">Display name, as a person would say it.</param>
/// <param name="Manufacturer">Display manufacturer.</param>
/// <param name="Category">What kind of flying this aircraft is for.</param>
/// <param name="Seats">
/// Passenger seats available to a contract - the cabin, not counting the flight crew. Zero for
/// anything that realistically only carries freight.
/// </param>
/// <param name="PayloadKg">
/// Useful load in kilograms: everything the aircraft can carry that is not fuel or crew. Contract
/// generation sizes cargo jobs from this.
/// </param>
/// <param name="RangeNm">Practical still-air range with a normal payload, nautical miles.</param>
/// <param name="CruiseTasKts">Typical cruise true airspeed, knots. Used to estimate a job's length.</param>
/// <param name="MatchPatterns">
/// A JSON array of regex strings, in exactly the format and with exactly the semantics of
/// <c>AircraftType.MatchPatterns</c> - so <c>AircraftTypeMatcher.IsMatch</c> can be used against
/// them unchanged. Used to recognise this aircraft from the freeform TITLE / ATC MODEL text a
/// package or the sim reports when no <c>icao_type_designator</c> is available.
/// </param>
/// <param name="ShipsWith">
/// Which edition of MSFS 2024 includes this as base content, or <see cref="SimAircraftAvailability.AddOn"/>.
/// </param>
/// <param name="BasePackageIds">
/// The base-content package folder names this aircraft is delivered in - for example
/// <c>fs24-asobo-aircraft-c172sp-classic</c>. Finding one of these on disk is evidence the sim has
/// the aircraft; not finding it proves nothing at all, because MSFS 2024 streams most base content
/// and only caches what has actually been flown. Empty for add-ons, which are found in the
/// Community folder instead.
///
/// <para>These are always the flyable package ids, never a <c>passiveaircraft-</c> one. The sim
/// ships low-detail <c>passiveaircraft-</c> models of aircraft the player cannot fly, so they exist
/// on disk for AI traffic regardless of edition - treating one as evidence of ownership would hand
/// somebody a job in an aircraft that is not in their hangar.</para>
/// </param>
public sealed record ContractAircraft(
    string TypeDesignator,
    string Name,
    string Manufacturer,
    ContractAircraftCategory Category,
    int Seats,
    int PayloadKg,
    int RangeNm,
    int CruiseTasKts,
    string MatchPatterns,
    SimAircraftAvailability ShipsWith,
    IReadOnlyList<string> BasePackageIds);

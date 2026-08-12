using FSOps.Core.Planning;

namespace FSOps.Server.Services;

/// <summary>
/// The result of one corroboration attempt for a tracked flight - see
/// <see cref="VatsimFlightCorroborationService.CheckAsync"/>.
/// </summary>
/// <param name="Matched">True when the configured CID was found online on the public feed AND its
/// reported position was within <see cref="VatsimFlightCorroborationService.MatchDistanceNm"/> of
/// FSOps' own telemetry sample at the time of the check. False covers every other case (CID not
/// online, feed unavailable, or online somewhere else entirely) - the caller folds this into a
/// running fraction rather than treating a single miss as decisive, since the feed only refreshes
/// every ~15 seconds and a miss is often just timing.</param>
/// <param name="Callsign">The callsign the CID was flying under, when found - regardless of whether
/// the position matched, so a caller can still record "seen as EZY123" even on a miss.</param>
/// <param name="DistanceNm">The distance between FSOps' own telemetry and the feed's reported
/// position, when the CID was found - null if the CID was not online at all.</param>
/// <param name="RelevantControllers">ATC callsigns currently online whose airport-local position
/// covers <c>departureIcao</c> or <c>arrivalIcao</c> - "controllers worked" for the report card.
/// Terminal positions only (tower/ground/delivery/approach), matching
/// <see cref="VatsimCallsigns.AirportIcaoFromCallsign"/>'s own airport-local rule; en-route sector
/// coverage is deliberately not attempted here (it would need
/// <c>IAtcBoundarySource</c>, which is a heavier dependency than this lightweight "worked" list is
/// worth carrying).</param>
public sealed record VatsimCorroborationCheck(
    bool Matched, string? Callsign, double? DistanceNm, IReadOnlyList<string> RelevantControllers);

/// <summary>
/// Corroborates a tracked flight against the same public VATSIM feed <see cref="IVatsimNetworkClient"/>
/// already caches for the ATC layer - G8. This is corroboration, not a second source of truth:
/// FSOps' own SimConnect telemetry stays authoritative for position, timing and landing quality:
/// this service never reports a position of its own, it only asks "does the feed's own report of
/// this CID sit near where FSOps independently knows the aircraft to be, right now". A single
/// affirmative "the CID is online somewhere" is deliberately NOT enough - see <see cref="MatchDistanceNm"/>.
/// <para>
/// Shares <see cref="IVatsimNetworkClient"/>'s own cache/backoff (one fetch per ~20s refresh
/// interval, shared across every consumer, including the ATC layer) rather than fetching
/// independently - calling <see cref="IVatsimNetworkClient.GetSnapshotAsync"/> here costs nothing
/// extra when a poll for controllers already happened within the cache window, and this service
/// never talks to the network directly itself.
/// </para>
/// </summary>
public sealed class VatsimFlightCorroborationService
{
    // Feed positions refresh roughly every 15 seconds; an aircraft at typical cruise speed
    // (~250-500 kt) can cover 1-2 nm in that time, plus whatever lag the feed itself carries before
    // a fresh position lands. 20 nm is generous enough to absorb that lag without demanding
    // near-exact position agreement, while still meaning something - it rules out "the CID is
    // online somewhere in the world" (which the feed's mere presence check would let through) in
    // favour of "the CID is plausibly this aircraft, right now".
    internal const double MatchDistanceNm = 20.0;

    private const int ObserverFacilityId = 0;

    private readonly IVatsimNetworkClient _vatsim;

    public VatsimFlightCorroborationService(IVatsimNetworkClient vatsim)
    {
        _vatsim = vatsim;
    }

    /// <summary>
    /// One corroboration attempt: is <paramref name="cid"/> online right now, and if so, is its
    /// reported position near <paramref name="latitudeDeg"/>/<paramref name="longitudeDeg"/> (FSOps'
    /// own telemetry for the tracked flight at this moment)? Never throws on a feed problem - an
    /// unavailable feed reads as "not matched this check", exactly like every other VATSIM
    /// integration in this app failing soft rather than surfacing an error.
    /// </summary>
    public async Task<VatsimCorroborationCheck> CheckAsync(
        int cid, double latitudeDeg, double longitudeDeg, string? departureIcao, string? arrivalIcao, CancellationToken ct)
    {
        var snapshot = await _vatsim.GetSnapshotAsync(ct);
        if (!snapshot.Available)
        {
            return new VatsimCorroborationCheck(false, null, null, Array.Empty<string>());
        }

        string? callsign = null;
        double? distanceNm = null;
        var matched = false;

        var pilot = snapshot.Pilots.FirstOrDefault(p => p.Cid == cid);
        if (pilot is not null)
        {
            callsign = pilot.Callsign;
            distanceNm = GreatCircle.DistanceNm(latitudeDeg, longitudeDeg, pilot.LatitudeDeg, pilot.LongitudeDeg);
            matched = distanceNm <= MatchDistanceNm;
        }

        var relevantControllers = snapshot.Controllers
            .Where(c => c.FacilityId != ObserverFacilityId)
            .Select(c => (Controller: c, Icao: VatsimCallsigns.AirportIcaoFromCallsign(c.Callsign)))
            .Where(t => t.Icao is not null && (t.Icao == departureIcao || t.Icao == arrivalIcao))
            .Select(t => t.Controller.Callsign)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new VatsimCorroborationCheck(matched, callsign, distanceNm, relevantControllers);
    }
}

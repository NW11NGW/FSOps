using System.Xml.Linq;
using FSOps.Core.Planning;

namespace FSOps.Server.Services;

/// <summary>
/// Fetches the player's latest OFP from SimBrief's free, keyless dispatch API and turns it into a
/// FlightPlan for the sector they're about to fly - see docs/PLAN.md's "External tools and
/// interoperability" and "Flight plan import" notes. The endpoint identifies the player purely by
/// their SimBrief Pilot ID (UserSettings.SimBriefPilotId, set on the Settings page); there is no
/// OAuth and FSOps never sees a SimBrief password.
///
/// Every outcome here is a normal one that this method returns rather than throws for: no Pilot
/// ID configured, an unknown Pilot ID (SimBrief answers with HTTP 400 for both "no such user" and
/// "no plan on file"), a timeout, a malformed/unreadable response, or an OFP filed for a
/// different city pair than the route being flown - the built-in estimator is always the fallback
/// (see BuiltInFlightPlanProvider), so a SimBrief outage or a bad Pilot ID must never prevent a
/// flight from starting.
///
/// SERVER-SIDE ONLY: this class must never be reachable from the SPA - the whole point of routing
/// this through the server is the app's no-external-network rule (the UI also runs inside an
/// MSFS panel with no reliable internet, and a browser call to simbrief.com would be both a CORS
/// problem and a privacy one). FlightEndpoints calls this; nothing under fsops-web ever does.
///
/// A Pilot ID is a user identifier - it appears in the outbound request URL to SimBrief (the
/// API's own contract - there is no other way to identify the player to it), but it is never
/// logged and never included in any exception message or response this class returns.
/// </summary>
public sealed class SimBriefFlightPlanProvider : IFlightPlanProvider
{
    private const string FetchUrlTemplate = "https://www.simbrief.com/api/xml.fetcher.php?userid={0}";
    private const double PoundsToKg = 0.45359237;

    // A single long-lived static HttpClient, shared across requests - the documented alternative
    // to IHttpClientFactory for a low-frequency outbound call like this one (at most a few times
    // per flight), avoiding both per-call socket exhaustion and a DI registration this endpoint
    // doesn't otherwise need. The constructor also accepts an HttpClient so tests can substitute a
    // fake handler and exercise every failure path without ever reaching simbrief.com.
    private static readonly HttpClient DefaultClient = new() { Timeout = RequestTimeout };

    private static TimeSpan RequestTimeout => TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;

    public SimBriefFlightPlanProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultClient;
    }

    public string Name => "SimBrief";

    public async Task<FlightPlanOutcome> GetPlanAsync(FlightPlanRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SimBriefPilotId))
        {
            return FlightPlanOutcome.Failed(Name, "No SimBrief Pilot ID is set - add one in Settings to pull real OFPs.");
        }

        var uri = new Uri(string.Format(FetchUrlTemplate, Uri.EscapeDataString(request.SimBriefPilotId)));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(uri, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The HttpClient's own Timeout fired, not the caller's cancellation token.
            return FlightPlanOutcome.Failed(Name, "SimBrief did not respond in time - using the built-in plan instead.");
        }
        catch (HttpRequestException)
        {
            return FlightPlanOutcome.Failed(Name, "Could not reach SimBrief - using the built-in plan instead.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // SimBrief answers HTTP 400 for both an unrecognised Pilot ID and a recognised one
                // with no flight plan on file - there's no reliable way to tell those apart from
                // the status code alone, so both read the same to the player.
                return FlightPlanOutcome.Failed(Name, "SimBrief has no flight plan for this Pilot ID - using the built-in plan instead.");
            }

            string xml;
            try
            {
                xml = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                return FlightPlanOutcome.Failed(Name, "Could not read SimBrief's response - using the built-in plan instead.");
            }

            return ParseOfp(xml, request);
        }
    }

    /// <summary>
    /// Pure parse of a SimBrief OFP XML document into a FlightPlan, or a failure outcome for
    /// anything that doesn't parse cleanly - malformed XML, a fetch that SimBrief itself reports
    /// as unsuccessful in the body, missing figures, or (the one FSOps must never silently ignore)
    /// a plan filed for a different city pair than the route actually being flown.
    /// </summary>
    private FlightPlanOutcome ParseOfp(string xml, FlightPlanRequest request)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException)
        {
            return FlightPlanOutcome.Failed(Name, "SimBrief returned an unreadable flight plan - using the built-in plan instead.");
        }

        var root = doc.Root;
        if (root is null)
        {
            return FlightPlanOutcome.Failed(Name, "SimBrief returned an empty response - using the built-in plan instead.");
        }

        var fetchStatus = (string?)root.Element("fetch")?.Element("status");
        if (!string.Equals(fetchStatus, "Success", StringComparison.OrdinalIgnoreCase))
        {
            return FlightPlanOutcome.Failed(Name, "SimBrief has no flight plan for this Pilot ID - using the built-in plan instead.");
        }

        var originIcao = (string?)root.Element("origin")?.Element("icao_code");
        var destinationIcao = (string?)root.Element("destination")?.Element("icao_code");
        if (string.IsNullOrWhiteSpace(originIcao) || string.IsNullOrWhiteSpace(destinationIcao))
        {
            return FlightPlanOutcome.Failed(Name, "SimBrief's plan is missing an origin or destination - using the built-in plan instead.");
        }

        // Never substitute a plan filed for a different route - the one absolute rule of this
        // import. Refuse politely and fall back rather than silently applying mismatched numbers.
        if (!string.Equals(originIcao, request.Departure.Icao, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(destinationIcao, request.Arrival.Icao, StringComparison.OrdinalIgnoreCase))
        {
            return FlightPlanOutcome.Failed(
                Name,
                $"Your latest SimBrief plan is for {originIcao}-{destinationIcao}, not {request.Departure.Icao}-{request.Arrival.Icao} - using the built-in plan instead.");
        }

        var routeString = (string?)root.Element("general")?.Element("route");
        var altitudeText = (string?)root.Element("general")?.Element("initial_altitude");
        var enrouteSecondsText = (string?)root.Element("times")?.Element("est_time_enroute");
        var rampFuelText = (string?)root.Element("fuel")?.Element("plan_ramp");
        var units = (string?)root.Element("params")?.Element("units");

        if (!int.TryParse(altitudeText, out var cruiseAltitudeFt) ||
            !double.TryParse(enrouteSecondsText, out var enrouteSeconds) ||
            !double.TryParse(rampFuelText, out var rampFuel))
        {
            return FlightPlanOutcome.Failed(Name, "SimBrief's plan is missing fuel, altitude, or time figures - using the built-in plan instead.");
        }

        // SimBrief reports weight in whichever unit the OFP was generated in (params/units is
        // "kgs" or "lbs") - FSOps' fuel figures are always kg (see FuelBreakdown), so convert
        // rather than assume. Defaults to treating the figure as already kg only if the units
        // field itself is missing, which is the more common/expected case in practice.
        var blockFuelKg = string.Equals(units, "lbs", StringComparison.OrdinalIgnoreCase)
            ? rampFuel * PoundsToKg
            : rampFuel;

        var blockTimeMinutes = (int)Math.Round(enrouteSeconds / 60.0, MidpointRounding.AwayFromZero);

        var plan = new FlightPlan(blockFuelKg, cruiseAltitudeFt, blockTimeMinutes, string.IsNullOrWhiteSpace(routeString) ? null : routeString);
        return FlightPlanOutcome.Succeeded(Name, plan, "Plan imported from SimBrief.");
    }
}

namespace FSOps.Server.Services;

/// <summary>
/// Shared callsign parsing for the VATSIM feed - pulled out of <c>VatsimEndpoints</c> so
/// <see cref="VatsimFlightCorroborationService"/> (which needs to know which controllers cover a
/// flight's departure/arrival airports, same as the ATC layer does) doesn't reimplement it
/// independently and risk drifting from the ATC layer's own rule for what counts as an
/// airport-local callsign.
/// </summary>
public static class VatsimCallsigns
{
    /// <summary>The callsign segment before the first underscore, when it looks like an ICAO code
    /// (exactly four letters). TRACON callsigns ("NY_APP") don't match and correctly resolve to no
    /// airport - see VatsimEndpoints' class doc.</summary>
    public static string? AirportIcaoFromCallsign(string callsign)
    {
        var separator = callsign.IndexOf('_');
        var candidate = separator < 0 ? callsign : callsign[..separator];
        return candidate.Length == 4 && candidate.All(char.IsAsciiLetterUpper) ? candidate : null;
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace FSOps.Core.Flights;

/// <summary>
/// Checks whether the aircraft loaded in the sim belongs to the same family as a route's expected
/// <c>AircraftType</c>. Informational only - see <c>Flight.TypeMismatch</c> - a mismatch is never
/// supposed to affect money or block anything, so this never throws: an unparsable pattern list or
/// a regex that fails to compile is treated as "no match" rather than as an error.
/// </summary>
public static class AircraftTypeMatcher
{
    /// <summary>
    /// True if the sim has told us anything at all about the loaded aircraft - a non-blank TITLE or
    /// ATC MODEL. False means there is nothing to check against (sim not connected, or no aircraft
    /// loaded yet), which callers must report as unknown rather than passing it to
    /// <see cref="IsMatch"/> and treating a "no" answer as a mismatch - see
    /// <c>Flight.TypeMismatch</c>.
    /// </summary>
    public static bool HasAircraftData(string? aircraftTitle, string? atcModel) =>
        !string.IsNullOrWhiteSpace(aircraftTitle) || !string.IsNullOrWhiteSpace(atcModel);

    /// <param name="matchPatternsJson"><c>AircraftType.MatchPatterns</c> - a JSON array of regex strings.</param>
    /// <param name="aircraftTitle">The sim's TITLE for the loaded aircraft.</param>
    /// <param name="atcModel">The sim's ATC MODEL for the loaded aircraft.</param>
    public static bool IsMatch(string matchPatternsJson, string? aircraftTitle, string? atcModel)
    {
        if (string.IsNullOrWhiteSpace(aircraftTitle) && string.IsNullOrWhiteSpace(atcModel))
        {
            return false;
        }

        string[] patterns;
        try
        {
            patterns = JsonSerializer.Deserialize<string[]>(matchPatternsJson) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (MatchesEither(pattern, aircraftTitle, atcModel))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesEither(string pattern, string? title, string? atcModel)
    {
        try
        {
            if (!string.IsNullOrEmpty(title) && Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(atcModel) && Regex.IsMatch(atcModel, pattern, RegexOptions.IgnoreCase))
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            // Malformed pattern in the data - never let a bad regex crash flight tracking.
        }

        return false;
    }
}

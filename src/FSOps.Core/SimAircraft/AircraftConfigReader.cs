namespace FSOps.Core.SimAircraft;

/// <summary>What one <c>aircraft.cfg</c> says about the aircraft it configures.</summary>
/// <param name="TypeDesignator">The <c>icao_type_designator</c> from [GENERAL], raw.</param>
/// <param name="AtcModel">The <c>atc_model</c> from [GENERAL], raw. Often a localisation token.</param>
/// <param name="Title">The first [FLTSIM.n] <c>title</c>, raw.</param>
/// <param name="IsAiTrafficOnly">
/// True when every [FLTSIM.n] entry in the file is an AI-traffic model the player cannot select.
/// <b>This is the difference between an aircraft somebody can fly and one they can only look at.</b>
/// FSLTL's traffic base declares <c>content_type: "AIRCRAFT"</c> and ships 2,551 aircraft
/// configurations; treating those as owned aircraft would put a "Generic Quad Jet Airliner" in the
/// player's hangar and hand them contracts for it.
/// </param>
public sealed record AircraftConfig(
    string? TypeDesignator,
    string? AtcModel,
    string? Title,
    bool IsAiTrafficOnly);

/// <summary>
/// Reads the handful of keys FSOps needs out of an MSFS <c>aircraft.cfg</c>.
///
/// <para>Deliberately a tolerant line reader rather than a real INI parser. These files are written
/// by dozens of different developers and tools: keys appear with and without quotes, with and
/// without spaces around the equals sign, with trailing <c>; comments</c>, commented out entirely,
/// and in MSFS 2024's modular format the interesting keys can be split across a package's
/// <c>common</c>, <c>presets</c> and <c>attachments</c> configs. A parser that rejected anything
/// malformed would find nothing in real folders, so this takes what it recognises and ignores
/// everything else. It never throws on content.</para>
/// </summary>
public static class AircraftConfigReader
{
    /// <summary>
    /// Configs are small - a few kilobytes - but a corrupt or hostile file should not be able to
    /// pull an unbounded amount of text into memory during a scan of somebody's whole sim folder.
    /// </summary>
    private const int MaxLines = 4000;

    public static AircraftConfig Parse(IEnumerable<string> lines)
    {
        string? designator = null;
        string? atcModel = null;
        string? title = null;

        var fltsimEntries = 0;
        var selectableEntries = 0;
        var inFltsim = false;
        var currentEntryIsSelectable = true;

        var read = 0;
        foreach (var rawLine in lines)
        {
            if (++read > MaxLines)
            {
                break;
            }

            var line = StripComment(rawLine);
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                // Closing off the previous [FLTSIM.n] before moving on: an entry with neither flag
                // set is selectable, which is the common case and must not be counted as AI.
                if (inFltsim && currentEntryIsSelectable)
                {
                    selectableEntries++;
                }

                inFltsim = line.StartsWith("[FLTSIM", StringComparison.OrdinalIgnoreCase);
                if (inFltsim)
                {
                    fltsimEntries++;
                    currentEntryIsSelectable = true;
                }

                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());

            if (inFltsim)
            {
                if (key.Equals("title", StringComparison.OrdinalIgnoreCase) && title is null && value.Length > 0)
                {
                    title = value;
                }
                else if (key.Equals("isUserSelectable", StringComparison.OrdinalIgnoreCase) && value == "0")
                {
                    currentEntryIsSelectable = false;
                }
                else if (key.Equals("isAirTraffic", StringComparison.OrdinalIgnoreCase) && value == "1")
                {
                    currentEntryIsSelectable = false;
                }

                continue;
            }

            if (key.Equals("icao_type_designator", StringComparison.OrdinalIgnoreCase) && designator is null && value.Length > 0)
            {
                designator = value;
            }
            else if (key.Equals("atc_model", StringComparison.OrdinalIgnoreCase) && atcModel is null && value.Length > 0)
            {
                atcModel = value;
            }
        }

        if (inFltsim && currentEntryIsSelectable)
        {
            selectableEntries++;
        }

        // A config with no [FLTSIM.n] at all is not AI traffic - it is one of MSFS 2024's modular
        // fragments (the Fenix A320 declares its type designator in an attachment config that has no
        // FLTSIM section at all). Only a file that has variations, all of them unselectable, is.
        var aiOnly = fltsimEntries > 0 && selectableEntries == 0;

        return new AircraftConfig(designator, atcModel, title, aiOnly);
    }

    private static string StripComment(string line)
    {
        var semicolon = line.IndexOf(';');
        var text = semicolon >= 0 ? line[..semicolon] : line;
        return text.Trim();
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Trim();
    }
}

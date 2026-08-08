using System.Text.RegularExpressions;

namespace FSOps.Core.Routes;

/// <summary>
/// Suggests flight numbers for routes when the user doesn't supply one, so every route the
/// server creates already has something an OFP, an aircraft FMS and SimBrief can agree on.
/// <para>
/// Scheme: an airline's ICAO code deterministically picks which "hundred" series it counts
/// from (so two different airlines don't both cluster around flight 100/101), and within that
/// series outbound legs are always odd, mirroring the common real-world airline convention of
/// pairing an odd outbound number with the next even number for the return leg (e.g. an
/// outbound 101 pairs with a return 102). Both suggestion methods walk forward in steps of two
/// past anything already taken, so the result never collides with an existing route.
/// </para>
/// Deliberately pure and side-effect free - no database access - so it's cheap to unit test and
/// safe to call from a request handler that has already loaded the airline and its routes.
/// </summary>
public static class FlightNumberGenerator
{
    /// <summary>Matches the numeric part with an optional single trailing letter, e.g. "101" or "204A".</summary>
    public static readonly Regex ValidFormat = new("^[0-9]{1,4}[A-Z]?$", RegexOptions.Compiled);

    private const int SeriesWidth = 100;
    private const int SeriesCount = 8;

    /// <summary>
    /// Suggests the next free odd outbound number for the given airline, seeded from its ICAO
    /// code so different airlines don't collide even before either has any routes.
    /// </summary>
    /// <param name="airlineIcaoCode">The owning airline's ICAO code.</param>
    /// <param name="existingFlightNumbers">Flight numbers already in use anywhere on this airline.</param>
    public static string SuggestOutbound(string airlineIcaoCode, IEnumerable<string?> existingFlightNumbers)
    {
        var taken = ParseNumericParts(existingFlightNumbers);
        var seriesBase = SeriesBaseFor(airlineIcaoCode);

        var candidate = seriesBase;
        while (taken.Contains(candidate))
        {
            candidate += 2;
        }

        return candidate.ToString();
    }

    /// <summary>
    /// Suggests a number for the return leg of <paramref name="outboundFlightNumber"/>: the next
    /// even number after it, or the next free even number past that if it's already taken. Falls
    /// back to <see cref="SuggestOutbound"/> if the outbound number can't be parsed (e.g. it was
    /// hand-entered in a format that has no obvious "next" number).
    /// </summary>
    public static string SuggestReturn(string airlineIcaoCode, string? outboundFlightNumber, IEnumerable<string?> existingFlightNumbers)
    {
        var existing = existingFlightNumbers as ICollection<string?> ?? existingFlightNumbers.ToList();

        if (!TryParseNumericPart(outboundFlightNumber, out var outboundNumber))
        {
            return SuggestOutbound(airlineIcaoCode, existing);
        }

        var taken = ParseNumericParts(existing);
        var candidate = outboundNumber + 1;
        while (taken.Contains(candidate))
        {
            candidate += 2;
        }

        return candidate.ToString();
    }

    /// <summary>True when <paramref name="flightNumber"/> is digits with an optional single trailing letter.</summary>
    public static bool IsValidFormat(string flightNumber) => ValidFormat.IsMatch(flightNumber);

    /// <summary>
    /// Deterministic starting number for an airline's series: the ICAO code's letters are
    /// summed and folded into one of <see cref="SeriesCount"/> hundred-blocks between 100 and
    /// 800, then nudged to the nearest odd number so outbound suggestions always start odd.
    /// </summary>
    private static int SeriesBaseFor(string icaoCode)
    {
        var sum = 0;
        foreach (var c in icaoCode.ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z')
            {
                sum += c - 'A' + 1;
            }
        }

        var series = sum % SeriesCount;
        var seriesBase = SeriesWidth + series * SeriesWidth + 1; // +1 keeps every series base odd
        return seriesBase;
    }

    private static HashSet<int> ParseNumericParts(IEnumerable<string?> flightNumbers)
    {
        var set = new HashSet<int>();
        foreach (var flightNumber in flightNumbers)
        {
            if (TryParseNumericPart(flightNumber, out var n))
            {
                set.Add(n);
            }
        }

        return set;
    }

    private static bool TryParseNumericPart(string? flightNumber, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(flightNumber))
        {
            return false;
        }

        var digits = new string(flightNumber.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out number);
    }
}

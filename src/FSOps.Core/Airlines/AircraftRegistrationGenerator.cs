namespace FSOps.Core.Airlines;

/// <summary>
/// Generates a plausible tail number for a new airline's starter aircraft, based on the ISO
/// country code of its home airport. This is a small illustrative mapping, not an aviation
/// registry - real registration formats vary far more than this (letter/number mixes, block
/// allocations, etc.), so unmapped countries fall back to a generic FS- prefix.
/// </summary>
public static class AircraftRegistrationGenerator
{
    private static readonly Dictionary<string, string> PrefixByCountry = new()
    {
        ["GB"] = "G-",
        ["US"] = "N",
        ["FR"] = "F-",
        ["DE"] = "D-",
        ["IE"] = "EI-",
        ["NL"] = "PH-",
        ["ES"] = "EC-",
        ["IT"] = "I-",
        ["CA"] = "C-",
        ["AU"] = "VH-",
        ["CH"] = "HB-",
        ["SE"] = "SE-",
        ["NO"] = "LN-",
        ["PL"] = "SP-",
        ["BR"] = "PP-",
        ["ZA"] = "ZS-",
        ["JP"] = "JA",
        ["CN"] = "B-",
        ["NZ"] = "ZK-",
        ["PT"] = "CS-",
        ["BE"] = "OO-",
        ["AT"] = "OE-",
        ["DK"] = "OY-",
        ["FI"] = "OH-",
        ["GR"] = "SX-",
        ["IN"] = "VT-",
        ["MX"] = "XA-",
        ["AE"] = "A6-",
        ["SG"] = "9V-",
        ["HK"] = "B-",
    };

    private const string FallbackPrefix = "FS-";

    public static string Generate(string? countryCode, string airlineIcaoCode)
    {
        var prefix = countryCode is not null && PrefixByCountry.TryGetValue(countryCode.ToUpperInvariant(), out var mapped)
            ? mapped
            : FallbackPrefix;

        var basis = (airlineIcaoCode + "FSOPS").ToUpperInvariant();
        var letters = new string(basis.Where(char.IsLetter).Take(4).ToArray()).PadRight(4, 'X');

        return prefix + letters;
    }
}

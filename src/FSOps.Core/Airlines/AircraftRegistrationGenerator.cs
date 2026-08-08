namespace FSOps.Core.Airlines;

/// <summary>
/// Generates an individual, format-correct tail number for one fleet aircraft, based on the ISO
/// country code of the airline's HOME AIRPORT at the moment of acquisition - never the aircraft's
/// current location (see docs/PLAN.md "Registrations - real tail numbers, and let the player
/// choose"). Each mapped country carries its own registration FORMAT, not just a prefix, because
/// real formats genuinely differ in shape: most of Europe is a fixed prefix plus a fixed number of
/// letters, but the US "N-number" is a hyphen-less 1-5 character code that must start with a digit.
/// A single "prefix + 4 letters" template (the old design) cannot express the US rule at all, which
/// is why it used to produce invalid registrations like "NOLAF".
///
/// <para>Uniqueness within a fleet is deliberately NOT this type's job - see
/// FleetEndpoints.ResolveRegistrationAsync, which calls <see cref="Generate"/> repeatedly with a
/// fresh random draw each time until it lands on one the fleet doesn't already have. This type only
/// ever produces one independently-random, format-correct candidate per call; regenerating rather
/// than appending a numeric suffix is what keeps every result a real, plausible registration
/// (suffixing produced "G-OLAF1", which exists in no registry).</para>
/// </summary>
public static class AircraftRegistrationGenerator
{
    private const string LetterAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // Excludes I and O, matching the real FAA N-number convention (too easily confused with 1 and
    // 0 in the cockpit/on a strip) - applied here for realism even though nothing downstream
    // actually depends on it.
    private const string UsLetterAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ";

    private const string UsCountryCode = "US";

    /// <summary>
    /// Real published registration format per ISO country code - see the class doc for why a bare
    /// prefix isn't enough. Still a small illustrative mapping, not an aviation registry: unmapped
    /// countries fall back to <see cref="FallbackFormat"/>.
    /// </summary>
    private static readonly Dictionary<string, RegistrationFormat> FormatByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        // The six formats docs/PLAN.md calls out explicitly.
        ["GB"] = new("G-", 4),
        ["DE"] = new("D-A", 3),
        ["FR"] = new("F-G", 3),
        ["IE"] = new("EI-", 3),
        ["NL"] = new("PH-", 3),
        ["ES"] = new("EC-", 3),
        // Everything else the old mapping covered, upgraded from one generic "4 letters" guess to
        // each country's real published body shape where it's straightforward.
        ["IT"] = new("I-", 4),
        ["CA"] = new("C-F", 3),
        ["AU"] = new("VH-", 3),
        ["CH"] = new("HB-", 3),
        ["SE"] = new("SE-", 3),
        ["NO"] = new("LN-", 3),
        ["PL"] = new("SP-", 3),
        ["BR"] = new("PP-", 3),
        ["ZA"] = new("ZS-", 3),
        ["JP"] = new("JA", 4, BodyIsDigits: true),
        ["CN"] = new("B-", 4, BodyIsDigits: true),
        ["NZ"] = new("ZK-", 3),
        ["PT"] = new("CS-", 3),
        ["BE"] = new("OO-", 3),
        ["AT"] = new("OE-", 3),
        ["DK"] = new("OY-", 3),
        ["FI"] = new("OH-", 3),
        ["GR"] = new("SX-", 3),
        ["IN"] = new("VT-", 3),
        ["MX"] = new("XA-", 3),
        ["AE"] = new("A6-", 3),
        ["SG"] = new("9V-", 3),
        ["HK"] = new("B-H", 2),
    };

    private static readonly RegistrationFormat FallbackFormat = new("FS-", 4);

    /// <summary>
    /// Produces one plausible, randomly-generated, format-correct registration for the given ISO
    /// country code. NOT guaranteed unique within any fleet - see the class doc. <paramref
    /// name="random"/> defaults to <see cref="Random.Shared"/>; tests that need a deterministic
    /// result can pass a seeded instance.
    /// </summary>
    public static string Generate(string? countryCode, Random? random = null)
    {
        random ??= Random.Shared;

        if (string.Equals(countryCode, UsCountryCode, StringComparison.OrdinalIgnoreCase))
        {
            return GenerateUsNNumber(random);
        }

        var format = countryCode is not null && FormatByCountry.TryGetValue(countryCode, out var mapped)
            ? mapped
            : FallbackFormat;

        var alphabet = format.BodyIsDigits ? "0123456789" : LetterAlphabet;
        var body = new string(Enumerable.Range(0, format.BodyLength).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
        return format.Prefix + body;
    }

    /// <summary>
    /// A US N-number: "N" followed by 1 to 5 characters, the first of which must be a digit 1-9 (an
    /// N-number never starts with 0, and never starts with a letter - "NOLAF" is not a valid one).
    /// Real FAA numbers also never revert to digits after a letter appears, so this always produces
    /// a run of 1-4 digits optionally followed by 0-2 letters - realistic shapes like N737FS or
    /// N123AB, capped at 5 characters total after the N.
    /// </summary>
    private static string GenerateUsNNumber(Random random)
    {
        var digitCount = random.Next(1, 5); // 1-4 digits
        var digits = new string(Enumerable.Range(0, digitCount)
            .Select(i => (char)('0' + (i == 0 ? random.Next(1, 10) : random.Next(0, 10))))
            .ToArray());

        var remaining = 5 - digitCount;
        var maxLetters = Math.Min(remaining, 2);
        var letterCount = maxLetters <= 0 ? 0 : random.Next(0, maxLetters + 1);
        var letters = new string(Enumerable.Range(0, letterCount).Select(_ => UsLetterAlphabet[random.Next(UsLetterAlphabet.Length)]).ToArray());

        return "N" + digits + letters;
    }

    /// <summary>
    /// Light validation for a player-typed custom registration (buying, leasing, or renaming from
    /// the Fleet page) - docs/PLAN.md "Let the player set a custom registration": uppercase,
    /// letters/digits/hyphen only, a sensible length. Deliberately does NOT enforce any country's
    /// registry format - a player matching a specific repaint's real-world tail knows what they
    /// want better than a validator does; this only rejects what would break the app. Expects the
    /// caller to have already trimmed/upper-cased <paramref name="registration"/> (see
    /// FleetEndpoints.NormalizeRegistration) so this is a pure predicate over the final value.
    /// </summary>
    public static bool IsValidCustomRegistration(string registration) =>
        registration.Length is >= 2 and <= 10 && registration.All(c => (c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9') || c == '-');
}

/// <summary>
/// One country's registration shape - see <see cref="AircraftRegistrationGenerator"/>'s class doc.
/// <paramref name="BodyLength"/> is how many characters follow <paramref name="Prefix"/>;
/// <paramref name="BodyIsDigits"/> switches the body alphabet from letters to digits (Japan and
/// China register airliners with a numeric suffix rather than a letter one).
/// </summary>
public sealed record RegistrationFormat(string Prefix, int BodyLength, bool BodyIsDigits = false);

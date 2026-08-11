using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FSOps.Server.Services;

/// <summary>
/// A semantic version (semver 2.0.0) with real precedence rules, used to decide whether a GitHub
/// release is actually newer than the running build.
/// <para>
/// String comparison is not an acceptable substitute and this type exists precisely to stop anyone
/// reaching for it: ordinally, "0.10.0" sorts BEFORE "0.9.0", so a string-compared updater would go
/// permanently silent at the exact moment the minor version reached double digits - a bug that
/// would not show up in testing for months and then never announce itself.
/// </para>
/// <para>
/// Pre-release precedence is implemented in full (1.0.0-alpha &lt; 1.0.0-alpha.1 &lt; 1.0.0-beta &lt;
/// 1.0.0) because the updater has to be able to recognise a pre-release in order to refuse to offer
/// it. Build metadata after '+' is ignored for precedence, exactly as the spec requires.
/// </para>
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The dot-separated pre-release identifiers ("beta.2"), or null for a stable release.</summary>
    public string? Prerelease { get; }

    /// <summary>True for anything carrying a pre-release tag - never offered as an update.</summary>
    public bool IsPrerelease => Prerelease is not null;

    /// <summary>
    /// Parses "1.2.3", "v1.2.3", "1.2.3-beta.1", "1.2.3+build" and the leading-"v" tag form GitHub
    /// releases conventionally use. A missing patch or minor component is treated as zero so a tag
    /// like "v2" or "v1.4" still compares sensibly rather than silently failing to parse and
    /// disabling the whole check.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        // Build metadata never participates in precedence, so it is discarded before anything else.
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        string? prerelease = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length is 0 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (i >= parts.Length)
            {
                numbers[i] = 0;
                continue;
            }

            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            numbers[i] = parsed;
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;

        var byMinor = Minor.CompareTo(other.Minor);
        if (byMinor != 0) return byMinor;

        var byPatch = Patch.CompareTo(other.Patch);
        if (byPatch != 0) return byPatch;

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    /// <summary>
    /// Semver 2.0.0 §11.3-11.4: a version WITH a pre-release tag has lower precedence than the same
    /// version without one; identifiers are compared left to right, numeric ones numerically,
    /// everything else ASCII-ordinally, numeric always ranking below alphanumeric; and when one
    /// side runs out of identifiers first, the shorter one is lower.
    /// </summary>
    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');

        for (var i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
        {
            if (i >= leftParts.Length) return -1;
            if (i >= rightParts.Length) return 1;

            var leftIsNumeric = int.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightIsNumeric = int.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            if (leftIsNumeric && rightIsNumeric)
            {
                var byNumber = leftNumber.CompareTo(rightNumber);
                if (byNumber != 0) return byNumber;
                continue;
            }

            if (leftIsNumeric) return -1;
            if (rightIsNumeric) return 1;

            var byText = string.CompareOrdinal(leftParts[i], rightParts[i]);
            if (byText != 0) return byText < 0 ? -1 : 1;
        }

        return 0;
    }

    public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);
}

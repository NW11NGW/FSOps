using System.Reflection;

namespace FSOps.Server.Services;

/// <summary>
/// The single source of truth for "what version of FSOps is this". Reads the assembly's
/// informational version, which comes from &lt;Version&gt; in Directory.Build.props, so the number
/// is declared once in the build rather than retyped in every place that wants to display it.
/// <para>
/// The fallback is not defensive padding: a single-file or trimmed publish can strip informational
/// version metadata, and a null version here would make the update checker unable to compare
/// anything at all. A stale-but-parseable constant degrades far better than that - the worst case
/// is one spurious "update available" for a release that is actually already installed, which the
/// user can simply ignore.
/// </para>
/// <para>
/// The informational version can carry build metadata that MSBuild appends automatically
/// (e.g. "0.1.0+9a3f21c" from SourceLink), so everything from the first '+' is trimmed off before
/// the value is used or displayed.
/// </para>
/// <para>
/// <b>This depends on &lt;Version&gt; being set in Directory.Build.props.</b> With no &lt;Version&gt;
/// property, MSBuild emits an informational version of "1.0.0" - a default that looks exactly like a
/// deliberate answer and cannot be told apart from a genuine 1.0.0 release. That would make the
/// update checker believe the app is newer than every release below 1.0.0 and go permanently quiet.
/// Keep the &lt;Version&gt; property in step with the release tag.
/// </para>
/// </summary>
public static class AppVersion
{
    /// <summary>Used when the assembly carries no usable version metadata - see the class doc.</summary>
    public const string Fallback = "0.1.0";

    private static readonly Lazy<string> CurrentLazy = new(() =>
        Resolve(typeof(AppVersion).Assembly));

    /// <summary>The running build's version, e.g. "0.1.0". Never null or empty.</summary>
    public static string Current => CurrentLazy.Value;

    /// <summary>
    /// Kept internal and parameterised so the resolution rules can be tested against a supplied
    /// assembly instead of depending on how the test host itself happens to be versioned.
    /// </summary>
    internal static string Resolve(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var trimmed = StripBuildMetadata(informational);
        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        // AssemblyVersion is always present, but is 1.0.0.0 when no <Version> was set and 0.0.0.0
        // for an assembly emitted at runtime. Both are placeholders rather than answers, and acting
        // on either would be worse than admitting we do not know: 1.0.0.0 would make the app believe
        // it is newer than every release below 1.0.0 and go permanently quiet, and 0.0.0.0 would
        // make it believe every release is an upgrade.
        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null &&
            assemblyVersion != new Version(1, 0, 0, 0) &&
            assemblyVersion != new Version(0, 0, 0, 0))
        {
            return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        }

        return Fallback;
    }

    private static string? StripBuildMetadata(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var plus = version.IndexOf('+');
        var trimmed = (plus < 0 ? version : version[..plus]).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}

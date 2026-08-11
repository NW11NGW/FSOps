namespace FSOps.Desktop;

/// <summary>
/// Finds FSOps.Server.exe. Pure - the file system arrives through a delegate - so the candidate
/// order is unit-testable.
///
/// <para>
/// In every shipped layout the answer is "right next to me": scripts/publish.ps1 publishes the
/// server and the shell into one folder, and the installer copies that folder verbatim. The
/// remaining candidates exist so the shell is runnable from its own bin folder during development,
/// where FSOps.Server builds into a sibling project's output instead. That fallback must never be
/// the first thing tried, or a stale dev build would win over the installed one.
/// </para>
/// </summary>
internal static class ServerExecutableLocator
{
    public const string ExecutableName = "FSOps.Server.exe";

    /// <summary>Escape hatch for an unusual layout; checked first when set.</summary>
    public const string OverrideVariable = "FSOPS_SERVER_EXE";

    /// <summary>Marker that identifies the repository root when walking up from a dev bin folder.</summary>
    private const string RepositoryMarker = "FSOps.sln";

    private const int MaxParentLevels = 8;

    /// <summary>
    /// Candidate paths in priority order. <paramref name="configuration"/> is the build
    /// configuration name used by the development fallbacks ("Debug" / "Release").
    /// </summary>
    public static IEnumerable<string> CandidatePaths(
        string baseDirectory,
        string configuration,
        string? overridePath,
        Func<string, bool> directoryExists)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return Path.GetFullPath(overridePath.Trim());
        }

        // Published, installed, and "everything in one folder" layouts all land here.
        yield return Path.Combine(baseDirectory, ExecutableName);

        // Development: src/FSOps.Desktop/bin/<cfg>/net8.0-windows/ -> repo root -> the server's own
        // build output. Walk up looking for the solution file rather than counting directory levels,
        // because the artifacts-path builds other agents use change the depth.
        var directory = baseDirectory;
        for (var level = 0; level < MaxParentLevels; level++)
        {
            var parent = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            directory = parent;

            if (!File.Exists(Path.Combine(directory, RepositoryMarker)))
            {
                continue;
            }

            var serverBin = Path.Combine(directory, "src", "FSOps.Server", "bin");
            if (!directoryExists(serverBin))
            {
                break;
            }

            // Prefer the configuration the shell itself was built in, then the other one - a
            // developer running a Debug shell against a Release server is unusual but harmless.
            yield return Path.Combine(serverBin, configuration, "net8.0", ExecutableName);
            yield return Path.Combine(serverBin, configuration == "Debug" ? "Release" : "Debug", "net8.0", ExecutableName);
            break;
        }
    }

    /// <summary>The first candidate that exists, or <c>null</c> when the server is genuinely missing.</summary>
    public static string? Locate(
        string baseDirectory,
        string configuration,
        string? overridePath,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        foreach (var candidate in CandidatePaths(baseDirectory, configuration, overridePath, directoryExists))
        {
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Convenience overload that reads the real file system and environment.</summary>
    public static string? Locate() => Locate(
        AppContext.BaseDirectory,
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        Environment.GetEnvironmentVariable(OverrideVariable),
        File.Exists,
        Directory.Exists);
}

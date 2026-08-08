namespace FSOps.Core;

/// <summary>
/// Resolves every path FSOps writes to at runtime. The app installs into Program Files,
/// which is read-only for standard users, so nothing may ever be written next to the
/// executable - the database, logs, and any future config all live under the current
/// user's LocalAppData instead. Every path here is created on first access so callers
/// never have to remember to call Directory.CreateDirectory themselves.
/// <para>
/// Set the FSOPS_DATA_DIR environment variable to point the whole data directory
/// somewhere else. Automated testing must always use this so that a test run can never
/// read or destroy the real save in LocalAppData.
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>Environment variable that redirects <see cref="DataDirectory"/>.</summary>
    public const string DataDirectoryOverrideVariable = "FSOPS_DATA_DIR";

    private static readonly Lazy<string> DataDirectoryLazy = new(() =>
    {
        var path = ResolveDataDirectory();
        Directory.CreateDirectory(path);
        return path;
    });

    private static string ResolveDataDirectory() =>
        ResolveDataDirectoryFrom(Environment.GetEnvironmentVariable(DataDirectoryOverrideVariable));

    /// <summary>
    /// Works out the data directory from a supplied override value. Kept pure and public so it
    /// can be tested without mutating process-wide environment state.
    /// </summary>
    public static string ResolveDataDirectoryFrom(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            // Relative overrides would resolve against the working directory, which differs
            // between dotnet run, a launched executable and a test host.
            return Path.GetFullPath(overridePath.Trim());
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FSOps");
    }

    /// <summary>True when an override is redirecting the data directory away from LocalAppData.</summary>
    public static bool IsOverridden =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirectoryOverrideVariable));

    private static readonly Lazy<string> LogsDirectoryLazy = new(() =>
    {
        var path = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(path);
        return path;
    });

    /// <summary>%LOCALAPPDATA%\FSOps\ - created if missing.</summary>
    public static string DataDirectory => DataDirectoryLazy.Value;

    /// <summary>%LOCALAPPDATA%\FSOps\fsops.db</summary>
    public static string DatabasePath => Path.Combine(DataDirectory, "fsops.db");

    /// <summary>%LOCALAPPDATA%\FSOps\logs\ - created if missing.</summary>
    public static string LogsDirectory => LogsDirectoryLazy.Value;
}

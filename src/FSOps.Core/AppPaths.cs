namespace FSOps.Core;

/// <summary>
/// Resolves every path FSOps writes to at runtime. The app installs into Program Files,
/// which is read-only for standard users, so nothing may ever be written next to the
/// executable - the database, logs, and any future config all live under the current
/// user's LocalAppData instead. Every path here is created on first access so callers
/// never have to remember to call Directory.CreateDirectory themselves.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> DataDirectoryLazy = new(() =>
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FSOps");
        Directory.CreateDirectory(path);
        return path;
    });

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

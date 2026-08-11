using System.Text.Json;
using System.Text.RegularExpressions;

namespace FSOps.Server.Services;

/// <summary>
/// Detects, validates, installs, repairs and uninstalls the FSOps in-game panel package (see
/// src/fsops-ingame-panel/README.md) into a player-chosen MSFS Community folder. Pure file-system
/// logic with no database or DI dependency - every path is passed in explicitly - so it can be
/// exercised directly against a throwaway temp directory in tests, never against a real Community
/// folder.
///
/// <para>
/// docs/PLAN.md "The Community folder is captured at onboarding and reused to install the panel":
/// detect first, ask second; validate what the player chooses and explain a refusal; install,
/// update and repair are one operation, safe to run repeatedly; a version stamp in the package lets
/// the app tell whether what's on disk matches what it expects; never write outside the folder the
/// player nominated.
/// </para>
/// </summary>
public static class PanelPackageInstaller
{
    /// <summary>
    /// Must match package/manifest.json's "package_version" in src/fsops-ingame-panel. Bump both
    /// together whenever the panel template's content changes, so InstalledVersion vs
    /// ExpectedVersion actually means something.
    /// </summary>
    public const string ExpectedPanelVersion = "1.0.0";

    /// <summary>
    /// Fixed sub-folder name the panel always lives under, directly inside the player's Community
    /// folder. Every install/update/repair/uninstall operation targets exactly this path and
    /// nothing else in Community - see ValidateWriteTarget.
    /// </summary>
    public const string PackageFolderName = "fsops-panel";

    /// <summary>FSOps' documented default port - Program.cs uses the identical fallback.</summary>
    public const string DefaultPort = "5977";

    /// <summary>Same lookup Program.cs performs for Kestrel's own binding, so the panel and the
    /// server it points at can never disagree about which port is "current".</summary>
    public static string ResolveConfiguredPort() =>
        Environment.GetEnvironmentVariable("FSOPS_PORT") ?? DefaultPort;

    // ---------------------------------------------------------------------------------------
    // Detection
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Best-effort scan for MSFS 2024 Community folders. Returns an empty list rather than
    /// throwing when nothing is found - "detection will not always succeed" (docs/PLAN.md), and a
    /// missing MSFS install is not an error, it's the honest answer.
    ///
    /// Two independent, conflicting descriptions of the Microsoft Store package family name turned
    /// up during research ("Microsoft.Limitless_8wekyb3d8bbwe" per an official forum reply,
    /// "Microsoft.FlightSimulator_8wekyb3d8bbwe" - the MSFS-2020-era name - per other community
    /// reports of the same folder). Rather than hardcode either one and risk it being stale or
    /// simply wrong, this scans %LOCALAPPDATA%\Packages for anything matching either prefix. The
    /// most reliable source either way is UserCfg.opt's InstalledPackagesPath line (the same file
    /// the plan's architecture section already names), which is tried first for every install this
    /// finds and works regardless of package family naming.
    /// </summary>
    public static IReadOnlyList<PanelCandidate> DetectCommunityFolderCandidates()
    {
        var candidates = new List<PanelCandidate>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // Steam / non-Store install - MSFS 2024 keeps its config and package roots directly under
        // Roaming AppData (a change from MSFS 2020's split locations).
        var steamRoot = Path.Combine(roamingAppData, "Microsoft Flight Simulator 2024");
        TryAddFromUserCfg(candidates, Path.Combine(steamRoot, "UserCfg.opt"), "UserCfg.opt (Steam / non-Store install)");
        TryAddCandidate(candidates, Path.Combine(steamRoot, "Packages", "Community"), "Default Steam / non-Store location");

        // Microsoft Store / Xbox install - packaged apps keep writable data under a per-package-
        // family folder in Local AppData. Scan for the family rather than assuming one exact name.
        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            IEnumerable<string> packageDirs;
            try
            {
                packageDirs = Directory.EnumerateDirectories(packagesRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                packageDirs = [];
            }

            foreach (var dir in packageDirs)
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("Microsoft.Limitless_", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Microsoft.FlightSimulator_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var localCache = Path.Combine(dir, "LocalCache");
                TryAddFromUserCfg(candidates, Path.Combine(localCache, "UserCfg.opt"), $"UserCfg.opt (Microsoft Store - {name})");
                TryAddCandidate(candidates, Path.Combine(localCache, "Packages", "Community"), $"Default Microsoft Store location ({name})");
            }
        }

        // Same path can legitimately be found twice (UserCfg.opt pointing at the default location
        // itself) - collapse duplicates, preferring whichever entry we can confirm exists.
        return candidates
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Exists).First())
            .OrderByDescending(c => c.Exists)
            .ThenBy(c => c.Source, StringComparer.Ordinal)
            .ToList();
    }

    private static void TryAddFromUserCfg(List<PanelCandidate> candidates, string userCfgPath, string source)
    {
        if (!File.Exists(userCfgPath))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(userCfgPath);
            var match = Regex.Match(text, "InstalledPackagesPath\\s+\"([^\"]+)\"");
            if (!match.Success)
            {
                return;
            }

            TryAddCandidate(candidates, Path.Combine(match.Groups[1].Value, "Community"), source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort detection only - an unreadable or locked config file just means this
            // particular candidate can't be offered, not that detection as a whole failed.
        }
    }

    private static void TryAddCandidate(List<PanelCandidate> candidates, string path, string source)
    {
        string resolved;
        try
        {
            resolved = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return;
        }

        candidates.Add(new PanelCandidate(resolved, source, Directory.Exists(resolved)));
    }

    // ---------------------------------------------------------------------------------------
    // Validation
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Validates a player-chosen path is genuinely a Community folder before anything is ever
    /// written to it. Refuses with a plain-English reason rather than accepting and breaking
    /// silently later (docs/PLAN.md: "this looks like the sim's install folder, not Community").
    /// </summary>
    public static PanelPathValidation ValidateCommunityFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PanelPathValidation(false, "Enter or choose a Community folder path.", null);
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return new PanelPathValidation(false, "That doesn't look like a valid folder path.", null);
        }

        if (Path.GetPathRoot(resolved) == resolved)
        {
            return new PanelPathValidation(false, "That's a drive root, not a Community folder.", null);
        }

        var folderName = new DirectoryInfo(resolved).Name;
        if (!string.Equals(folderName, "Community", StringComparison.OrdinalIgnoreCase))
        {
            return new PanelPathValidation(
                false,
                $"This looks like “{folderName}”, not a Community folder — MSFS's add-on " +
                "folder is always named “Community” (for example …\\Packages\\Community). " +
                "Point at that folder itself, not the simulator's install folder or a package inside it.",
                null);
        }

        // Defensive: refuse a Community folder that turns out to be FSOps' own install or data
        // directory (never realistic, but "never write outside that folder" cuts both ways).
        var appDirectory = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(resolved, appDirectory, StringComparison.OrdinalIgnoreCase) ||
            appDirectory.StartsWith(resolved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return new PanelPathValidation(false, "That's FSOps' own program folder, not a Community folder.", null);
        }

        return new PanelPathValidation(true, null, resolved);
    }

    // ---------------------------------------------------------------------------------------
    // Install / update / repair (one operation, safe to run repeatedly)
    // ---------------------------------------------------------------------------------------

    public static PanelOperationResult InstallOrRepair(string? path, string templateDirectory, string port)
    {
        var validation = ValidateCommunityFolder(path);
        if (!validation.Valid || validation.ResolvedPath is null)
        {
            return PanelOperationResult.Refused(validation.Reason ?? "Invalid Community folder.");
        }

        if (!Directory.Exists(templateDirectory))
        {
            return PanelOperationResult.Refused(
                "The panel package template is missing from this FSOps install. Reinstall FSOps, or report this as a bug.");
        }

        var target = ResolveWriteTarget(validation.ResolvedPath);
        if (target is null)
        {
            return PanelOperationResult.Refused("Refused to install: the resolved path escaped the chosen Community folder.");
        }

        // Repair = always start from a clean slate under our own sub-folder, so disk state can
        // never drift into a partial merge of old and new files. This never touches anything
        // outside <Community>\fsops-panel - see ResolveWriteTarget's containment check.
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);

        var filesWritten = CopyTemplateFiles(templateDirectory, target);
        RewritePortConfig(target, port);
        WriteLayoutJson(target);

        var installedVersion = ReadManifestVersion(target);
        var spbPresent = File.Exists(Path.Combine(target, "InGamePanels", "FSOpsPanel.spb"));

        return new PanelOperationResult(
            Success: true,
            Reason: null,
            Installed: true,
            InstalledPath: target,
            InstalledVersion: installedVersion,
            ExpectedVersion: ExpectedPanelVersion,
            SpbPresent: spbPresent,
            ToolbarWillAppearInSim: spbPresent,
            FilesWritten: filesWritten,
            Message: spbPresent
                ? "Panel installed. Restart MSFS if it's already running, then look for the FSOps icon in the toolbar."
                : "Panel files installed, but this build of FSOps doesn't yet include the compiled panel " +
                  "component the toolbar needs - the FSOps button will not appear in MSFS until a future " +
                  "update adds it.");
    }

    // ---------------------------------------------------------------------------------------
    // Status (read-only - never writes)
    // ---------------------------------------------------------------------------------------

    public static PanelOperationResult GetStatus(string? path, string port)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PanelOperationResult(
                true, null, false, null, null, ExpectedPanelVersion, false, false, 0,
                "No Community folder configured yet.");
        }

        var validation = ValidateCommunityFolder(path);
        if (!validation.Valid || validation.ResolvedPath is null)
        {
            return PanelOperationResult.Refused(validation.Reason ?? "Invalid Community folder.");
        }

        var target = ResolveWriteTarget(validation.ResolvedPath);
        if (target is null || !Directory.Exists(target) || !File.Exists(Path.Combine(target, "manifest.json")))
        {
            return new PanelOperationResult(
                true, null, false, target, null, ExpectedPanelVersion, false, false, 0,
                "Not installed yet.");
        }

        var installedVersion = ReadManifestVersion(target);
        var spbPresent = File.Exists(Path.Combine(target, "InGamePanels", "FSOpsPanel.spb"));
        var upToDate = string.Equals(installedVersion, ExpectedPanelVersion, StringComparison.Ordinal);

        var message = !upToDate
            ? "An older panel version is installed - reinstall to update."
            : !spbPresent
                ? "Panel files are installed, but the toolbar button will not appear - this build's panel component isn't compiled yet."
                : "Installed and up to date.";

        return new PanelOperationResult(
            true, null, true, target, installedVersion, ExpectedPanelVersion, spbPresent, spbPresent, 0, message);
    }

    // ---------------------------------------------------------------------------------------
    // Uninstall
    // ---------------------------------------------------------------------------------------

    public static PanelOperationResult Uninstall(string? path)
    {
        var validation = ValidateCommunityFolder(path);
        if (!validation.Valid || validation.ResolvedPath is null)
        {
            return PanelOperationResult.Refused(validation.Reason ?? "Invalid Community folder.");
        }

        var target = ResolveWriteTarget(validation.ResolvedPath);
        if (target is null)
        {
            return PanelOperationResult.Refused("Refused to uninstall: the resolved path escaped the chosen Community folder.");
        }

        if (!Directory.Exists(target))
        {
            return new PanelOperationResult(
                true, null, false, target, null, ExpectedPanelVersion, false, false, 0,
                "Nothing was installed there - already clean.");
        }

        Directory.Delete(target, recursive: true);
        return new PanelOperationResult(
            true, null, false, target, null, ExpectedPanelVersion, false, false, 0,
            "Panel removed from your Community folder.");
    }

    // ---------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves the fixed <c>&lt;Community&gt;\fsops-panel</c> write target and re-confirms it is
    /// genuinely still beneath the validated Community folder before any caller deletes or writes
    /// to it. <paramref name="community"/> is untrusted input (chosen by the player) for
    /// file-writing purposes - this is the one guard standing between a validation gap and deleting
    /// something that isn't ours, so it is re-checked here even though PackageFolderName is a fixed
    /// constant with no path separators and Combine+GetFullPath should never disagree with it.
    /// </summary>
    private static string? ResolveWriteTarget(string community)
    {
        var target = Path.GetFullPath(Path.Combine(community, PackageFolderName));
        var communityWithSeparator = community.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return target.StartsWith(communityWithSeparator, StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    private static int CopyTemplateFiles(string templateDirectory, string target)
    {
        var count = 0;
        foreach (var sourceFile in Directory.EnumerateFiles(templateDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(templateDirectory, sourceFile);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, overwrite: true);
            count++;
        }

        return count;
    }

    private static void RewritePortConfig(string target, string port)
    {
        // Defensive: the port ultimately comes from an environment variable, not player input, but
        // it still ends up interpolated into a JavaScript file - validate it is purely numeric
        // before writing it in raw rather than trusting the source.
        var numericPort = Regex.IsMatch(port, "^[0-9]+$") ? port : DefaultPort;

        var configPath = Path.Combine(target, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.config.js");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(
            configPath,
            "// Generated by FSOps at install/update/repair time - do not edit by hand.\n" +
            $"window.FSOPS_PANEL_PORT = {numericPort};\n");
    }

    /// <summary>
    /// Regenerates layout.json from the files actually on disk rather than shipping a static one -
    /// see src/fsops-ingame-panel/README.md "layout.json is deliberately not checked in". Matches
    /// the verified format: a "content" array of {path, size, date}, every file except layout.json
    /// itself (manifest.json IS included), forward-slash relative paths, and "date" as a Windows
    /// FILETIME (100ns ticks since 1601-01-01 UTC) - the same encoding DateTime.ToFileTimeUtc()
    /// produces.
    /// </summary>
    private static void WriteLayoutJson(string target)
    {
        var entries = new List<object>();
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(target, file).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relative, "layout.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(file);
            entries.Add(new
            {
                path = relative,
                size = info.Length,
                date = info.LastWriteTimeUtc.ToFileTimeUtc(),
            });
        }

        entries = entries
            .OrderBy(e => (string)e.GetType().GetProperty("path")!.GetValue(e)!, StringComparer.Ordinal)
            .ToList();

        var json = JsonSerializer.Serialize(new { content = entries }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(target, "layout.json"), json);
    }

    private static string? ReadManifestVersion(string target)
    {
        var manifestPath = Path.Combine(target, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("package_version", out var version) ? version.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One detected (or default, unconfirmed) Community folder candidate.</summary>
public record PanelCandidate(string Path, string Source, bool Exists);

/// <summary>Result of validating a player-chosen path before it is ever written to.</summary>
public record PanelPathValidation(bool Valid, string? Reason, string? ResolvedPath);

/// <summary>Result of an install, repair, uninstall, or status read.</summary>
public record PanelOperationResult(
    bool Success,
    string? Reason,
    bool Installed,
    string? InstalledPath,
    string? InstalledVersion,
    string ExpectedVersion,
    bool SpbPresent,
    bool ToolbarWillAppearInSim,
    int FilesWritten,
    string Message)
{
    public static PanelOperationResult Refused(string reason) =>
        new(false, reason, false, null, null, PanelPackageInstaller.ExpectedPanelVersion, false, false, 0, reason);
}

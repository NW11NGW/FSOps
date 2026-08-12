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
/// The Community folder is captured once at onboarding and reused to install the panel, so the
/// player is never asked for the same path twice and never told to copy a folder by hand:
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
    /// throwing when nothing is found. MSFS 2024 installs to different places for Microsoft Store,
    /// Steam and custom installs, so detection will not always succeed, and a
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
    /// silently later - "this looks like the sim's install folder, not Community" is a reason the
    /// player can act on; a panel that simply never appears is not.
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

        return new PanelPathValidation(true, null, resolved, Directory.Exists(resolved));
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

        // Directory.CreateDirectory below would happily build the whole missing tree, which would
        // look like a successful install and do nothing: MSFS only reads the Community folder it
        // actually has, so a phantom one FSOps invented is a panel that never appears. If the
        // folder is gone, the path is wrong or the sim moved - say so instead.
        if (!validation.Exists)
        {
            return PanelOperationResult.Refused(
                $"That folder doesn't exist: {validation.ResolvedPath}. If you've reinstalled or moved Microsoft " +
                "Flight Simulator, point FSOps at the new Community folder.");
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
        // outside <Community>\fsops-panel - see ResolveWriteTarget's containment check - and
        // never deletes a folder of that name FSOps did not put there, see IsOurPackage.
        if (Directory.Exists(target) && !IsOurPackage(target))
        {
            return PanelOperationResult.Refused(NotOursReason(target));
        }

        int filesWritten;
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(target);

            filesWritten = CopyTemplateFiles(templateDirectory, target);
            RewritePortConfig(target, port);
            WriteLayoutJson(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwhelmingly this is MSFS itself holding the package open, which is a thing the
            // player can fix - say so, rather than surfacing a raw file-system error.
            return PanelOperationResult.Refused(
                "Couldn't write to the panel folder — close Microsoft Flight Simulator if it's running, " +
                $"then try again. ({ex.Message})");
        }

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
                  "update adds it.",
            InstalledPort: ReadInstalledPort(target),
            ExpectedPort: port);
    }

    // ---------------------------------------------------------------------------------------
    // Move (change of Community folder)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Moves the panel from one Community folder to another when the player changes their folder in
    /// Settings. Installs into the new folder FIRST and only removes the old copy once that has
    /// actually succeeded, so a failed move can never leave the player with no panel at all - the
    /// worst case is two copies, which is recoverable, rather than none, which needs a reinstall.
    ///
    /// <para>
    /// Cleaning up the old folder is deliberately best-effort and always reported honestly: an old
    /// path that has since been deleted, renamed, locked by a running sim, or that no longer holds
    /// an FSOps package is left alone and said so, never silently swallowed.
    /// </para>
    /// </summary>
    public static PanelMoveResult MoveInstall(string? fromPath, string? toPath, string templateDirectory, string port)
    {
        var install = InstallOrRepair(toPath, templateDirectory, port);
        if (!install.Success)
        {
            return new PanelMoveResult(
                false, install.Reason, install.Message, install, false,
                "Your old folder was left exactly as it was, because the new install didn't succeed.");
        }

        if (string.IsNullOrWhiteSpace(fromPath))
        {
            return new PanelMoveResult(true, null, install.Message, install, false, "There was no previous folder to clean up.");
        }

        var fromValidation = ValidateCommunityFolder(fromPath);
        if (!fromValidation.Valid || fromValidation.ResolvedPath is null)
        {
            return new PanelMoveResult(
                true, null, install.Message, install, false,
                "The panel is installed in the new folder. Your previous folder path isn't a usable Community " +
                "folder, so nothing was removed from it.");
        }

        // Same folder either side of the "move" - InstallOrRepair has already rewritten it in
        // place, and removing "the old copy" here would delete the install just made.
        var toValidation = ValidateCommunityFolder(toPath);
        if (string.Equals(fromValidation.ResolvedPath, toValidation.ResolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            return new PanelMoveResult(true, null, install.Message, install, false, "Reinstalled in place — it's the same folder as before.");
        }

        var oldTarget = Path.Combine(fromValidation.ResolvedPath, PackageFolderName);
        if (!Directory.Exists(oldTarget))
        {
            return new PanelMoveResult(
                true, null, install.Message, install, false,
                "There was nothing left in your previous folder to clean up.");
        }

        var removal = Uninstall(fromValidation.ResolvedPath);
        return new PanelMoveResult(
            true,
            null,
            install.Message,
            install,
            removal.Success,
            removal.Success
                ? $"The old copy in {oldTarget} was removed."
                : $"The old copy in {oldTarget} could not be removed — {removal.Reason} You can delete that folder by hand.");
    }

    // ---------------------------------------------------------------------------------------
    // Status (read-only - never writes)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// What is actually on disk in <paramref name="path"/> right now.
    ///
    /// <para>
    /// <paramref name="templateDirectory"/> is what the install is measured against: every file the
    /// template would write must still be present, or the package is reported as incomplete. Passing
    /// null skips only that completeness check (version, .spb and port are still read), which keeps
    /// the older two-argument callers working; the server always passes it.
    /// </para>
    /// </summary>
    public static PanelOperationResult GetStatus(string? path, string port, string? templateDirectory = null)
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

        // A configured folder that has since been deleted is its own state, and a far more useful
        // thing to say than "not installed" - which would invite the player to press Install and
        // wonder why the panel never turns up.
        if (!validation.Exists)
        {
            return PanelOperationResult.Refused(
                $"The Community folder FSOps is set to no longer exists: {validation.ResolvedPath}. " +
                "If you've reinstalled or moved Microsoft Flight Simulator, choose the new folder below.");
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
        var installedPort = ReadInstalledPort(target);
        var portMatches = installedPort is null || string.Equals(installedPort, port, StringComparison.Ordinal);
        var missingFiles = FindMissingPackageFiles(target, templateDirectory);
        var complete = missingFiles.Count == 0;

        // Whether the .spb is absent because this BUILD never shipped one, or because the player's
        // copy lost it, decides which of two opposite things to tell them: wait for a future update,
        // or press repair now. Getting that the wrong way round sends them away to wait for
        // something that will never arrive, so it is worth asking the template rather than guessing.
        var templateHasSpb = templateDirectory is not null
            && File.Exists(Path.Combine(templateDirectory, "InGamePanels", "FSOpsPanel.spb"));

        // Missing files lead the message, then port drift: both leave the panel blank in the sim
        // while it looks perfectly installed from here, and a repair is the fix for either.
        var message = !complete
            ? $"{DescribeMissing(missingFiles)} from the installed panel - reinstall to put them back."
            : !portMatches
                ? $"The installed panel is still calling port {installedPort}, but FSOps is now on port {port} - " +
                  "reinstall to point it at the right one, or the panel will show nothing in the sim."
                : !upToDate
                    ? "An older panel version is installed - reinstall to update."
                    : !spbPresent
                        ? templateHasSpb
                            ? "The compiled panel component is missing from your copy - reinstall to put it back."
                            : "Panel files are installed, but the toolbar button will not appear - this build's panel component isn't compiled yet."
                        : "Installed and up to date.";

        return new PanelOperationResult(
            true, null, complete, target, installedVersion, ExpectedPanelVersion, spbPresent, spbPresent, 0, message,
            installedPort, port, missingFiles);
    }

    private static string DescribeMissing(IReadOnlyList<string> missing) =>
        missing.Count == 1 ? $"{missing[0]} is missing" : $"{missing.Count} files are missing";

    /// <summary>
    /// Package-relative paths the template would have written that are not on disk.
    ///
    /// <para>
    /// The expected set is read from the template every time rather than hardcoded, so a file added
    /// to the package later is covered with no change here - a hardcoded list would drift silently
    /// and reproduce the very bug this check exists to catch. layout.json is the one addition: it is
    /// generated at install time by WriteLayoutJson rather than copied, so it is not in the template,
    /// but MSFS needs it and its absence is just as much a broken install as any copied file's.
    /// </para>
    ///
    /// <para>
    /// Returns empty when no template is available to compare against - an unknown answer must not
    /// masquerade as "nothing is missing", but neither can it condemn a perfectly good install.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> FindMissingPackageFiles(string target, string? templateDirectory)
    {
        if (templateDirectory is null || !Directory.Exists(templateDirectory))
        {
            return Array.Empty<string>();
        }

        var expected = Directory
            .EnumerateFiles(templateDirectory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(templateDirectory, file))
            .Append("layout.json")
            .ToList();

        return expected
            .Where(relative => !File.Exists(Path.Combine(target, relative)))
            .Select(relative => relative.Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();
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

        if (!IsOurPackage(target))
        {
            return PanelOperationResult.Refused(NotOursReason(target));
        }

        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PanelOperationResult.Refused(
                "Couldn't remove the panel folder — close Microsoft Flight Simulator if it's running, " +
                $"then try again. ({ex.Message})");
        }

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

    /// <summary>
    /// Confirms a <c>&lt;Community&gt;\fsops-panel</c> folder is genuinely one FSOps wrote before
    /// anything deletes it recursively. Both the install (which clears the folder first, so a repair
    /// can never merge old and new files) and the uninstall gate on this.
    ///
    /// <para>
    /// The Community folder is a path the player typed, and <c>fsops-panel</c> is a name anyone
    /// could have used for something of their own. Writing into a folder we did not create is
    /// merely rude; recursively deleting one is unrecoverable, and no amount of "but the name
    /// matched" makes that acceptable. So: match on a fingerprint only an FSOps install leaves,
    /// and refuse - loudly, with the path - when it is absent.
    /// </para>
    ///
    /// <para>
    /// Two independent fingerprints are accepted so a half-written or hand-edited install can still
    /// be repaired rather than becoming permanently un-fixable: the manifest's own title, and the
    /// config file FSOps generates itself at every install (it is never part of a package a player
    /// would have built). An empty folder is accepted too - there is nothing there to lose.
    /// </para>
    /// </summary>
    private static bool IsOurPackage(string target)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(target).Any())
            {
                return true;
            }

            if (File.Exists(Path.Combine(target, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.config.js")))
            {
                return true;
            }

            var manifestPath = Path.Combine(target, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("title", out var title) &&
                   title.GetString()?.Contains("FSOps", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Can't read it, so can't prove it is ours - and "can't prove" must never authorise a
            // recursive delete.
            return false;
        }
    }

    private static string NotOursReason(string target) =>
        $"There's already a folder at {target}, but it doesn't look like one FSOps installed. " +
        "FSOps won't delete or overwrite something it didn't create — move or remove that folder " +
        "yourself if you're sure, then try again.";

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

    /// <summary>
    /// Reads back the port RewritePortConfig baked into the installed package. Returns null when the
    /// file is missing or unreadable, which is treated as "no drift to report" rather than a
    /// mismatch - claiming the wrong port when we simply could not read one would send the player
    /// chasing a problem that isn't there.
    /// </summary>
    private static string? ReadInstalledPort(string target)
    {
        var configPath = Path.Combine(target, "html_ui", "InGamePanels", "FSOpsPanel", "FSOpsPanel.config.js");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var match = Regex.Match(File.ReadAllText(configPath), @"FSOPS_PANEL_PORT\s*=\s*(\d+)");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

/// <summary>
/// Result of validating a player-chosen path before it is ever written to. <paramref name="Valid"/>
/// is a judgement about the path's shape only; <paramref name="Exists"/> is reported separately
/// because the two answers are useful in different places - onboarding wants to accept a
/// well-formed path without insisting the sim is installed on this machine yet, while an install
/// must refuse a folder that isn't there rather than helpfully creating it.
/// </summary>
public record PanelPathValidation(bool Valid, string? Reason, string? ResolvedPath, bool Exists = false);

/// <summary>
/// Result of moving the panel between Community folders. <paramref name="Install"/> is the full
/// result of installing into the new folder; the two OldCopy fields report, separately and
/// honestly, what happened to the folder left behind - a move that installed fine but couldn't
/// clean up is a success with a caveat, not a failure, and the player needs to be told which.
/// </summary>
/// <param name="Message">
/// The one line worth showing the player, carried at the top level because that is where both the
/// SPA's generic error reader and a failed-request toast look for it - a 400 whose reason is buried
/// inside a nested object surfaces to the player as "Bad Request", which tells them nothing.
/// </param>
public record PanelMoveResult(
    bool Success,
    string? Reason,
    string Message,
    PanelOperationResult Install,
    bool OldCopyRemoved,
    string OldCopyMessage);

/// <summary>Result of an install, repair, uninstall, or status read.</summary>
/// <param name="Installed">
/// True only when a COMPLETE FSOps package is present in the folder. This deliberately no longer
/// means merely "manifest.json is there": a status read that decided from the manifest alone
/// reported a green "Installed, up to date" over an install whose FSOpsPanel.js had been deleted,
/// which is worse than no status at all because it argues against the one action that would fix it.
/// When a package is present but incomplete this is false and <paramref name="MissingFiles"/> is
/// non-empty - the two together are what the UI reads as its needs-repair state.
/// </param>
/// <param name="MissingFiles">
/// Package-relative paths that should be on disk and are not, empty whenever nothing is wrong.
/// Non-empty only for a package that IS present but incomplete; a folder with no FSOps package at
/// all is "not installed", not "missing every file". Presence only - contents and sizes are not
/// compared, because deletion is by far the commoner failure and a status read has to stay cheap.
/// </param>
/// <param name="InstalledPort">
/// The port baked into the installed FSOpsPanel.config.js, or null when nothing is installed. The
/// panel talks to FSOps over HTTP on a fixed port written at install time, so if FSOps later moves
/// port - FSOPS_PORT is set, or the app falls back because something else took 5977 - the installed
/// panel goes on calling the old one and simply shows nothing in the sim, with no error anywhere.
/// Reporting both ports is what makes that visible; a repair rewrites the file and fixes it.
/// </param>
/// <param name="ExpectedPort">The port this FSOps is actually listening on right now.</param>
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
    string Message,
    string? InstalledPort = null,
    string? ExpectedPort = null,
    IReadOnlyList<string>? MissingFiles = null)
{
    /// <summary>Never null, so neither the SPA nor a test has to special-case the empty case.</summary>
    public IReadOnlyList<string> MissingFiles { get; init; } = MissingFiles ?? Array.Empty<string>();

    public static PanelOperationResult Refused(string reason) =>
        new(false, reason, false, null, null, PanelPackageInstaller.ExpectedPanelVersion, false, false, 0, reason);
}

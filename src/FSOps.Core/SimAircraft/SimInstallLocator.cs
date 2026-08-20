namespace FSOps.Core.SimAircraft;

/// <summary>
/// Finds the player's Community folder without asking them where it is.
///
/// <para>There is no single answer to look up. Microsoft Store and Steam put the simulator in
/// completely different places, MSFS 2020 and MSFS 2024 are different places again, and the folder
/// can be moved anywhere - a second drive is the normal choice once somebody has a few hundred
/// gigabytes of scenery. So this asks the simulator rather than guessing: <c>UserCfg.opt</c>
/// carries an <c>InstalledPackagesPath</c> line naming wherever the packages actually live, and
/// that is authoritative in a way a hardcoded path never is. The hardcoded paths are a fallback for
/// when no UserCfg.opt can be read.</para>
///
/// <para>Finding nothing is a perfectly normal outcome - the simulator may simply not be installed
/// on this machine - and is reported as "could not find it", never as an error and never as a claim
/// about what the player owns.</para>
/// </summary>
public static class SimInstallLocator
{
    private const string InstalledPackagesKey = "InstalledPackagesPath";

    /// <summary>
    /// Every Community folder that exists on this machine, best guess first. Empty when none was
    /// found. The caller picks the first, but the whole list is worth having: somebody with both
    /// simulators installed should be able to see that FSOps found two.
    /// </summary>
    public static IReadOnlyList<string> FindCommunityFolders() =>
        FindCommunityFolders(DefaultUserCfgPaths(), DefaultDirectCommunityPaths());

    /// <summary>Overload taking the candidate paths, so this can be tested without a simulator installed.</summary>
    public static IReadOnlyList<string> FindCommunityFolders(
        IEnumerable<string> userCfgPaths,
        IEnumerable<string> directCommunityPaths)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var userCfgPath in userCfgPaths)
        {
            var packagesRoot = ReadInstalledPackagesPath(userCfgPath);
            if (packagesRoot is null)
            {
                continue;
            }

            var community = SafeCombine(packagesRoot, "Community");
            if (community is not null && SafeDirectoryExists(community) && seen.Add(community))
            {
                found.Add(community);
            }
        }

        foreach (var candidate in directCommunityPaths)
        {
            if (SafeDirectoryExists(candidate) && seen.Add(candidate))
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    /// <summary>
    /// Pulls <c>InstalledPackagesPath "..."</c> out of a UserCfg.opt. Returns null for a file that
    /// is missing, unreadable, or does not carry the key - all of which are ordinary.
    /// </summary>
    public static string? ReadInstalledPackagesPath(string userCfgPath)
    {
        string[] lines;
        try
        {
            if (!File.Exists(userCfgPath))
            {
                return null;
            }

            lines = File.ReadAllLines(userCfgPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(InstalledPackagesKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed[InstalledPackagesKey.Length..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            value = value.Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Where each store's simulator keeps its UserCfg.opt. MSFS 2024 first, because that is what
    /// FSOps is for; MSFS 2020 after it, because plenty of people still have both installed and a
    /// 2020 Community folder is a real answer rather than a wrong one.
    /// </summary>
    private static IEnumerable<string> DefaultUserCfgPaths()
    {
        var localAppData = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = SafeFolder(Environment.SpecialFolder.ApplicationData);

        // Microsoft Store / Game Pass, MSFS 2024. The package family name really is "Limitless".
        yield return Combine(localAppData, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");

        // Steam, MSFS 2024.
        yield return Combine(roamingAppData, "Microsoft Flight Simulator 2024", "UserCfg.opt");

        // Microsoft Store / Game Pass, MSFS 2020.
        yield return Combine(localAppData, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");

        // Steam, MSFS 2020.
        yield return Combine(roamingAppData, "Microsoft Flight Simulator", "UserCfg.opt");
    }

    /// <summary>
    /// The default Community folders, used only when no UserCfg.opt could be read. These are wrong
    /// for anybody who moved their packages, which is exactly why they are the fallback.
    /// </summary>
    private static IEnumerable<string> DefaultDirectCommunityPaths()
    {
        var localAppData = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = SafeFolder(Environment.SpecialFolder.ApplicationData);

        yield return Combine(localAppData, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "Packages", "Community");
        yield return Combine(roamingAppData, "Microsoft Flight Simulator 2024", "Packages", "Community");
        yield return Combine(localAppData, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "Packages", "Community");
        yield return Combine(roamingAppData, "Microsoft Flight Simulator", "Packages", "Community");
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string Combine(string root, params string[] parts) =>
        string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(new[] { root }.Concat(parts).ToArray());

    private static string? SafeCombine(string root, string child)
    {
        try
        {
            return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, child);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}

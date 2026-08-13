using FSOps.Core;
using Microsoft.Data.Sqlite;

namespace FSOps.Server.Services;

/// <summary>
/// Removes the FSOps in-game panel from the player's MSFS Community folder during uninstall - see
/// installer/FSOps.iss's <c>[UninstallRun]</c> entry, which runs this build as
/// <c>FSOps.Server.exe --uninstall-panel</c>.
///
/// <para>
/// The installer never learns the Community folder the player chose at onboarding - only the app
/// knows it, saved in <c>UserSettings.CommunityFolderPath</c>. Inno's own documentation describes
/// <c>[UninstallRun]</c> entries as executing "as the first step of uninstallation" - before Inno
/// removes any of the app's own files, and well before <c>CurUninstallStepChanged</c>'s optional
/// data-directory-deletion prompt, which fires later, at <c>usPostUninstall</c>. So this command
/// always runs with both a working <c>FSOps.Server.exe</c> AND an intact database to read the path
/// from, regardless of what the player later answers at that prompt.
/// </para>
///
/// <para>
/// Reads the database directly with a single, hand-written <c>SELECT</c> rather than through
/// EF/<c>FsOpsDbContext</c>: this process has no server, no DI container, and no business running
/// migrations against a database whose lifecycle belongs to the app, not to an uninstall step. The
/// actual removal is delegated entirely to <see cref="PanelPackageInstaller.Uninstall"/>, which
/// already refuses to touch a folder FSOps did not create - no new deletion logic is written here on
/// purpose.
/// </para>
///
/// <para>
/// Every branch below gives up quietly rather than throwing, and <see cref="Run"/> always returns 0.
/// This runs from <c>[UninstallRun]</c>: failing to tidy up a Community folder is a minor
/// inconvenience the player can still fix by hand (see the troubleshooting guide), while failing the
/// WHOLE uninstall over it - leaving Windows unable to remove FSOps via Add/Remove Programs - would
/// be far worse. Every I/O failure (a missing or locked database, an unexpected schema, a locked
/// panel folder, a moved or deleted Community folder) is swallowed after a line is written to stdout
/// for anyone who inspects the uninstall log.
/// </para>
/// </summary>
public static class PanelUninstallCommand
{
    /// <summary>The argument Program.cs looks for on the command line - see installer/FSOps.iss.</summary>
    public const string Argument = "--uninstall-panel";

    /// <summary>
    /// Matches FSOps.Server.Auth.LocalUser.UserId. FSOps is single-user today, so there is exactly
    /// one UserSettings row to look at. Spelled out as a constant, rather than referencing LocalUser
    /// itself, because constructing that would mean pulling in ICurrentUser/DI machinery this command
    /// deliberately runs without. Every character is a digit, so the SQLite
    /// stored-GUIDs-are-upper-case gotcha (see AGENTS/CLAUDE conventions) genuinely cannot bite this
    /// particular value - there is no hex letter in it to differ by case - but the query below still
    /// compares case-insensitively so this stays true if that ID is ever revisited.
    /// </summary>
    private const string LocalUserId = "11111111-1111-1111-1111-111111111111";

    /// <summary>Production entry point - always the real database at AppPaths.DatabasePath (which
    /// itself honours FSOPS_DATA_DIR, exactly like the running app).</summary>
    public static int Run() => RunFor(AppPaths.DatabasePath);

    /// <summary>
    /// Split out from <see cref="Run"/> purely so tests can point this at a throwaway database file
    /// instead of the real one - a test must never touch AppPaths.DatabasePath.
    /// </summary>
    internal static int RunFor(string databasePath)
    {
        try
        {
            var path = TryReadCommunityFolderPath(databasePath);
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("FSOps: no Community folder was configured - nothing to remove.");
                return 0;
            }

            var result = PanelPackageInstaller.Uninstall(path);
            Console.WriteLine(result.Success
                ? $"FSOps: {result.Message}"
                : $"FSOps: couldn't remove the in-game panel automatically - {result.Reason}");
        }
        catch (Exception ex)
        {
            // Deliberately catches everything, not just IOException/SqliteException - nothing this
            // command can encounter is worth failing the uninstall over. See the class doc.
            Console.WriteLine($"FSOps: panel removal skipped after an unexpected error - {ex.Message}");
        }

        return 0;
    }

    /// <summary>
    /// A single read-only-in-intent <c>SELECT</c> against the SQLite file directly - no EF, and no
    /// statement here ever writes. Returns null (never throws) whenever the answer can't be known for
    /// certain: no database file, a locked file, a schema this build doesn't recognise, or no row at
    /// all. "Can't prove there's a path configured" must never be treated as "here's a path to
    /// delete" - see <see cref="PanelPackageInstaller.Uninstall"/>'s own IsOurPackage reasoning for
    /// why that asymmetry matters.
    /// </summary>
    internal static string? TryReadCommunityFolderPath(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            // ReadWrite, never ReadWriteCreate: this must never bring a database into existence, and
            // File.Exists above already proved one is there. Plain ReadOnly is deliberately avoided -
            // the database runs in WAL mode (see WalModeConnectionInterceptor), and SQLite needs to
            // create/update the -wal and -shm sidecar files even for a read in that mode, which a
            // read-only-mode connection cannot do.
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            // A short, bounded wait rather than SQLite's zero-wait default. FSOps itself should
            // already be closed by the time an uninstall reaches [UninstallRun], but if it somehow
            // is not, wait briefly for whatever transaction is in flight rather than failing on the
            // very first busy signal.
            DefaultTimeout = 3,
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT CommunityFolderPath FROM UserSettings WHERE UPPER(OwnerUserId) = UPPER(@ownerUserId) LIMIT 1";
            command.Parameters.AddWithValue("@ownerUserId", LocalUserId);

            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            // Missing table/column (a database old enough not to have this schema element yet, or a
            // future one that has renamed it), a locked file, or a damaged one - none of these
            // authorise guessing at a path to delete.
            return null;
        }
    }
}

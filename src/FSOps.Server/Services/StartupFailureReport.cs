using FSOps.Core;

namespace FSOps.Server.Services;

/// <summary>
/// Writes a startup failure somewhere the desktop shell can show it, and names the exit code that
/// tells the shell to go and look.
///
/// <para>The shell already notices when the server exits before it finishes starting, but all it
/// could say was "It exited with code N", which tells the user nothing they can act on. Rather than
/// duplicating the explanatory text into <c>ServerSupervisor</c> - where the two copies would drift
/// apart within a release or two - the server writes the message to a file and the shell shows that
/// file verbatim. The wording lives in exactly one place, next to the code that knows what went
/// wrong.</para>
///
/// <para>The file sits in the data directory rather than beside the executable, for the same reason
/// everything else does: the app installs into Program Files and cannot write there.</para>
/// </summary>
public static class StartupFailureReport
{
    /// <summary>
    /// Exit code meaning "I could not start, and I have left an explanation in
    /// <see cref="FilePath"/>". Distinct from 1 so the shell can tell a reportable failure from any
    /// other crash and does not show a stale file after an unrelated one.
    /// </summary>
    public const int ExitCode = 3;

    public const string FileName = "startup-error.txt";

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, FileName);

    /// <summary>
    /// Records <paramref name="userMessage"/> for the shell to display and returns
    /// <see cref="ExitCode"/> so the caller can return it from Main.
    ///
    /// <para>Best-effort by design. If the message cannot be written - which is entirely possible,
    /// since an unwritable data directory is one of the things that lands here - the process must
    /// still exit with the right code rather than throwing on its way out and replacing a clear
    /// failure with a confusing one.</para>
    /// </summary>
    public static int Write(string userMessage)
    {
        try
        {
            File.WriteAllText(FilePath, userMessage.ReplaceLineEndings());
        }
        catch (Exception)
        {
            // Nothing useful left to do: the log has the detail, and the exit code still tells the
            // shell something went wrong at startup.
        }

        return ExitCode;
    }

    /// <summary>
    /// Removes any report left by a previous run. Called on a successful start so the shell can
    /// never show a stale explanation for a launch that actually worked.
    /// </summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception)
        {
            // A leftover file is only a problem if the server also exits with ExitCode, and it did not.
        }
    }
}

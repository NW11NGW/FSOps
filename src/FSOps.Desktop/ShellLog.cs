using System.Globalization;
using System.Text;
using FSOps.Core;

namespace FSOps.Desktop;

/// <summary>
/// A deliberately tiny append-only log for the shell itself, written beside the server's Serilog
/// files in the FSOps data directory (and therefore honouring FSOPS_DATA_DIR like everything else).
///
/// <para>
/// The shell does not take a logging framework dependency on purpose. The only things worth
/// recording here are the half-dozen decisions that explain a failed launch - which port was
/// chosen, which server executable was found, why the window showed an error - and when those go
/// wrong the log itself must not be the thing that failed. Every write is best-effort and swallows
/// its own exceptions: a locked or unwritable log file must never stop the app from opening.
/// </para>
/// </summary>
internal static class ShellLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    private static string? Path
    {
        get
        {
            if (_path is not null)
            {
                return _path;
            }

            try
            {
                _path = System.IO.Path.Combine(AppPaths.LogsDirectory, "fsops-desktop.log");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }

            return _path;
        }
    }

    public static void Write(string message)
    {
        var path = Path;
        if (path is null)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}{Environment.NewLine}");

        try
        {
            lock (Gate)
            {
                // Restart rather than roll: this file exists to explain the launch that just
                // happened, so a megabyte of history is already far more than anyone needs.
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.WriteAllText(path, string.Empty, Encoding.UTF8);
                }

                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort by design - see the class summary.
        }
    }

    public static void Write(string message, Exception exception) =>
        Write($"{message}: {exception.GetType().Name}: {exception.Message}");

    /// <summary>Where the log lives, for the "copy details" button on the error screen.</summary>
    public static string? Location => Path;
}

using System.Diagnostics;

namespace FSOps.Desktop;

/// <summary>
/// Entry point for the FSOps desktop shell.
///
/// <para>
/// The shell is a window and a process supervisor, and nothing else. It never touches the database,
/// never reads world data, and holds no opinion about the economy - all of that stays in
/// FSOps.Server, which it starts as a child process and stops again on close. Keeping it that thin
/// is what makes it safe to add: nothing about running the app in a browser or headless changes
/// because this exists.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>Set to "1" to skip the embedded window and use the default browser.</summary>
    public const string UseBrowserVariable = "FSOPS_USE_BROWSER";

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();

        ShellLog.Write("---- FSOps desktop shell starting ----");

        // Decided here, before any window exists. A missing WebView2 runtime must never be a gate:
        // the app's whole reason for existing is served over HTTP, and the user's own browser
        // renders it perfectly well. So the shell picks its display mode up front and MainForm
        // never constructs a web view it cannot use - nobody is shown a dead frame first.
        ShellDisplayMode mode;
        if (Environment.GetEnvironmentVariable(UseBrowserVariable) == "1")
        {
            // Set FSOPS_USE_BROWSER=1 to always use the default browser. It is there for anyone who
            // simply prefers their own browser - with its zoom, extensions and window management -
            // and it is also the only way to exercise the fallback on a machine that does have
            // WebView2, which is every machine this has been developed on.
            mode = ShellDisplayMode.DefaultBrowser;
            ShellLog.Write($"{UseBrowserVariable}=1; opening FSOps in the default browser by request.");
        }
        else
        {
            var webView2 = WebView2Runtime.Detect();
            mode = webView2.Available ? ShellDisplayMode.EmbeddedWindow : ShellDisplayMode.DefaultBrowser;

            ShellLog.Write(webView2.Available
                ? $"WebView2 runtime {webView2.Version}; showing FSOps in its own window."
                : $"No usable WebView2 runtime ({webView2.Detail}); opening FSOps in the default browser instead.");
        }

        using var form = new MainForm(mode);
        Application.Run(form);

        ShellLog.Write($"---- FSOps desktop shell exiting (code {form.ExitCode}) ----");
        return form.ExitCode;
    }

    /// <summary>
    /// Opens a URL with whatever the user has set as their default handler. Used for the browser
    /// fallback and for any outward link the app offers.
    /// </summary>
    public static bool OpenInDefaultBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            ShellLog.Write($"Could not open {uri.AbsoluteUri} in the default browser", ex);
            return false;
        }
    }
}

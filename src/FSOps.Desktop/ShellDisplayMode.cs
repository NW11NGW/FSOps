namespace FSOps.Desktop;

/// <summary>
/// How the shell shows FSOps. Chosen once, before any window is created, so the app never builds a
/// web view it cannot use and the user never sees a dead frame before being told what happened.
/// </summary>
internal enum ShellDisplayMode
{
    /// <summary>The normal case: the SPA rendered inside the FSOps window by WebView2.</summary>
    EmbeddedWindow,

    /// <summary>
    /// No usable WebView2 runtime. FSOps opens in the user's default browser instead and the window
    /// stays as a small companion that owns the server's lifetime. Deliberately not an error state:
    /// the browser is the app's original, fully working experience, so the product still works - it
    /// just does not have its own frame.
    /// </summary>
    DefaultBrowser,
}

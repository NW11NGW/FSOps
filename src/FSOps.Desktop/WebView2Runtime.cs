using Microsoft.Web.WebView2.Core;

namespace FSOps.Desktop;

internal readonly record struct WebView2Availability(bool Available, string? Version, string? Detail);

/// <summary>
/// Checks that the WebView2 runtime the shell renders in is actually installed, before a window
/// exists to fail inside.
///
/// <para>
/// The Evergreen runtime ships with Windows 11 and is delivered to Windows 10 through Edge updates,
/// so on a current machine this always succeeds. It is not guaranteed, though: LTSC and heavily
/// managed enterprise images can genuinely be without it, and a machine where the runtime was
/// removed produces an unhandled COM exception the moment a CoreWebView2 is created. That reads to
/// a user as "the app crashes on launch" with nothing to act on, which is why this runs first and
/// says what to install instead.
/// </para>
/// </summary>
internal static class WebView2Runtime
{
    /// <summary>
    /// Microsoft's permanent short link to the Evergreen bootstrapper. The installer needs this
    /// too - a first-party installer should install the runtime rather than making the user do it.
    /// </summary>
    public const string EvergreenBootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    public static WebView2Availability Detect()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(version))
            {
                return new WebView2Availability(false, null, "No WebView2 runtime version was reported.");
            }

            return new WebView2Availability(true, version, null);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            return new WebView2Availability(false, null, ex.Message);
        }
        catch (DllNotFoundException ex)
        {
            // WebView2Loader.dll missing from the application folder - a broken deployment rather
            // than a missing runtime, and worth distinguishing because reinstalling FSOps fixes one
            // and installing the runtime fixes the other.
            return new WebView2Availability(false, null, $"WebView2Loader.dll could not be loaded: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new WebView2Availability(false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

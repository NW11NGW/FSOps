using System.Globalization;
using FSOps.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace FSOps.Desktop;

/// <summary>
/// The FSOps window: a chrome-free WebView2 filling a normal desktop frame, plus the states that
/// exist around it - "starting", "running in your browser instead", and "it did not start".
///
/// <para>
/// The startup sequence is deliberately ordered so a blank white window is never shown. The web
/// view is created and sized immediately (so its native surface exists and its background is
/// already the app's own colour), but nothing is navigated until FSOps.Server has answered
/// /api/v1/health. Until then the overlay covers it. Navigating early is what produces the flash of
/// a browser error page followed by a manual refresh, which is precisely the "is this thing broken?"
/// moment a desktop shell is supposed to remove.
/// </para>
///
/// <para>
/// If WebView2 is unavailable the web view is never constructed at all - see
/// <see cref="ShellDisplayMode"/>. The window still opens, because it owns the server's lifetime and
/// gives the user a taskbar entry to stop the app, but its whole content is one calm line saying
/// FSOps has opened in their browser. A missing runtime is never a reason the product does not work.
/// </para>
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ServerSupervisor _supervisor = new();
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly bool _lightTheme = SystemPrefersLightTheme();

    private readonly Panel _overlay = new();
    private readonly Label _headline = new();
    private readonly Label _status = new();
    private readonly Label _note = new();
    private readonly ProgressBar _progress = new();
    private readonly TextBox _detail = new();
    private readonly FlowLayoutPanel _buttons = new();
    private readonly Button _openButton = new();
    private readonly Button _addWindowButton = new();
    private readonly Button _retryButton = new();
    private readonly Button _copyButton = new();
    private readonly Button _closeButton = new();
    private readonly PictureBox _mark = new();
    private readonly Icon _applicationIcon = LoadApplicationIcon();

    private WebView2? _webView;
    private ShellDisplayMode _mode;
    private Uri? _baseAddress;
    private bool _uiReady;

    private Color Background => _lightTheme ? Color.FromArgb(0xF8, 0xFA, 0xFC) : Color.FromArgb(0x0B, 0x12, 0x20);
    private Color Foreground => _lightTheme ? Color.FromArgb(0x0B, 0x12, 0x20) : Color.FromArgb(0xE2, 0xE8, 0xF0);
    private Color Muted => _lightTheme ? Color.FromArgb(0x64, 0x74, 0x8B) : Color.FromArgb(0x94, 0xA3, 0xB8);
    private static Color Accent => Color.FromArgb(0x22, 0xB8, 0xF0);

    public MainForm(ShellDisplayMode mode)
    {
        _mode = mode;

        Text = "FSOps";
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = _applicationIcon;
        DoubleBuffered = true;

        if (_mode == ShellDisplayMode.EmbeddedWindow)
        {
            MinimumSize = new Size(1024, 640);
            Size = DefaultWindowSize();
            BuildWebView();
            BuildOverlay();

            // Only the embedded window has a size worth remembering. The companion window below is
            // a fixed-size notice, and letting it save its own dimensions would quietly shrink the
            // real window the next time WebView2 is available again.
            ApplySavedPlacement();
            return;
        }

        // Browser mode: the whole content is a short message and three buttons, so a 1440x900 frame
        // would be a large empty rectangle sitting on the taskbar for no reason.
        MinimumSize = new Size(560, 380);
        Size = new Size(620, 420);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        BuildOverlay();
    }

    /// <summary>Non-zero when the window closed because the app could not start.</summary>
    public int ExitCode { get; private set; }

    // -----------------------------------------------------------------------------------------
    // Layout
    // -----------------------------------------------------------------------------------------

    private static Size DefaultWindowSize()
    {
        // Comfortably wide enough for the app's widest table layouts, but never larger than the
        // screen it is opening on - a 1440x900 default on a 1366x768 laptop would open partly off
        // the bottom of the display.
        var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        return new Size(Math.Min(1440, working.Width - 40), Math.Min(900, working.Height - 40));
    }

    private static Icon LoadApplicationIcon()
    {
        // The embedded copy carries every size; Icon.ExtractAssociatedIcon would give back a single
        // 32x32 that the taskbar then scales badly.
        var assembly = typeof(MainForm).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("fsops.ico", StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is not null)
            {
                return new Icon(stream);
            }
        }

        return SystemIcons.Application;
    }

    private void BuildWebView()
    {
        var webView = new WebView2
        {
            // Must be set before the control creates its CoreWebView2. The default user-data folder
            // is beside the executable, which is Program Files once installed and therefore
            // read-only for a standard user - WebView2 fails to initialise outright there. Put it
            // with the database, so it also follows FSOPS_DATA_DIR.
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(AppPaths.DataDirectory, "webview2"),
            },
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Background,
        };

        webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
        Controls.Add(webView);
        _webView = webView;
    }

    private void BuildOverlay()
    {
        // The companion window is much narrower than the app window, so wrap the text to fit it
        // rather than letting a fixed 560px paragraph run off both edges.
        var textWidth = _mode == ShellDisplayMode.EmbeddedWindow ? 560 : 460;

        _overlay.Dock = DockStyle.Fill;
        _overlay.BackColor = Background;

        _mark.Image = _applicationIcon.ToBitmap();
        _mark.SizeMode = PictureBoxSizeMode.Zoom;
        _mark.Size = new Size(72, 72);
        _mark.BackColor = Color.Transparent;

        _headline.Text = "FSOps";
        _headline.Font = new Font(Font.FontFamily, 20f, FontStyle.Regular);
        _headline.ForeColor = Foreground;
        _headline.AutoSize = true;
        _headline.MaximumSize = new Size(textWidth + 160, 0);
        _headline.TextAlign = ContentAlignment.MiddleCenter;

        _status.Text = "Starting…";
        _status.Font = new Font(Font.FontFamily, 10f);
        _status.ForeColor = Muted;
        _status.AutoSize = true;
        _status.MaximumSize = new Size(textWidth, 0);
        _status.TextAlign = ContentAlignment.MiddleCenter;

        _note.Font = new Font(Font.FontFamily, 9f);
        _note.ForeColor = Muted;
        _note.AutoSize = true;
        _note.MaximumSize = new Size(textWidth, 0);
        _note.TextAlign = ContentAlignment.MiddleCenter;
        _note.Visible = false;

        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 25;
        _progress.Size = new Size(Math.Min(320, textWidth), 4);
        _progress.ForeColor = Accent;

        _detail.Multiline = true;
        _detail.ReadOnly = true;
        _detail.ScrollBars = ScrollBars.Vertical;
        _detail.BorderStyle = BorderStyle.FixedSingle;
        _detail.BackColor = _lightTheme ? Color.White : Color.FromArgb(0x11, 0x1A, 0x2B);
        _detail.ForeColor = Muted;
        _detail.Font = new Font(FontFamily.GenericMonospace, 8.5f);
        _detail.Size = new Size(textWidth + 80, 200);
        _detail.Visible = false;

        _openButton.Text = "Open FSOps again";
        _addWindowButton.Text = "Add the FSOps window";
        _retryButton.Text = "Try again";
        _copyButton.Text = "Copy details";
        _closeButton.Text = "Close FSOps";
        foreach (var button in AllButtons())
        {
            button.AutoSize = true;
            button.Padding = new Padding(10, 4, 10, 4);
            button.Margin = new Padding(4, 0, 4, 0);
            button.FlatStyle = FlatStyle.System;
        }

        _openButton.Click += (_, _) => OpenInBrowser();
        _addWindowButton.Click += (_, _) => Program.OpenInDefaultBrowser(new Uri(WebView2Runtime.EvergreenBootstrapperUrl));
        _retryButton.Click += async (_, _) => await StartAsync().ConfigureAwait(true);
        _copyButton.Click += (_, _) => CopyDetails();
        _closeButton.Click += (_, _) => Close();

        _buttons.AutoSize = true;
        _buttons.FlowDirection = FlowDirection.LeftToRight;
        _buttons.WrapContents = false;
        _buttons.Visible = false;
        _buttons.Controls.AddRange([.. AllButtons()]);

        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.None,
            BackColor = Color.Transparent,
        };
        foreach (Control child in new Control[] { _mark, _headline, _status, _note, _progress, _detail, _buttons })
        {
            child.Margin = new Padding(0, 0, 0, 14);
            child.Anchor = AnchorStyles.None;
            stack.Controls.Add(child);
        }

        _overlay.Controls.Add(stack);
        _overlay.Resize += (_, _) => CentreStack(stack);
        stack.Resize += (_, _) => CentreStack(stack);
        Controls.Add(_overlay);
        _overlay.BringToFront();
        CentreStack(stack);
    }

    private Button[] AllButtons() => [_openButton, _addWindowButton, _retryButton, _copyButton, _closeButton];

    private void CentreStack(Control stack) =>
        stack.Location = new Point(
            Math.Max(0, (_overlay.ClientSize.Width - stack.Width) / 2),
            Math.Max(0, (_overlay.ClientSize.Height - stack.Height) / 2));

    private void ApplySavedPlacement()
    {
        var saved = WindowPlacementStore.Load();
        if (saved is null)
        {
            return;
        }

        var screens = Screen.AllScreens.Select(s => s.WorkingArea);
        if (saved.Width < MinimumSize.Width || saved.Height < MinimumSize.Height || !WindowPlacementStore.IsOnScreen(saved, screens))
        {
            return;
        }

        StartPosition = FormStartPosition.Manual;
        Location = new Point(saved.X, saved.Y);
        Size = new Size(saved.Width, saved.Height);
        if (saved.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Startup
    // -----------------------------------------------------------------------------------------

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await StartAsync().ConfigureAwait(true);
    }

    private async Task StartAsync()
    {
        ShowBusy("Starting…");

        ServerStartupResult result;
        try
        {
            var progress = new Progress<string>(ShowBusy);
            result = await _supervisor.StartAsync(progress, _startupCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ShellLog.Write("Unexpected failure while starting the server", ex);
            ShowError("FSOps could not start", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!result.Success || result.BaseAddress is null)
        {
            ShowError(result.ErrorHeadline ?? "FSOps could not start", result.ErrorDetail ?? string.Empty);
            return;
        }

        _baseAddress = result.BaseAddress;
        Text = _supervisor.Port == ServerPortPlanner.DefaultPort
            ? "FSOps"
            : $"FSOps — port {_supervisor.Port.ToString(CultureInfo.InvariantCulture)}";

        if (_mode == ShellDisplayMode.DefaultBrowser || _webView is null)
        {
            ShowBrowserMode(openNow: true);
            return;
        }

        ShowBusy("Loading FSOps…");

        try
        {
            await _webView.EnsureCoreWebView2Async().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // WebView2 reported itself as present but would not start - a repaired-but-broken
            // install, a locked user-data folder, group policy. Whatever the cause, the app itself
            // is running perfectly well on localhost, so fall through to the browser rather than
            // turning a rendering problem into "FSOps does not work".
            ShellLog.Write("WebView2 failed to initialise; falling back to the default browser", ex);
            _mode = ShellDisplayMode.DefaultBrowser;
            ShowBrowserMode(openNow: true);
            return;
        }

        _webView.CoreWebView2.Navigate(_baseAddress.ToString());
    }

    private void OnWebViewInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _webView?.CoreWebView2 is null)
        {
            ShellLog.Write($"CoreWebView2 initialisation failed: {e.InitializationException?.Message}");
            return;
        }

        var core = _webView.CoreWebView2;
        var settings = core.Settings;

        // A desktop app, not a browser: no "view page source", no status bar creeping over the UI,
        // and no autofill offering to remember anything from a local-only app.
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("FSOPS_DEVTOOLS") == "1";

        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
    }

    /// <summary>
    /// Keeps the shell pointed at FSOps. Anything else - a VATSIM link, a SimBrief link, a
    /// documentation link - goes to the user's real browser, where it has an address bar, their
    /// extensions and their session. Without this the window silently becomes a chrome-free browser,
    /// which is both a bad experience and a way to end up rendering an arbitrary remote page inside
    /// the same process as the app.
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsOwnOrigin(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        OpenExternally(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsOwnOrigin(e.Uri))
        {
            _webView?.CoreWebView2?.Navigate(e.Uri);
        }
        else
        {
            OpenExternally(e.Uri);
        }
    }

    private bool IsOwnOrigin(string uri)
    {
        if (string.IsNullOrEmpty(uri) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
               parsed.Scheme == Uri.UriSchemeHttp &&
               parsed.IsLoopback &&
               parsed.Port == _supervisor.Port;
    }

    private static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            // Never hand file:, javascript: or anything else to the shell for execution.
            return;
        }

        Program.OpenInDefaultBrowser(parsed);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            ShowApp();
            return;
        }

        // Only the very first load failing is interesting. Once the app is running, a failed
        // sub-navigation is the SPA's problem to report, not a reason to blank the window.
        if (_uiReady)
        {
            return;
        }

        ShellLog.Write($"Initial navigation failed: {e.WebErrorStatus}.");
        ShowError(
            "FSOps loaded, but its page could not be displayed",
            $"The window could not load http://localhost:{_supervisor.Port.ToString(CultureInfo.InvariantCulture)}/\n\n" +
            $"WebView2 reported: {e.WebErrorStatus}");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        ShellLog.Write($"WebView2 process failed: {e.ProcessFailedKind}.");
        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            return;
        }

        // The renderer is gone but the server is untouched, so offer the browser rather than a
        // dead end - same principle as a missing runtime.
        _uiReady = false;
        _mode = ShellDisplayMode.DefaultBrowser;
        ShowBrowserMode(openNow: false);
        _headline.Text = "FSOps' built-in window stopped";
        _status.Text = "The display component closed unexpectedly. FSOps itself is still running — open it in your browser to carry on.";
    }

    // -----------------------------------------------------------------------------------------
    // Overlay states
    // -----------------------------------------------------------------------------------------

    private void ShowBusy(string message)
    {
        _uiReady = false;
        _headline.Text = "FSOps";
        _status.Text = message;
        _status.ForeColor = Muted;
        _note.Visible = false;
        _progress.Visible = true;
        _detail.Visible = false;
        _buttons.Visible = false;
        _overlay.Visible = true;
        _overlay.BringToFront();
    }

    /// <summary>
    /// The no-WebView2 experience. Not an error, and phrased so it does not read as one: the app is
    /// running, it is open in the user's browser, and the runtime is offered as an improvement they
    /// may take rather than a gate they must pass.
    /// </summary>
    private void ShowBrowserMode(bool openNow)
    {
        _uiReady = false;
        ExitCode = 0;

        _headline.Text = "FSOps is open in your browser";
        _status.Text =
            "This PC doesn't have the Microsoft Edge WebView2 component, so FSOps opened in your " +
            "default browser instead. Everything works exactly the same.";
        _note.Text = _supervisor.Mode == ServerStartMode.StartOwnServer
            ? "Keep this window open while you're using FSOps — closing it stops the app."
            : "FSOps was already running, so you can close this window whenever you like.";
        _note.Visible = true;

        _progress.Visible = false;
        _detail.Visible = false;

        _openButton.Visible = true;
        _addWindowButton.Visible = true;
        _retryButton.Visible = false;
        _copyButton.Visible = false;
        _closeButton.Visible = true;
        _buttons.Visible = true;

        _overlay.Visible = true;
        _overlay.BringToFront();

        if (openNow)
        {
            OpenInBrowser();
        }
    }

    private void OpenInBrowser()
    {
        if (_baseAddress is null)
        {
            return;
        }

        if (Program.OpenInDefaultBrowser(_baseAddress))
        {
            ShellLog.Write($"Opened {_baseAddress} in the default browser.");
        }
        else
        {
            _note.Text = $"FSOps couldn't open a browser for you. Go to {_baseAddress} yourself.";
        }
    }

    private void ShowError(string headline, string detail)
    {
        _uiReady = false;
        _headline.Text = headline;
        _status.Text = "FSOps could not finish starting up.";
        _note.Visible = false;
        _progress.Visible = false;
        _detail.Text = detail.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        _detail.Visible = true;

        _openButton.Visible = false;
        _addWindowButton.Visible = false;
        _retryButton.Visible = true;
        _copyButton.Visible = true;
        _closeButton.Visible = true;
        _buttons.Visible = true;

        _overlay.Visible = true;
        _overlay.BringToFront();
        ExitCode = 1;
    }

    private void ShowApp()
    {
        _uiReady = true;
        ExitCode = 0;
        _overlay.Visible = false;
        _webView?.BringToFront();
        _webView?.Focus();
    }

    private void CopyDetails()
    {
        var text = $"{_headline.Text}{Environment.NewLine}{Environment.NewLine}{_detail.Text}";
        if (ShellLog.Location is { } location)
        {
            text += $"{Environment.NewLine}{Environment.NewLine}Log file: {location}";
        }

        try
        {
            Clipboard.SetText(text);
            _status.Text = "Details copied to the clipboard.";
        }
        catch (Exception ex)
        {
            ShellLog.Write("Could not write to the clipboard", ex);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Shutdown
    // -----------------------------------------------------------------------------------------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePlacement();
        _startupCancellation.Cancel();
        base.OnFormClosing(e);
    }

    private void SavePlacement()
    {
        if (_mode == ShellDisplayMode.DefaultBrowser)
        {
            // See the constructor: the companion window's size is not the user's window size.
            return;
        }

        var maximized = WindowState == FormWindowState.Maximized;
        var bounds = maximized || WindowState == FormWindowState.Minimized ? RestoreBounds : Bounds;
        WindowPlacementStore.Save(new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Order matters: tear the web view down first so it is not mid-request against a server
            // that is about to disappear, then stop the server itself.
            _webView?.Dispose();
            _supervisor.Dispose();
            _startupCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Matches the overlay to the user's Windows app theme so the moment before the SPA paints is
    /// not a white flash on a dark desktop (or the reverse). Defaults to dark, which is the app's
    /// own default theme, if the preference cannot be read.
    /// </summary>
    private static bool SystemPrefersLightTheme()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                null);
            return value is int i && i != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}

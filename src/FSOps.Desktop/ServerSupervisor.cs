using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using FSOps.Core;

namespace FSOps.Desktop;

internal sealed record ServerStartupResult(
    bool Success,
    Uri? BaseAddress,
    ServerStartMode Mode,
    string? ErrorHeadline,
    string? ErrorDetail)
{
    public static ServerStartupResult Ok(Uri baseAddress, ServerStartMode mode) =>
        new(true, baseAddress, mode, null, null);

    public static ServerStartupResult Failed(string headline, string detail) =>
        new(false, null, ServerStartMode.StartOwnServer, headline, detail);
}

/// <summary>
/// Owns FSOps.Server's lifetime for the desktop shell: picks a port, starts the server as a child
/// process inside a kill-on-close job object, waits until it actually answers before anyone
/// navigates to it, and takes it down again when the window closes.
///
/// <para><b>Why a child process rather than hosting Kestrel in-process.</b> In-process would remove
/// the orphan problem outright, and it was the first choice considered. It was rejected because the
/// server's composition root - migrations, world-data seeding, reservation reconciliation, six
/// hosted services - is a top-level-statements Program.cs that is not callable as a method, and
/// because FSOps.Server must stay independently launchable: it is how the app runs headless, how
/// the in-game panel is served when the desktop window is closed, and how every integration test
/// starts it. A child process keeps exactly one startup path in the product instead of two that can
/// drift, and the orphan risk it introduces is fully answered by <see cref="ChildProcessJob"/>,
/// which is enforced by the kernel rather than by our exit code being lucky.</para>
///
/// <para><b>Shutdown is a kill, not a graceful stop.</b> There is no way to deliver Ctrl+C to a
/// windowless child that System.Diagnostics.Process started, and adding an HTTP shutdown endpoint
/// to a server that also answers the in-game panel would be a worse trade. This is safe because of
/// how the data is written: FlightEvent and LedgerTransaction are append-only, every write goes
/// through an EF transaction, and SQLite rolls back a torn transaction on next open. The worst case
/// is losing the last few seconds of telemetry, which the next sim poll re-reads anyway.</para>
/// </summary>
internal sealed class ServerSupervisor : IDisposable
{
    /// <summary>
    /// How long to wait for the server to answer /api/v1/health. Generous on purpose: the very
    /// first launch on a new machine runs EF migrations before Kestrel starts listening, and on a
    /// slow disk with a cold .NET file cache that is not instant. World-data import is NOT included
    /// - the server backgrounds it and the UI polls its progress itself.
    /// </summary>
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(90);

    private const string StartupTimeoutVariable = "FSOPS_STARTUP_TIMEOUT_SECONDS";

    /// <summary>Last lines of the server's stdout/stderr, shown verbatim when startup fails.</summary>
    private readonly ConcurrentQueue<string> _output = new();
    private const int MaxCapturedLines = 120;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private ChildProcessJob? _job;
    private Process? _process;
    private bool _disposed;

    /// <summary>The port the UI ended up on, once <see cref="StartAsync"/> has succeeded.</summary>
    public int Port { get; private set; }

    /// <summary>Whether this instance started the server, or found one already running.</summary>
    public ServerStartMode Mode { get; private set; }

    public async Task<ServerStartupResult> StartAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var preferred = ServerPortPlanner.ResolvePreferredPort(
            Environment.GetEnvironmentVariable(ServerPortPlanner.PortVariable));

        progress?.Report("Looking for a free port…");
        var decision = ServerPortPlanner.Decide(preferred, ProbePort);

        if (decision is null)
        {
            var last = preferred + ServerPortPlanner.FallbackPortsToTry;
            ShellLog.Write($"No usable port between {preferred} and {last}.");
            return ServerStartupResult.Failed(
                "FSOps could not find a free port",
                $"Every port from {preferred} to {last} is already in use by other software.\n\n" +
                "Close whatever is using them, or set the FSOPS_PORT environment variable to a port " +
                "that is free and start FSOps again.");
        }

        Port = decision.Value.Port;
        Mode = decision.Value.Mode;
        var baseAddress = new Uri($"http://localhost:{Port.ToString(CultureInfo.InvariantCulture)}/");

        if (Mode == ServerStartMode.AttachToRunningServer)
        {
            // Someone else's server - very deliberately not ours to shut down later.
            ShellLog.Write($"Attaching to an FSOps server already running on port {Port}.");
            progress?.Report("Connecting to FSOps…");
            return ServerStartupResult.Ok(baseAddress, Mode);
        }

        var executable = ServerExecutableLocator.Locate();
        if (executable is null)
        {
            ShellLog.Write($"{ServerExecutableLocator.ExecutableName} not found from {AppContext.BaseDirectory}.");
            return ServerStartupResult.Failed(
                "FSOps is missing a file it needs",
                $"{ServerExecutableLocator.ExecutableName} could not be found next to " +
                $"{AppContext.BaseDirectory}\n\n" +
                "This usually means the installation is incomplete. Reinstalling FSOps will fix it.");
        }

        ShellLog.Write($"Starting {executable} on port {Port}.");
        progress?.Report("Starting FSOps…");

        try
        {
            StartServerProcess(executable);
        }
        catch (Exception ex)
        {
            ShellLog.Write("Failed to start the server process", ex);
            return ServerStartupResult.Failed(
                "FSOps could not start its background service",
                $"Tried to run:\n{executable}\n\n{ex.GetType().Name}: {ex.Message}");
        }

        return await WaitForServerAsync(baseAddress, progress, cancellationToken).ConfigureAwait(false);
    }

    private void StartServerProcess(string executable)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            // The server's own working directory is irrelevant - Program.cs pins its content root to
            // AppContext.BaseDirectory precisely so it does not matter - but pointing it at its own
            // folder keeps relative paths in any future logging or crash dump sane.
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // The port the shell chose, handed to the child explicitly. This is also what
        // PanelPackageInstaller.ResolveConfiguredPort() reads when the player installs or repairs
        // the in-game panel, so the panel's generated FSOpsPanel.config.js always names the port the
        // server is genuinely listening on - the shell and the panel cannot disagree.
        startInfo.Environment[ServerPortPlanner.PortVariable] = Port.ToString(CultureInfo.InvariantCulture);

        // We are the window. The server must never also open the user's browser.
        startInfo.Environment["FSOPS_NO_BROWSER"] = "1";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        process.Start();

        // Assign before anything else so the kill-on-close guarantee is in place essentially from
        // the first instruction the child runs. See ChildProcessJob.Assign on the residual race.
        _job = ChildProcessJob.Create();
        if (_job?.Assign(process) != true)
        {
            ShellLog.Write(
                "Warning: the server could not be placed in a job object; it will only be stopped on a clean exit.");
        }

        // Both streams must be drained or a chatty startup fills the pipe buffer and blocks the
        // server before it ever listens.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _process = process;
        ShellLog.Write($"Server process started, pid {process.Id}.");
    }

    private void Capture(string? line)
    {
        if (line is null)
        {
            return;
        }

        _output.Enqueue(line);
        while (_output.Count > MaxCapturedLines && _output.TryDequeue(out _))
        {
        }
    }

    private async Task<ServerStartupResult> WaitForServerAsync(
        Uri baseAddress,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var timeout = ResolveStartupTimeout();
        var deadline = DateTime.UtcNow + timeout;
        var healthUri = new Uri(baseAddress, "api/v1/health");
        var reportedSlowStart = false;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                var exitCode = _process.ExitCode;
                ShellLog.Write($"Server process exited during startup with code {exitCode}.");

                // The server knows things the shell cannot - that the database is damaged, say -
                // and it writes an explanation for the user rather than making the shell guess from
                // an exit code. Show that verbatim when it is there. See
                // FSOps.Server.Services.StartupFailureReport: the wording deliberately lives beside
                // the code that knows what went wrong, so there is no second copy here to drift.
                if (exitCode == ServerStartupErrorExitCode &&
                    ReadStartupErrorReport() is { Length: > 0 } reported)
                {
                    return ServerStartupResult.Failed("FSOps could not start", reported);
                }

                return ServerStartupResult.Failed(
                    "FSOps' background service stopped unexpectedly",
                    $"It exited with code {exitCode} before it finished starting.\n\n" +
                    CapturedOutputOrPlaceholder());
            }

            if (await IsHealthyAsync(healthUri, cancellationToken).ConfigureAwait(false))
            {
                ShellLog.Write($"Server healthy on port {Port}.");
                return ServerStartupResult.Ok(baseAddress, Mode);
            }

            if (!reportedSlowStart && DateTime.UtcNow > deadline - timeout + TimeSpan.FromSeconds(8))
            {
                reportedSlowStart = true;
                progress?.Report("Still starting — the first run sets up the database…");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        ShellLog.Write($"Server did not answer on port {Port} within {timeout.TotalSeconds:0} seconds.");
        return ServerStartupResult.Failed(
            "FSOps' background service did not start in time",
            $"It was still not answering on port {Port} after {timeout.TotalSeconds:0} seconds.\n\n" +
            CapturedOutputOrPlaceholder());
    }

    /// <summary>
    /// The server's "I could not start, and I have written down why" exit code, and the file it
    /// writes. Mirrored from <c>FSOps.Server.Services.StartupFailureReport</c> rather than
    /// referenced: the shell is a window and a process supervisor, and must not take a dependency
    /// on the server assembly to read two constants. Only the contract is duplicated - never the
    /// message, which is the part that would actually rot if it were copied.
    /// </summary>
    internal const int ServerStartupErrorExitCode = 3;

    internal const string ServerStartupErrorFileName = "startup-error.txt";

    /// <summary>
    /// Reads the server's explanation, or null if there is not a usable one. Best-effort
    /// throughout: this runs while the shell is already reporting a failure, and a missing or
    /// unreadable file must degrade to the generic exit-code message rather than throw on top of
    /// the problem it is trying to explain.
    /// </summary>
    private static string? ReadStartupErrorReport() => ReadStartupErrorReport(AppPaths.DataDirectory);

    /// <summary>
    /// Takes the directory as an argument so this can be tested without touching
    /// <see cref="AppPaths.DataDirectory"/>, which caches its answer for the life of the process
    /// and so cannot be redirected once anything has read it.
    /// </summary>
    internal static string? ReadStartupErrorReport(string dataDirectory)
    {
        try
        {
            var path = System.IO.Path.Combine(dataDirectory, ServerStartupErrorFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TimeSpan ResolveStartupTimeout()
    {
        var configured = Environment.GetEnvironmentVariable(StartupTimeoutVariable);
        if (int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is > 0 and <= 600)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return DefaultStartupTimeout;
    }

    private string CapturedOutputOrPlaceholder()
    {
        var lines = _output.ToArray();
        if (lines.Length == 0)
        {
            return ShellLog.Location is { } location
                ? $"The service produced no output. There may be more detail in:\n{location}"
                : "The service produced no output.";
        }

        var builder = new StringBuilder("Last output from the service:\n\n");
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private async Task<bool> IsHealthyAsync(Uri healthUri, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(healthUri, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------------------------
    // Port probing
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Classifies a port for <see cref="ServerPortPlanner"/>. Checks the OS listener table first so
    /// a free port costs no HTTP round trip at all - only a port that is genuinely occupied is worth
    /// asking "are you FSOps?".
    /// </summary>
    private PortState ProbePort(int port)
    {
        if (!IsSomethingListening(port))
        {
            return PortState.Free;
        }

        return LooksLikeFsOps(port) ? PortState.FsOpsServer : PortState.SomethingElse;
    }

    private static bool IsSomethingListening(int port)
    {
        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Port == port)
                {
                    return true;
                }
            }
        }
        catch (NetworkInformationException)
        {
            // If the listener table cannot be read, fall back to trying to bind the port ourselves.
            return !CanBind(port);
        }

        return false;
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the occupant of a port whether it is FSOps. The health endpoint's <c>serverTimeUtc</c>
    /// field is the marker: an unauthenticated GET to /api/v1/health returning JSON with that field
    /// is specific enough that no unrelated service on a developer's machine will be mistaken for
    /// the app, and checking the body rather than the status code matters because almost anything
    /// can return a 200.
    /// </summary>
    private bool LooksLikeFsOps(int port)
    {
        try
        {
            var uri = new Uri($"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}/api/v1/health");
            using var response = _http.GetAsync(uri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("serverTimeUtc", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();

        // ------------------------------------------------------------------------------------
        // DO NOT "TIDY UP" THIS ASYMMETRY. It is the whole point.
        //
        // In AttachToRunningServer mode this shell did not start the server, so it does not get to
        // stop it. That server belongs to whoever launched it - very often the owner's own copy of
        // FSOps, open in a browser tab while they are mid-flight. Closing the desktop window must
        // not take their app down with it.
        //
        // The general rule is: only ever stop the process you started, by its own handle - never by
        // name, and never by whatever happens to hold a port. That rule exists because it has been
        // broken repeatedly during this project's development, each time taking down a server
        // somebody was mid-way through using. Making shutdown "consistent" by always killing
        // whatever is on the port would reintroduce exactly that bug, with the user's live session
        // as the casualty.
        //
        // _job is still disposed here: in attach mode nothing was ever assigned to it, so this only
        // releases a handle.
        // ------------------------------------------------------------------------------------
        if (Mode == ServerStartMode.AttachToRunningServer || _process is null)
        {
            _job?.Dispose();
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                ShellLog.Write($"Stopping server process {_process.Id}.");
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            ShellLog.Write("Could not stop the server process directly; the job object will", ex);
        }
        finally
        {
            // Belt and braces: even if Kill failed, closing the job's last handle terminates it.
            _job?.Dispose();
            _process.Dispose();
            _process = null;
        }
    }
}

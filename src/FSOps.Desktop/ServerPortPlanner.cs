using System.Globalization;

namespace FSOps.Desktop;

/// <summary>What is currently sitting on a local TCP port.</summary>
internal enum PortState
{
    /// <summary>Nothing is listening - we can bind it.</summary>
    Free,

    /// <summary>Something is listening and it answered FSOps' health endpoint.</summary>
    FsOpsServer,

    /// <summary>Something is listening but it is not FSOps.</summary>
    SomethingElse,
}

/// <summary>Whether the shell needs to start a server or can use one that is already running.</summary>
internal enum ServerStartMode
{
    StartOwnServer,
    AttachToRunningServer,
}

internal readonly record struct ServerPortDecision(int Port, ServerStartMode Mode);

/// <summary>
/// Decides which port the shell's UI should talk to, and whether it must start a server to get one.
/// Pure: every observation of the outside world arrives through the <c>probe</c> delegate, so the
/// whole decision table is unit-testable without opening a socket.
///
/// <para>
/// Three cases matter, and the difference between them is the whole point of this class:
/// </para>
/// <list type="bullet">
/// <item>The port is free - start our own server there. The overwhelmingly common case.</item>
/// <item>FSOps is already answering on it - the owner has a copy running (from a shortcut, or
/// still open in a browser tab). Attach to it. There is exactly one SQLite database per user, and
/// pointing a second server at a file the first one already has open is a good way to turn a
/// double-click into a corrupted ledger. Attaching also means the shell must NOT kill that server
/// when its window closes: it did not start it.</item>
/// <item>Something that is not FSOps holds the port - step to the next free one rather than
/// refusing to launch. The shell hands the chosen port to the server it starts as FSOPS_PORT, so
/// the two cannot disagree about a port chosen here.</item>
/// </list>
/// </summary>
internal static class ServerPortPlanner
{
    /// <summary>The documented default - identical to FSOps.Server's own fallback.</summary>
    public const int DefaultPort = 5977;

    /// <summary>Environment variable FSOps.Server reads to choose its Kestrel binding.</summary>
    public const string PortVariable = "FSOPS_PORT";

    /// <summary>
    /// How far above the preferred port to look before giving up. Deliberately small: wandering
    /// hundreds of ports away from 5977 would make the address unrecognisable to anyone who has
    /// read the docs, and if twenty consecutive ports are all taken by non-FSOps listeners, the
    /// honest answer is an error message, not a lucky guess.
    /// </summary>
    public const int FallbackPortsToTry = 20;

    /// <summary>
    /// Reads a preferred port from an FSOPS_PORT-shaped string, falling back to
    /// <see cref="DefaultPort"/> for anything absent or unusable. Never throws: a typo in an
    /// environment variable must not stop the app from starting.
    /// </summary>
    public static int ResolvePreferredPort(string? configuredPort)
    {
        if (int.TryParse(configuredPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            port is > 0 and <= 65535)
        {
            return port;
        }

        return DefaultPort;
    }

    /// <summary>
    /// Walks upwards from <paramref name="preferredPort"/> and returns the first port that is
    /// either free or already serving FSOps. Returns <c>null</c> when the whole search range is
    /// occupied by other software, which the caller must surface as a real error rather than
    /// silently binding something unexpected.
    /// </summary>
    public static ServerPortDecision? Decide(
        int preferredPort,
        Func<int, PortState> probe,
        int fallbackPortsToTry = FallbackPortsToTry)
    {
        ArgumentNullException.ThrowIfNull(probe);

        for (var offset = 0; offset <= fallbackPortsToTry; offset++)
        {
            var port = preferredPort + offset;
            if (port > 65535)
            {
                break;
            }

            switch (probe(port))
            {
                case PortState.FsOpsServer:
                    return new ServerPortDecision(port, ServerStartMode.AttachToRunningServer);
                case PortState.Free:
                    return new ServerPortDecision(port, ServerStartMode.StartOwnServer);
            }
        }

        return null;
    }
}

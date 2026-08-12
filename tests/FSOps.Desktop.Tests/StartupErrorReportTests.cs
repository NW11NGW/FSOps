namespace FSOps.Desktop.Tests;

/// <summary>
/// When the server cannot start it writes an explanation for the user and exits with a distinct
/// code; the shell shows that file verbatim instead of "It exited with code N", which told nobody
/// anything. The wording lives in the server, beside the code that knows what went wrong - the
/// shell only knows the exit code and the file name.
///
/// <para>Those two constants are the one thing duplicated across the boundary, because the shell is
/// a window and a process supervisor and must not reference the server assembly. That duplication
/// is only safe if both sides are pinned, so these tests assert the shell's half and
/// <c>StartupFailureReportContractTests</c> in FSOps.Server.Tests asserts the server's. Change
/// either and the other's test fails, which is the point.</para>
/// </summary>
public class StartupErrorReportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"fsops-shell-report-{Guid.NewGuid():N}");

    public StartupErrorReportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TheShellAndServerMustAgreeOnTheContract()
    {
        // Mirrors FSOps.Server.Services.StartupFailureReport.ExitCode / .FileName.
        Assert.Equal(3, ServerSupervisor.ServerStartupErrorExitCode);
        Assert.Equal("startup-error.txt", ServerSupervisor.ServerStartupErrorFileName);
    }

    [Fact]
    public void AReportWrittenByTheServerIsReturnedVerbatim()
    {
        var message = "FSOps cannot open its database.\n\n  C:\\somewhere\\fsops.db\n\nCopy it somewhere safe.";
        File.WriteAllText(Path.Combine(_directory, "startup-error.txt"), message);

        var read = ServerSupervisor.ReadStartupErrorReport(_directory);

        Assert.Equal(message, read);
    }

    [Fact]
    public void NoReportMeansTheShellFallsBackToItsGenericMessage()
    {
        // Null is what tells the caller to use the exit-code wording instead.
        Assert.Null(ServerSupervisor.ReadStartupErrorReport(_directory));
    }

    [Fact]
    public void AnEmptyOrWhitespaceReportIsTreatedAsNoReport()
    {
        var path = Path.Combine(_directory, "startup-error.txt");

        File.WriteAllText(path, string.Empty);
        Assert.Null(ServerSupervisor.ReadStartupErrorReport(_directory));

        File.WriteAllText(path, "   \r\n  \t ");
        Assert.Null(ServerSupervisor.ReadStartupErrorReport(_directory));
    }

    [Fact]
    public void AnUnreadableDirectoryDegradesQuietlyRatherThanThrowing()
    {
        // This runs while the shell is already reporting a failure. Throwing here would replace a
        // clear problem with a confusing one.
        Assert.Null(ServerSupervisor.ReadStartupErrorReport(Path.Combine(_directory, "does", "not", "exist")));
        Assert.Null(ServerSupervisor.ReadStartupErrorReport("\0invalid"));
    }
}
